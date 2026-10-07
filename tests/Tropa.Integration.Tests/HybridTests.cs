using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Tropa.Core.Model;
using Tropa.Core.Testing;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.Testing;
using Tropa.Service;

namespace Tropa.Integration.Tests;

/// <summary>
/// Гибрид sing-box → Xray на настоящем Xray-сервере VLESS + XHTTP (127.0.0.1). Сервер отправляет
/// весь трафик на локальный «сайт» (freedom redirect), поэтому запрос к любому домену доходит до
/// «сайта» только через XHTTP — без DNS и интернета. Так проверяется и схема конфига Xray,
/// которую «xray -test» не проверяет.
/// </summary>
public sealed class HybridTests : IAsyncLifetime
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-hybrid-");
    private Process? _xrayServer;
    private TcpListener? _site;
    private int _xrayPort;
    private int _sitePort;
    private int _hits;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static CoreLocations Cores => new(Path.Combine(RepoRoot(), "cores"), Path.Combine(RepoRoot(), "geo"));

    private static bool Ready => File.Exists(Path.Combine(Cores.CoresDirectory, "xray.exe")) && File.Exists(Path.Combine(Cores.CoresDirectory, "sing-box.exe"));

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public async ValueTask InitializeAsync()
    {
        if (!Ready)
            return;
        // Простейший HTTP-ответчик: HttpListener без прав администратора не принимает чужой Host (tropa.test).
        _site = new TcpListener(IPAddress.Loopback, 0);
        _site.Start();
        _sitePort = ((IPEndPoint)_site.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _site.AcceptTcpClientAsync();
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        });

        _xrayPort = FreePort();
        var cfg = Path.Combine(_work.FullName, "xray-server.json");
        await File.WriteAllTextAsync(cfg, $$"""
        {
          "log": { "loglevel": "warning" },
          "inbounds": [ { "listen": "127.0.0.1", "port": {{_xrayPort}}, "protocol": "vless",
            "settings": { "clients": [ { "id": "{{Uuid}}" } ], "decryption": "none" },
            "streamSettings": { "network": "xhttp", "xhttpSettings": { "path": "/x" } } } ],
          "outbounds": [ { "protocol": "freedom", "settings": { "redirect": "127.0.0.1:{{_sitePort}}", "finalRules": [ { "action": "allow", "ip": [ "127.0.0.1" ] } ] } } ]
        }
        """);
        var psi = new ProcessStartInfo(Path.Combine(Cores.CoresDirectory, "xray.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "run", "-c", cfg })
            psi.ArgumentList.Add(a);
        _xrayServer = Process.Start(psi);
        await Task.Delay(1500);
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buffer = new byte[8192];
                var head = "";
                while (!head.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await stream.ReadAsync(buffer);
                    if (n == 0)
                        return;
                    head += System.Text.Encoding.ASCII.GetString(buffer, 0, n);
                }

                Interlocked.Increment(ref _hits);
                var path = head.Split(' ')[1];
                if (path.EndsWith("/big", StringComparison.Ordinal))
                {
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(new byte[1_000_000]);
                }
                else
                {
                    await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"));
                }
            }
            catch (IOException)
            {
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_xrayServer is { HasExited: false })
            _xrayServer.Kill();
        _xrayServer?.Dispose();
        _site?.Stop();
        try
        {
            _work.Delete(recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }

    private string XhttpLink => $"vless://{Uuid}@127.0.0.1:{_xrayPort}?type=xhttp&path=%2Fx&security=none#XHTTP";

    private static AppSettings Settings(int port) => new()
    {
        Connection = new ConnectionSettings
        {
            SocksPort = port,
            AutoSelect = false,
            TestUrl = "http://tropa.test/generate_204",
            SpeedUrl = "http://tropa.test/big",
            UdpTestOn = false,
        },
        Routing = new RoutingSettings { Preset = RoutePreset.All },
    };

    private TropaEngine Engine(string name, Func<CancellationToken, Task<ServiceClient?>>? service = null) =>
        TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, name, "r"), Path.Combine(_work.FullName, name, "l")),
            new FakeProxyStore(), Cores, connectService: service);

    private static async Task<HttpStatusCode> GetThroughProxy(int port, string url, CancellationToken ct)
    {
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(url, ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Xhttp_server_is_tested_through_temporary_xray()
    {
        if (!Ready)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        await using var engine = Engine("t");
        engine.UpdateSettings(_ => Settings(FreePort()));
        await engine.ImportTextAsync(XhttpLink, null, ct);
        var p = engine.State.Profiles.Single().Profile;

        var r = (await engine.TestAsync([p], TestKinds.Tcp | TestKinds.Delay | TestKinds.Speed, ct: ct))[p.Id];
        Assert.True(r.DelayMs is >= 0, r.Error);
        Assert.True(r.SpeedMbps is > 0, r.Error);
        Assert.Equal(HealthStatus.Working, ServerHealth.Classify(r));
    }

    [Fact]
    public async Task Engine_connects_xhttp_server_via_hybrid()
    {
        if (!Ready)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        await using var engine = Engine("e");
        var port = FreePort();
        engine.UpdateSettings(_ => Settings(port));
        await engine.ImportTextAsync(XhttpLink, null, ct);

        await engine.ConnectAsync(ct);
        Assert.True(engine.Status.State == ConnectionState.Connected, engine.Status.Message);

        var before = _hits;
        Assert.Equal(HttpStatusCode.NoContent, await GetThroughProxy(port, "http://tropa.test/hello", ct));
        Assert.True(_hits > before, "запрос должен был дойти до «сайта» через Xray и XHTTP");

        await engine.DisconnectAsync();
        // Оба ядра остановлены: порт закрыт.
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => GetThroughProxy(port, "http://tropa.test/hello", ct));
    }

    [Fact]
    public async Task Service_runs_hybrid_pair()
    {
        if (!Ready)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        var pipe = "Tropa.Test." + Guid.NewGuid().ToString("N");
        using var server = new PipeServer(new ServiceOptions
        {
            PipeName = pipe,
            DataDirectory = Path.Combine(_work.FullName, "svc"),
            Cores = Cores,
            ClientVerifier = new ExecutablePathVerifier(Environment.ProcessPath!),
            Privileged = false,
        }, NullLogger<PipeServer>.Instance);
        await server.StartAsync(ct);
        try
        {
            await using var engine = Engine("s", c => ServiceClient.TryConnectAsync(pipe, Environment.ProcessPath!, TimeSpan.FromSeconds(3), c));
            var port = FreePort();
            engine.UpdateSettings(_ => Settings(port));
            await engine.ImportTextAsync(XhttpLink, null, ct);
            await engine.ConnectAsync(ct);
            Assert.True(engine.Status.State == ConnectionState.Connected, engine.Status.Message);
            Assert.True(engine.ServiceAvailable);

            var before = _hits;
            Assert.Equal(HttpStatusCode.NoContent, await GetThroughProxy(port, "http://tropa.test/svc", ct));
            Assert.True(_hits > before);
            await engine.DisconnectAsync();
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Sing_box_only_choice_refuses_xhttp_clearly()
    {
        if (!Ready)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        await using var engine = Engine("n");
        var s = Settings(FreePort());
        engine.UpdateSettings(_ => s with { Cores = s.Cores with { CoreChoice = CoreChoice.SingBox } });
        await engine.ImportTextAsync(XhttpLink, null, ct);
        await engine.ConnectAsync(ct);
        Assert.Equal(ConnectionState.Error, engine.Status.State);
        Assert.Contains("Автоматически", engine.Status.Message, StringComparison.Ordinal);
    }
}
