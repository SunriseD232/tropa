using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Core.Security;
using Tropa.Core.Testing;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Testing;

namespace Tropa.Integration.Tests;

/// <summary>
/// Тесты скорости, «заморозки» и UDP без интернета: VLESS-сервер sing-box на 127.0.0.1,
/// локальный «сайт» (быстрый файл и файл, который замолкает после 16 КБ — как при ограничении ТСПУ)
/// и локальный STUN-ответчик.
/// </summary>
public sealed class ServerTesterTests : IAsyncLifetime
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private const string Uuid2 = "0f4a7a1e-2b6c-4e8f-9a3d-5c1b7e2f8d90";
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-tester-");
    private readonly CancellationTokenSource _stop = new();
    private Process? _server;
    private HttpListener? _site;
    private UdpClient? _stun;
    private int _serverPort;
    private int _sitePort;
    private int _stunPort;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static CoreLocations Cores => new(Path.Combine(RepoRoot(), "cores"), Path.Combine(RepoRoot(), "geo"));

    private static int FreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public async ValueTask InitializeAsync()
    {
        if (!File.Exists(Path.Combine(Cores.CoresDirectory, "sing-box.exe")))
            return;

        _sitePort = FreeTcpPort();
        _site = new HttpListener();
        _site.Prefixes.Add($"http://127.0.0.1:{_sitePort}/");
        _site.Start();
        _ = Task.Run(SiteLoopAsync);

        _stun = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _stunPort = ((IPEndPoint)_stun.Client.LocalEndPoint!).Port;
        _ = Task.Run(StunLoopAsync);

        _serverPort = FreeTcpPort();
        var cfg = Path.Combine(_work.FullName, "server.json");
        await File.WriteAllTextAsync(cfg, $$"""
        { "log": { "level": "warn" },
          "inbounds": [ { "type": "vless", "listen": "127.0.0.1", "listen_port": {{_serverPort}}, "users": [ { "uuid": "{{Uuid}}" }, { "uuid": "{{Uuid2}}" } ] } ],
          "outbounds": [ { "type": "direct" } ] }
        """);
        var psi = new ProcessStartInfo(Path.Combine(Cores.CoresDirectory, "sing-box.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "run", "-c", cfg })
            psi.ArgumentList.Add(a);
        _server = Process.Start(psi);
        await Task.Delay(1000);
    }

    private async Task SiteLoopAsync()
    {
        while (_site is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _site.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var path = ctx.Request.Url!.AbsolutePath;
                    if (path == "/big")
                    {
                        var size = long.Parse(ctx.Request.QueryString["bytes"] ?? "1000000", CultureInfo.InvariantCulture);
                        ctx.Response.ContentLength64 = size;
                        var chunk = new byte[64 * 1024];
                        for (long sent = 0; sent < size; sent += chunk.Length)
                            await ctx.Response.OutputStream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, size - sent)));
                    }
                    else if (path == "/freeze")
                    {
                        // Как ТСПУ: первые 16 КБ проходят, дальше тишина при открытом соединении.
                        ctx.Response.SendChunked = true;
                        await ctx.Response.OutputStream.WriteAsync(new byte[16 * 1024]);
                        await ctx.Response.OutputStream.FlushAsync();
                        await Task.Delay(TimeSpan.FromSeconds(60), _stop.Token);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 204;
                    }
                }
                catch (Exception ex) when (ex is HttpListenerException or OperationCanceledException or ObjectDisposedException)
                {
                }
                finally
                {
                    try
                    {
                        ctx.Response.Close();
                    }
                    catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                    {
                    }
                }
            });
        }
    }

    /// <summary>Отвечает на STUN Binding Request успешным ответом с тем же номером транзакции.</summary>
    private async Task StunLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult req;
            try
            {
                req = await _stun!.ReceiveAsync(_stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (req.Buffer.Length < 20)
                continue;
            var resp = new byte[20];
            BinaryPrimitives.WriteUInt16BigEndian(resp, 0x0101);
            req.Buffer.AsSpan(4, 16).CopyTo(resp.AsSpan(4)); // cookie + transaction id
            await _stun.SendAsync(resp, req.RemoteEndPoint);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_server is { HasExited: false })
            _server.Kill();
        _server?.Dispose();
        _site?.Close();
        _stun?.Dispose();
        _stop.Dispose();
        try
        {
            _work.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AppSettings Settings(string speedPath = "/big?bytes=1") => new()
    {
        Connection = new ConnectionSettings
        {
            TestUrl = $"http://127.0.0.1:{_sitePort}/generate_204",
            SpeedUrl = $"http://127.0.0.1:{_sitePort}{speedPath}",
            SpeedSizeMb = 2,
            StunServer = $"127.0.0.1:{_stunPort}",
        },
    };

    private Profile Good => ShareLink.Parse($"vless://{Uuid}@127.0.0.1:{_serverPort}?security=none#good").Profile!;

    private Task<IReadOnlyDictionary<Guid, ServerTestResult>> Run(Profile p, AppSettings s, TestKinds kinds, TesterOptions? o = null) =>
        ServerTester.TestAsync([p], [p], s, kinds, Cores, Path.Combine(_work.FullName, "run"), SecretScrubber.PatternsOnly, _ => { },
            null, TestContext.Current.CancellationToken, o);

    [Fact]
    public async Task Healthy_server_passes_all_checks()
    {
        if (_server is null)
            Assert.Skip("Ядра не скачаны");
        var p = Good;
        var r = (await Run(p, Settings(), TestKinds.Standard))[p.Id];

        Assert.True(r.TcpMs is >= 0, r.Error);
        Assert.True(r.DelayMs is >= 0, r.Error);
        Assert.True(r.SpeedMbps is > 0, r.Error);
        Assert.False(r.Frozen);
        Assert.True(r.UdpOk, "STUN через сервер должен пройти");
        Assert.Equal(HealthStatus.Working, ServerHealth.Classify(r));
    }

    [Fact]
    public async Task Freeze_after_16_kb_is_detected_despite_good_ping()
    {
        if (_server is null)
            Assert.Skip("Ядра не скачаны");
        var p = Good;
        var r = (await Run(p, Settings("/freeze"), TestKinds.Tcp | TestKinds.Delay | TestKinds.Speed))[p.Id];

        Assert.True(r.TcpMs is >= 0);   // пинг зелёный…
        Assert.True(r.DelayMs is >= 0); // …задержка тоже…
        Assert.True(r.Frozen);          // …а данные не идут
        Assert.InRange(r.FrozenAfterBytes ?? 0, 1, FreezeDetector.Threshold - 1);
        Assert.Equal(HealthStatus.Frozen, ServerHealth.Classify(r));
    }

    [Fact]
    public async Task Udp_failure_is_reported()
    {
        if (_server is null)
            Assert.Skip("Ядра не скачаны");
        var p = Good;
        var s = Settings() with { Connection = Settings().Connection with { StunServer = "127.0.0.1:1" } };
        var r = (await Run(p, s, TestKinds.Delay | TestKinds.Udp, new TesterOptions { UdpTimeout = TimeSpan.FromSeconds(1) }))[p.Id];
        Assert.False(r.UdpOk);
        Assert.Equal(HealthStatus.Working, ServerHealth.Classify(r)); // сайты работают, но объяснение про звонки
        Assert.Contains("UDP", ServerHealth.Explain(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stability_series()
    {
        if (_server is null)
            Assert.Skip("Ядра не скачаны");
        var p = Good;
        var r = (await Run(p, Settings(), TestKinds.Stability,
            new TesterOptions { StabilitySamples = 5, StabilityInterval = TimeSpan.FromMilliseconds(50) }))[p.Id];
        Assert.Equal(0, r.Loss);
        Assert.NotNull(r.MedianMs);
        Assert.NotNull(r.JitterMs);
    }

    [Fact]
    public async Task Dead_server_is_cut_at_tcp_ping()
    {
        if (_server is null)
            Assert.Skip("Ядра не скачаны");
        var dead = ShareLink.Parse($"vless://{Uuid}@127.0.0.1:1?security=none#dead").Profile!;
        var r = (await Run(dead, Settings(), TestKinds.Standard))[dead.Id];
        Assert.Null(r.TcpMs);
        Assert.Null(r.DelayMs);
        Assert.Equal(HealthStatus.NoResponse, ServerHealth.Classify(r));
    }

    [Fact]
    public void Speed_url_takes_size_from_settings()
    {
        Assert.Equal("https://speed.cloudflare.com/__down?bytes=25000000", ServerTester.SpeedUrl("https://speed.cloudflare.com/__down?bytes=10000000", 25));
        Assert.Equal("https://example.com/file.bin", ServerTester.SpeedUrl("https://example.com/file.bin", 25));
    }

    [Fact]
    public async Task Background_check_switches_away_from_frozen_server()
    {
        if (_server is null)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        var proxy = new FakeProxyStore();
        await using var engine = TropaEngine.Open(
            new EnginePaths(Path.Combine(_work.FullName, "r"), Path.Combine(_work.FullName, "l")), proxy, Cores);
        engine.MonitorIntervalOverride = TimeSpan.FromSeconds(1);
        var good = Settings();
        engine.UpdateSettings(_ => good with { Connection = good.Connection with { SocksPort = FreeTcpPort(), AutoSelect = true } });
        await engine.ImportTextAsync(
            $"vless://{Uuid}@127.0.0.1:{_serverPort}?security=none#A\nvless://{Uuid2}@127.0.0.1:{_serverPort}?security=none#B", null, ct);
        var a = engine.State.Profiles.Single(p => p.Profile.Name == "A").Profile.Id;
        var b = engine.State.Profiles.Single(p => p.Profile.Name == "B").Profile.Id;

        // B проверен и работает; результаты сохраняются в состоянии.
        await engine.TestAsync([engine.State.Profiles.Single(p => p.Profile.Id == b).Profile], TestKinds.Delay | TestKinds.Speed, ct: ct);
        Assert.Equal(HealthStatus.Working, ServerHealth.Classify(engine.State.Profiles.Single(p => p.Profile.Id == b).LastTest));

        await engine.SetActiveAsync(a, ct);
        await engine.ConnectAsync(ct);
        Assert.True(engine.Status.State == ConnectionState.Connected, engine.Status.Message);

        // Теперь «сайт» для теста скорости начинает «замерзать» — фоновая проверка должна это заметить.
        engine.UpdateSettings(s => s with { Connection = s.Connection with { SpeedUrl = $"http://127.0.0.1:{_sitePort}/freeze" } });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(40);
        while (!(engine.State.ActiveProfileId == b && engine.Status.State == ConnectionState.Connected
                   && engine.Status.Message?.Contains("Переключено", StringComparison.Ordinal) == true)
               && DateTime.UtcNow < deadline)
            await Task.Delay(250, ct);

        Assert.Equal(b, engine.State.ActiveProfileId);
        Assert.True(engine.Status.State == ConnectionState.Connected, engine.Status.Message);
        Assert.Contains("Переключено", engine.Status.Message, StringComparison.Ordinal);
        Assert.Equal(HealthStatus.Frozen, ServerHealth.Classify(engine.State.Profiles.Single(p => p.Profile.Id == a).LastTest));
        await engine.DisconnectAsync();
    }
}
