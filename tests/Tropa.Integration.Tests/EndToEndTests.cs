using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tropa.Core.Model;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.SystemIntegration;

namespace Tropa.Integration.Tests;

/// <summary>
/// Сквозная проверка без интернета: настоящий sing-box в роли VLESS-сервера на 127.0.0.1,
/// локальный HTTP-сервер в роли «сайта», и движок Тропы как клиент.
/// </summary>
public sealed class EndToEndTests : IAsyncLifetime
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-e2e-");
    private Process? _server;
    private HttpListener? _site;
    private int _serverPort;
    private int _sitePort;
    private int _siteHits;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static CoreLocations Locations => new(Path.Combine(RepoRoot(), "cores"), Path.Combine(RepoRoot(), "geo"));

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public async ValueTask InitializeAsync()
    {
        if (!File.Exists(Path.Combine(Locations.CoresDirectory, "sing-box.exe")) || !Directory.Exists(Locations.GeoSourceDirectory))
            return;

        _sitePort = FreePort();
        _site = new HttpListener();
        _site.Prefixes.Add($"http://127.0.0.1:{_sitePort}/");
        _site.Start();
        _ = Task.Run(async () =>
        {
            while (_site.IsListening)
            {
                try
                {
                    var ctx = await _site.GetContextAsync();
                    Interlocked.Increment(ref _siteHits);
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                }
                catch (HttpListenerException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        });

        _serverPort = FreePort();
        var serverConfig = Path.Combine(_work.FullName, "server.json");
        await File.WriteAllTextAsync(serverConfig, $$"""
        {
          "log": { "level": "warn" },
          "inbounds": [ { "type": "vless", "listen": "127.0.0.1", "listen_port": {{_serverPort}}, "users": [ { "uuid": "{{Uuid}}" } ] } ],
          "outbounds": [ { "type": "direct" } ]
        }
        """);
        var psi = new ProcessStartInfo(Path.Combine(Locations.CoresDirectory, "sing-box.exe")) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(serverConfig);
        _server = Process.Start(psi);
        await Task.Delay(1000);
    }

    public ValueTask DisposeAsync()
    {
        if (_server is { HasExited: false })
            _server.Kill();
        _server?.Dispose();
        _site?.Close();
        _work.Delete(recursive: true);
        return ValueTask.CompletedTask;
    }

    private TropaEngine OpenEngine(FakeProxyStore proxyStore) =>
        TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, "roaming"), Path.Combine(_work.FullName, "local")), proxyStore, Locations);

    [Fact]
    public async Task Server_tester_measures_delay_through_real_vless_server()
    {
        if (_server is null)
            Assert.Skip("Ядра или наборы правил не скачаны: tools/fetch-cores.ps1, tools/fetch-geo.ps1");

        await using var engine = OpenEngine(new FakeProxyStore());
        engine.UpdateSettings(s => s with { Connection = s.Connection with { TestUrl = $"http://127.0.0.1:{_sitePort}/generate_204" } });
        var report = await engine.ImportTextAsync($"vless://{Uuid}@127.0.0.1:{_serverPort}?security=none#local\nvless://{Uuid}@127.0.0.1:1?security=none#dead", null, TestContext.Current.CancellationToken);
        Assert.Equal(2, report.Added);

        var results = await engine.TestAsync(engine.State.Profiles.Select(p => p.Profile).ToList(),
            Infrastructure.Testing.TestKinds.Tcp | Infrastructure.Testing.TestKinds.Delay, ct: TestContext.Current.CancellationToken);
        var goodId = engine.State.Profiles.Single(p => p.Profile.Name == "local").Profile.Id;
        var good = results[goodId];
        var dead = results.Single(r => r.Key != goodId).Value;
        Assert.True(good.DelayMs is >= 0, good.Error);
        Assert.True(good.TcpMs is >= 0, good.Error);
        Assert.Null(dead.TcpMs); // мёртвый порт отсекается ещё на TCP-пинге
        Assert.NotNull(dead.Error);
        Assert.True(_siteHits >= 3, "запросы должны были пройти через VLESS-сервер до «сайта»");
    }

    [Fact]
    public async Task Connect_applies_system_proxy_and_disconnect_restores_it()
    {
        if (_server is null)
            Assert.Skip("Ядра или наборы правил не скачаны: tools/fetch-cores.ps1, tools/fetch-geo.ps1");

        var proxyStore = new FakeProxyStore();
        var original = proxyStore.Read();
        await using var engine = OpenEngine(proxyStore);
        var port = FreePort();
        engine.UpdateSettings(s => s with { Connection = s.Connection with { SocksPort = port, AutoSelect = false } });
        await engine.ImportTextAsync($"vless://{Uuid}@127.0.0.1:{_serverPort}?security=none#local", null, TestContext.Current.CancellationToken);

        await engine.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.Status.State == ConnectionState.Connected, engine.Status.Message);
        Assert.Equal($"127.0.0.1:{port}", proxyStore.Values["ProxyServer"]);

        // Конфиг с ключами удалён с диска сразу после старта ядра.
        Assert.Empty(Directory.GetFiles(Path.Combine(_work.FullName, "local", "run"), "config-*.json"));

        // Порт работает как HTTP-прокси без пароля (режим «Только браузеры», ADR-013).
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{port}"), UseProxy = true });
        using var response = await client.GetAsync($"http://127.0.0.1:{_sitePort}/x", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        await engine.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, engine.Status.State);
        Assert.Equal(original, proxyStore.Values);
    }

    [Fact]
    public async Task Crash_leaves_no_proxy_behind()
    {
        if (_server is null)
            Assert.Skip("Ядра или наборы правил не скачаны: tools/fetch-cores.ps1, tools/fetch-geo.ps1");

        var proxyStore = new FakeProxyStore();
        var original = proxyStore.Read();
        var engine = OpenEngine(proxyStore);
        engine.UpdateSettings(s => s with { Connection = s.Connection with { SocksPort = FreePort(), AutoSelect = false } });
        await engine.ImportTextAsync($"vless://{Uuid}@127.0.0.1:{_serverPort}?security=none#local", null, TestContext.Current.CancellationToken);
        await engine.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionState.Connected, engine.Status.State);

        // Имитация аварии: движок брошен без DisposeAsync; новый запуск должен откатить прокси.
        await using var restarted = OpenEngine(proxyStore);
        Assert.Equal(original, proxyStore.Values);
        Assert.Contains("аварийно", restarted.StartupWarning, StringComparison.Ordinal);
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Tampered_core_is_refused()
    {
        if (_server is null)
            Assert.Skip("Ядра или наборы правил не скачаны: tools/fetch-cores.ps1, tools/fetch-geo.ps1");

        var fakeCores = Directory.CreateDirectory(Path.Combine(_work.FullName, "fake-cores"));
        File.Copy(Path.Combine(Locations.CoresDirectory, "sing-box.exe"), Path.Combine(fakeCores.FullName, "sing-box.exe"));
        await using (var f = File.OpenWrite(Path.Combine(fakeCores.FullName, "sing-box.exe")))
        {
            f.Seek(0, SeekOrigin.End);
            f.WriteByte(0);
        }

        var proxyStore = new FakeProxyStore();
        var original = proxyStore.Read();
        await using var engine = TropaEngine.Open(
            new EnginePaths(Path.Combine(_work.FullName, "r2"), Path.Combine(_work.FullName, "l2")), proxyStore,
            new CoreLocations(fakeCores.FullName, Locations.GeoSourceDirectory));
        engine.UpdateSettings(s => s with { Connection = s.Connection with { SocksPort = FreePort(), AutoSelect = false } });
        await engine.ImportTextAsync($"vless://{Uuid}@127.0.0.1:{_serverPort}?security=none#local", null, TestContext.Current.CancellationToken);
        await engine.ConnectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ConnectionState.Error, engine.Status.State);
        Assert.Contains("изменён", engine.Status.Message, StringComparison.Ordinal);
        Assert.Equal(original, proxyStore.Values); // система не тронута
    }
}

internal sealed class FakeProxyStore : IProxySettingsStore
{
    public Dictionary<string, string?> Values { get; } = new()
    {
        ["ProxyEnable"] = "0",
        ["ProxyServer"] = null,
        ["ProxyOverride"] = null,
        ["AutoConfigURL"] = null,
    };

    public Dictionary<string, string?> Read() => new(Values);

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (k, v) in values)
            Values[k] = v;
    }
}
