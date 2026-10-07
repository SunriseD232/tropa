using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Ipc;
using Tropa.Service;

namespace Tropa.Integration.Tests;

/// <summary>Kill switch, который только записывает, что с ним делали.</summary>
internal sealed class FakeKillSwitch(IReadOnlyList<string> apps, bool allowLan) : IKillSwitch
{
    public IReadOnlyList<string> Apps { get; } = apps;
    public bool AllowLan { get; } = allowLan;
    public List<ulong> Tun { get; } = [];
    public bool Disposed { get; private set; }

    public void AllowTunInterface(ulong luid) => Tun.Add(luid);

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Логика аварийной блокировки в службе: когда включается, когда держится и кто её снимает.
/// Настоящий WFP подменён (нужны права администратора, см. WfpTests), TUN тоже: служба считает
/// себя привилегированной, а конфиг поднимает только локальный порт.
/// </summary>
public sealed class ServiceKillSwitchTests : IAsyncLifetime
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-ks-");
    private readonly string _pipe = "Tropa.Test." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private readonly List<FakeKillSwitch> _switches = [];
    private readonly System.Security.Cryptography.ECDsa _updateKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private PipeServer? _server;

    private static string Self => Environment.ProcessPath!;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir!.FullName;
    }

    private static CoreLocations Cores => new(Path.Combine(RepoRoot(), "cores"), Path.Combine(RepoRoot(), "geo"));

    private static bool CoresPresent => File.Exists(Path.Combine(Cores.CoresDirectory, "sing-box.exe")) && Directory.Exists(Cores.GeoSourceDirectory);

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public async ValueTask InitializeAsync()
    {
        if (!CoresPresent)
            return;
        _server = new PipeServer(new ServiceOptions
        {
            PipeName = _pipe,
            DataDirectory = Path.Combine(_work.FullName, "svc"),
            Cores = Cores,
            ClientVerifier = new ExecutablePathVerifier(Self),
            Privileged = true,
            KillSwitchFactory = (apps, lan) =>
            {
                var ks = new FakeKillSwitch(apps, lan);
                lock (_switches)
                    _switches.Add(ks);
                return ks;
            },
            FindTunLuid = _ => Task.FromResult<ulong?>(42),
            Updates = new Infrastructure.Updates.UpdateConfig("https://updates.example/manifest.json",
                Convert.ToBase64String(_updateKey.ExportSubjectPublicKeyInfo()), 0),
        }, NullLogger<PipeServer>.Instance);
        await _server.StartAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _cts.CancelAsync();
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
        }

        _cts.Dispose();
        _updateKey.Dispose();
        try
        {
            _work.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ядро ещё завершается и держит свой exe — временный каталог уберёт система.
        }
    }

    private Task<ServiceClient?> Connect() =>
        ServiceClient.TryConnectAsync(_pipe, Self, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

    private static StartRequest Start(ServiceClient client, int port, bool killSwitch = true)
    {
        var settings = new AppSettings { Connection = new ConnectionSettings { Mode = CaptureMode.SystemProxy, SocksPort = port, AutoSelect = false } };
        var profile = ShareLink.Parse("vless://b831381d-6324-4d53-ad4f-8cda48b30811@127.0.0.1:9?security=none#local").Profile!;
        var config = SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = settings,
            Active = profile,
            RuleSetDirectory = client.RuleSetDirectory,
            LocalAuth = new LocalAuth("u", "p"),
        });
        return new StartRequest(config, CaptureModeDto.Tun, port, false, false, KillSwitch: killSwitch, KillSwitchAllowLan: false);
    }

    private static async Task<bool> Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 100; i++)
        {
            if (condition())
                return true;
            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task Block_is_enabled_before_core_and_survives_reconnect()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        await using var client = (await Connect())!;
        var reply = await client.SendAsync(Start(client, FreePort()), ct);
        Assert.True(reply.Ok, reply.Error);
        var ks = Assert.Single(_switches);
        Assert.Contains(ks.Apps, a => a.EndsWith("sing-box.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(ks.Apps, a => a.EndsWith("xray.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(ks.AllowLan);
        Assert.Equal([42ul], ks.Tun);

        // Переподключение (новый Start без Stop): та же блокировка, без «окна» между ядрами.
        Assert.True((await client.SendAsync(Start(client, FreePort()), ct)).Ok);
        Assert.Single(_switches);
        Assert.False(ks.Disposed);
        Assert.Equal([42ul, 42ul], ks.Tun);

        // «Отключить» снимает блокировку.
        Assert.True((await client.SendAsync(new StopRequest(), ct)).Ok);
        Assert.True(ks.Disposed);
    }

    [Fact]
    public async Task Block_stays_when_ui_disappears_and_is_reported()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        var first = (await Connect())!;
        Assert.True((await first.SendAsync(Start(first, FreePort()), ct)).Ok);
        await first.DisposeAsync(); // интерфейс упал
        var ks = Assert.Single(_switches);
        await Task.Delay(500, ct);
        Assert.False(ks.Disposed);

        // Новый интерфейс узнаёт о блокировке из статуса.
        await using var second = (await Connect())!;
        var statuses = new List<StatusEvent>();
        second.Status += (_, s) => { lock (statuses) statuses.Add(s); };
        await second.SendAsync(new StatusRequest(), ct);
        Assert.True(await Eventually(() => { lock (statuses) return statuses.Any(s => s.Blocking); }));

        // «Аварийно вернуть настройки сети» снимает всё.
        Assert.True((await second.SendAsync(new RollbackAllRequest(), ct)).Ok);
        Assert.True(ks.Disposed);
    }

    [Fact]
    public async Task Without_kill_switch_nothing_is_blocked()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        await using var client = (await Connect())!;
        Assert.True((await client.SendAsync(Start(client, FreePort(), killSwitch: false), ct)).Ok);
        Assert.Empty(_switches);
        await client.SendAsync(new StopRequest(), ct);
    }

    [Fact]
    public async Task Signed_update_is_installed_and_used_for_next_start()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        var tools = Path.Combine(RepoRoot(), "tools");
        var now = DateTimeOffset.UtcNow;
        var manifest = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = 1,
            ["sequence"] = 3,
            ["issued"] = now.ToString("o"),
            ["expires"] = now.AddDays(30).ToString("o"),
            ["cores"] = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(tools, "cores.lock.json"))),
            ["geo"] = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(tools, "geo.lock.json"))),
        }.ToJsonString());
        var sig = Core.Updates.ManifestSignature.Sign(manifest, _updateKey);
        var empty = Directory.CreateDirectory(Path.Combine(_work.FullName, "download")).FullName;

        await using (var client = (await Connect())!)
        {
            Assert.Equal(0, client.UpdateSequence);
            // Подделка: подпись от другого ключа.
            using var evil = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var bad = await client.SendAsync(new InstallUpdateRequest(Convert.ToBase64String(manifest),
                Convert.ToBase64String(Core.Updates.ManifestSignature.Sign(manifest, evil)), empty), ct);
            Assert.False(bad.Ok);
            Assert.Contains("Подпись", bad.Error, StringComparison.Ordinal);

            var ok = await client.SendAsync(new InstallUpdateRequest(Convert.ToBase64String(manifest), Convert.ToBase64String(sig), empty), ct);
            Assert.True(ok.Ok, ok.Error);
        }

        await using var again = (await Connect())!;
        Assert.Equal(3, again.UpdateSequence);
        Assert.Contains(Path.Combine("updates", "3", "cores"), again.XrayPath, StringComparison.OrdinalIgnoreCase);
        var reply = await again.SendAsync(Start(again, FreePort(), killSwitch: false), ct);
        Assert.True(reply.Ok, reply.Error);
        await again.SendAsync(new StopRequest(), ct);
    }

    [Fact]
    public async Task Bad_store_sid_is_refused()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        await using var client = (await Connect())!;
        var request = Start(client, FreePort(), killSwitch: false) with { Mode = CaptureModeDto.SystemProxy, LoopbackSids = ["S-1-5-18"] };
        var reply = await client.SendAsync(request, ct);
        Assert.False(reply.Ok);
        Assert.Contains("Store", reply.Error, StringComparison.Ordinal);
    }
}
