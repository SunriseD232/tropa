using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Tropa.Core.Model;
using Tropa.Core.Security;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Diagnostics;
using Tropa.Infrastructure.Testing;

namespace Tropa.Integration.Tests;

/// <summary>
/// DNS-запросы диагностики через временное ядро: UDP через SOCKS5 UDP ASSOCIATE и TCP через CONNECT.
/// Отвечает локальный DNS-сервер на 127.0.0.1 — интернет не нужен.
/// </summary>
public sealed class DiagnosticsDnsTests : IAsyncDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-diag-");
    private readonly CancellationTokenSource _stop = new();
    private readonly UdpClient _udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly TcpListener _tcp = new(IPAddress.Loopback, 0);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static CoreLocations Cores => new(Path.Combine(RepoRoot(), "cores"), Path.Combine(RepoRoot(), "geo"));

    public DiagnosticsDnsTests()
    {
        _tcp.Start();
        _ = Task.Run(UdpLoopAsync);
        _ = Task.Run(TcpLoopAsync);
    }

    private int UdpPort => ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;

    private int TcpPort => ((IPEndPoint)_tcp.LocalEndpoint).Port;

    /// <summary>Ответ: blocked.test — «нет такого домена», остальное — 93.184.216.34.</summary>
    private static byte[] Answer(byte[] query)
    {
        var nx = System.Text.Encoding.ASCII.GetString(query).Contains("blocked", StringComparison.Ordinal);
        var r = new List<byte>(query);
        r[2] = 0x81;
        r[3] = (byte)(nx ? 0x83 : 0x80);
        if (!nx)
        {
            r[7] = 1;
            r.AddRange([0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 93, 184, 216, 34]);
        }

        return [.. r];
    }

    private async Task UdpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var q = await _udp.ReceiveAsync(_stop.Token);
                await _udp.SendAsync(Answer(q.Buffer), q.RemoteEndPoint, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task TcpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _tcp.AcceptTcpClientAsync(_stop.Token);
                var s = client.GetStream();
                var len = new byte[2];
                await s.ReadExactlyAsync(len, _stop.Token);
                var q = new byte[BinaryPrimitives.ReadUInt16BigEndian(len)];
                await s.ReadExactlyAsync(q, _stop.Token);
                await s.WriteAsync(Core.Diagnostics.DnsMessage.WithTcpLength(Answer(q)), _stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    [Fact]
    public async Task Dns_queries_go_through_probe_core()
    {
        if (!File.Exists(Path.Combine(Cores.CoresDirectory, "sing-box.exe")))
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        var run = Path.Combine(_work.FullName, "run");
        Directory.CreateDirectory(run);

        var (udp, tcp, nx) = await ServerTester.ProbeAsync(null, [], new AppSettings(), Cores, run, SecretScrubber.PatternsOnly, _ => { },
            async (ports, token) =>
            {
                Assert.Null(ports.ServerPort);
                var u = await DiagnosticsRunner.QueryUdpAsync(ports.DirectPort, ports.Auth, IPAddress.Loopback, "discord.com", token, UdpPort);
                var t = await DiagnosticsRunner.QueryTcpAsync(ports.DirectPort, ports.Auth, "127.0.0.1", "discord.com", token, TcpPort);
                var n = await DiagnosticsRunner.QueryUdpAsync(ports.DirectPort, ports.Auth, IPAddress.Loopback, "blocked.test", token, UdpPort);
                return (u, t, n);
            }, ct, bindPhysical: false);

        Assert.Equal("93.184.216.34", Assert.Single(udp!.Addresses).ToString());
        Assert.Equal("93.184.216.34", Assert.Single(tcp!.Addresses).ToString());
        Assert.True(nx!.IsNxDomain);
        Assert.Equal(Core.Diagnostics.DnsVerdict.Spoofed, Core.Diagnostics.Diagnosis.CompareDns(nx, tcp));
        // Конфиг временного ядра с паролем не остался на диске.
        Assert.Empty(Directory.GetFiles(run, "config-*.json"));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _udp.Dispose();
        _tcp.Stop();
        _tcp.Dispose();
        _stop.Dispose();
        try
        {
            _work.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
