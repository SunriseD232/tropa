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

    // Журнал 2026-11-24: рядом работал Hiddify со своим TUN на том же 172.19.0.1 (ADR-070).
    [Theory]
    [InlineData("FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: set ipv4 address: The object already exists.", true)]
    [InlineData("FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: set ipv6 address: The object already exists.", true)]
    [InlineData("FATAL[0015] start service: start inbound/tun[tun-in]: configure tun interface: (create adapter: Cannot create a file when that file already exists. | open existing adapter: Element not found.)", false)]
    [InlineData("FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: Access is denied.", false)]
    public void Tun_address_conflict_is_recognized(string message, bool expected)
    {
        Assert.Equal(expected, CoreSupervisor.IsTunAddressConflict(message));
        if (expected)
            Assert.False(CoreSupervisor.IsStaleTunAdapter(message));
    }

    [Fact]
    public void Tun_address_is_replaced_only_in_tun_inbound()
    {
        const string json = """{"inbounds":[{"type":"tun","tag":"tun-in","address":["172.19.0.1/30","fdfe:dcba:9876::1/126"]},{"type":"mixed","tag":"mixed-in"}],"outbounds":[]}""";
        var result = System.Text.Json.Nodes.JsonNode.Parse(CoreSupervisor.WithTunAddress(json, Tropa.Core.Diagnostics.Diagnosis.OwnTunCidrsAlt))!;
        var address = result["inbounds"]![0]!["address"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(["172.23.233.1/30", "fdfe:dcba:9877::1/126"], address);
        Assert.Null(result["inbounds"]![1]!["address"]);
        Assert.True(Tropa.Core.Diagnostics.Diagnosis.IsOwnTunAddress(Tropa.Core.Diagnostics.Diagnosis.OwnTunAddressAlt));
        Assert.True(Tropa.Core.Diagnostics.Diagnosis.IsOwnTunAddress(IPAddress.Parse("172.19.0.1")));
        Assert.False(Tropa.Core.Diagnostics.Diagnosis.IsOwnTunAddress(IPAddress.Parse("172.19.0.2")));
    }

    // Журнал 2026-11-24: 24 минуты без шлюза с «Подключено» (ADR-071).
    [Fact]
    public void No_network_note_is_added_once_and_removed_keeping_other_notes()
    {
        const string foreign = "Работает другой VPN или клиент обхода (wireguard).";
        var withNote = Tropa.Infrastructure.TropaEngine.WithNoNetworkNote(foreign);
        Assert.StartsWith(Tropa.Infrastructure.TropaEngine.NoNetworkNote, withNote, StringComparison.Ordinal);
        Assert.Equal(withNote, Tropa.Infrastructure.TropaEngine.WithNoNetworkNote(withNote));
        Assert.Equal(foreign, Tropa.Infrastructure.TropaEngine.WithoutNoNetworkNote(withNote));
        Assert.Null(Tropa.Infrastructure.TropaEngine.WithoutNoNetworkNote(Tropa.Infrastructure.TropaEngine.WithNoNetworkNote(null)));
    }

    [Fact]
    public void Tun_names_alternate()
    {
        Assert.Equal("Tropa-2", CoreSupervisor.OtherTunName("Tropa"));
        Assert.Equal("Tropa", CoreSupervisor.OtherTunName("Tropa-2"));
        Assert.Equal("Tropa-2", CoreSupervisor.OtherTunName(null));
        Assert.True(Tropa.Core.Diagnostics.Diagnosis.IsOwnTunName("tropa-2"));
        Assert.False(Tropa.Core.Diagnostics.Diagnosis.IsOwnTunName("Tropa-3"));
    }

    [Fact]
    public void Tun_name_is_replaced_only_in_tun_inbound()
    {
        const string json = """{"inbounds":[{"type":"tun","tag":"tun-in","interface_name":"Tropa"},{"type":"mixed","tag":"mixed-in","listen":"127.0.0.1"}],"outbounds":[]}""";
        var result = System.Text.Json.Nodes.JsonNode.Parse(CoreSupervisor.WithTunName(json, "Tropa-2"))!;
        Assert.Equal("Tropa-2", (string?)result["inbounds"]![0]!["interface_name"]);
        Assert.Null(result["inbounds"]![1]!["interface_name"]);
        const string noTun = """{"inbounds":[{"type":"mixed"}]}""";
        Assert.Equal(noTun, CoreSupervisor.WithTunName(noTun, "Tropa-2"));
    }

    [Theory]
    // Журнал 2026-11-07: порт временного ядра проверки попал под запрет Windows.
    [InlineData("Ядро sing-box завершилось при запуске: FATAL[0000] start service: start inbound/socks[test-direct]: listen tcp 127.0.0.1:50123: bind: An attempt was made to access a socket in a way forbidden by its access permissions.", true)]
    [InlineData("Ядро sing-box завершилось при запуске: FATAL[0000] start service: start inbound/socks[t1]: listen tcp 127.0.0.1:50123: bind: Only one usage of each socket address (protocol/network address/port) is normally permitted.", true)]
    [InlineData("Ядро sing-box завершилось при запуске: FATAL[0000] decode config: unknown field", false)]
    public void Port_bind_error_is_recognized(string message, bool expected) =>
        Assert.Equal(expected, Tropa.Infrastructure.Testing.ServerTester.IsPortBindError(message));
}
