using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tropa.Infrastructure.Diagnostics;
using Tropa.Service;

namespace Tropa.Integration.Tests;

/// <summary>Разбор аномалий журнала разработчика (2026-11-01): 407 на входе Тропы и зависший адаптер TUN.</summary>
public sealed class LogAnomalyTests
{
    [Fact]
    public async Task Loopback_client_of_a_port_is_found_by_process_name()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        using var accepted = await listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken);

        using var self = Process.GetCurrentProcess();
        Assert.Contains(self.ProcessName, TcpOwners.LoopbackClients(port));
        // По порту клиента из строки журнала — тоже.
        Assert.Contains(self.ProcessName, TcpOwners.LoopbackClients(1, ((IPEndPoint)client.Client.LocalEndPoint!).Port));
    }

    [Fact]
    public void Nobody_connected_means_empty_list()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Assert.Empty(TcpOwners.LoopbackClients(((IPEndPoint)listener.LocalEndpoint).Port));
    }

    [Theory]
    [InlineData("FATAL[0015] start service: start inbound/tun[tun-in]: configure tun interface: (create adapter: Cannot create a file when that file already exists. | open existing adapter: Element not found.)", true)]
    [InlineData("FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: Access is denied.", false)]
    [InlineData("Ядро sing-box не запустилось за 20 с: процесс работает, но локальный порт не открылся (ядро ничего не вывело).", false)]
    public void Stale_tun_adapter_is_recognized(string message, bool expected) =>
        Assert.Equal(expected, CoreSupervisor.IsStaleTunAdapter(message));
}
