using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;
using Tropa.Ipc;
using Tropa.Service;

namespace Tropa.Integration.Tests;

/// <summary>
/// Служба в тестовом процессе: настоящий именованный канал с ACL, настоящая проверка клиента
/// по пути программы (клиент и сервер — один и тот же testhost.exe), настоящее ядро sing-box.
/// TUN здесь не проверяется: для него нужны права администратора (см. tools/dev-service.ps1).
/// </summary>
public sealed class ServiceTests : IAsyncLifetime
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-svc-");
    private readonly string _pipe = "Tropa.Test." + Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _cts = new();
    private PipeServer? _server;
    private Process? _vless;
    private int _vlessPort;

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

    private ServiceOptions Options(IClientVerifier? verifier = null, string? pipe = null) => new()
    {
        PipeName = pipe ?? _pipe,
        DataDirectory = Path.Combine(_work.FullName, "svc"),
        Cores = Cores,
        ClientVerifier = verifier ?? new ExecutablePathVerifier(Self),
        Privileged = false,
    };

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
        _server = new PipeServer(Options(), NullLogger<PipeServer>.Instance);
        await _server.StartAsync(_cts.Token);

        _vlessPort = FreePort();
        var cfg = Path.Combine(_work.FullName, "vless.json");
        await File.WriteAllTextAsync(cfg, $$"""
        { "log": { "level": "warn" },
          "inbounds": [ { "type": "vless", "listen": "127.0.0.1", "listen_port": {{_vlessPort}}, "users": [ { "uuid": "{{Uuid}}" } ] } ],
          "outbounds": [ { "type": "direct" } ] }
        """);
        var psi = new ProcessStartInfo(Path.Combine(Cores.CoresDirectory, "sing-box.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "run", "-c", cfg })
            psi.ArgumentList.Add(a);
        _vless = Process.Start(psi);
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _cts.CancelAsync();
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
        }

        if (_vless is { HasExited: false })
            _vless.Kill();
        _vless?.Dispose();
        _cts.Dispose();
        try
        {
            _work.Delete(recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Task<ServiceClient?> Connect(string? expectedServer = null) =>
        ServiceClient.TryConnectAsync(_pipe, expectedServer ?? Self, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

    private string Config(ServiceClient client, int port, CaptureMode mode = CaptureMode.SystemProxy)
    {
        var settings = new AppSettings { Connection = new ConnectionSettings { Mode = mode, SocksPort = port, AutoSelect = false } };
        var profile = ShareLink.Parse($"vless://{Uuid}@127.0.0.1:{_vlessPort}?security=none#local").Profile!;
        return SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = settings,
            Active = profile,
            RuleSetDirectory = client.RuleSetDirectory,
            LocalAuth = new LocalAuth("u", "p"),
        });
    }

    private static async Task<bool> PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<bool> Eventually(Func<Task<bool>> condition, int seconds = 10)
    {
        for (var i = 0; i < seconds * 10; i++)
        {
            if (await condition())
                return true;
            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task Hello_reports_capabilities()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        await using var client = await Connect();
        Assert.NotNull(client);
        Assert.False(client.TunSupported); // тест идёт без прав администратора
        Assert.StartsWith(_work.FullName, client.RuleSetDirectory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Start_and_stop_core_through_service()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        await using var client = (await Connect())!;
        var port = FreePort();
        var reply = await client.SendAsync(new StartRequest(Config(client, port), CaptureModeDto.SystemProxy, port, false, false), TestContext.Current.CancellationToken);
        Assert.True(reply.Ok, reply.Error);
        Assert.True(await PortOpen(port));

        // Конфиг с ключами не остаётся на диске службы.
        Assert.Empty(Directory.GetFiles(Path.Combine(_work.FullName, "svc", "run"), "config-*.json"));

        Assert.True((await client.SendAsync(new StopRequest(), TestContext.Current.CancellationToken)).Ok);
        Assert.True(await Eventually(async () => !await PortOpen(port)));
    }

    [Fact]
    public async Task Tun_without_privileges_is_refused_clearly()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        await using var client = (await Connect())!;
        var port = FreePort();
        var reply = await client.SendAsync(new StartRequest(Config(client, port, CaptureMode.Tun), CaptureModeDto.Tun, port, false, true), TestContext.Current.CancellationToken);
        Assert.False(reply.Ok);
        Assert.Contains("служб", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Service_rechecks_config_and_refuses_open_port()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        await using var client = (await Connect())!;
        var port = FreePort();
        var evil = Config(client, port).Replace("\"127.0.0.1\"", "\"0.0.0.0\"", StringComparison.Ordinal);
        var reply = await client.SendAsync(new StartRequest(evil, CaptureModeDto.SystemProxy, port, false, false), TestContext.Current.CancellationToken);
        Assert.False(reply.Ok);
        Assert.Contains("проверку безопасности", reply.Error, StringComparison.Ordinal);
        Assert.False(await PortOpen(port));
    }

    [Fact]
    public async Task Foreign_client_is_dropped()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var pipe = "Tropa.Test." + Guid.NewGuid().ToString("N");
        using var strict = new PipeServer(Options(new ExecutablePathVerifier(@"C:\Program Files\Tropa\Tropa.exe"), pipe), NullLogger<PipeServer>.Instance);
        await strict.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var attempt = () => ServiceClient.TryConnectAsync(pipe, Self, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(attempt);
        }
        finally
        {
            await strict.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Client_refuses_impostor_server()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ex = await Assert.ThrowsAsync<ServiceException>(() => Connect(@"C:\Program Files\Tropa\Tropa.Service.exe"));
        Assert.Contains("чужой программой", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Second_server_cannot_squat_the_pipe()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        using var squatter = new PipeServer(Options(), NullLogger<PipeServer>.Instance);
        await squatter.StartAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => squatter.ExecuteTask!);
    }

    [Fact]
    public async Task Oversized_frame_drops_connection()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        await using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(3000, TestContext.Current.CancellationToken);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, 500 * 1024 * 1024);
        await raw.WriteAsync(header, TestContext.Current.CancellationToken);
        var buffer = new byte[16];
        var read = await raw.ReadAsync(buffer, TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, read); // служба закрыла канал, ничего не выделив
    }

    [Fact]
    public async Task Commands_before_hello_are_refused()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        await using var raw = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync(3000, TestContext.Current.CancellationToken);
        await Framing.WriteAsync<Request>(raw, new StopRequest { Id = 7 }, IpcJsonContext.Default.Request, TestContext.Current.CancellationToken);
        var reply = Assert.IsType<Reply>(await Framing.ReadAsync(raw, IpcJsonContext.Default.ServerMessage, TestContext.Current.CancellationToken), exactMatch: false);
        Assert.False(reply.Ok);
        Assert.Equal(7, reply.Id);
    }

    [Fact]
    public async Task Disconnected_controller_stops_core()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var port = FreePort();
        var client = (await Connect())!;
        Assert.True((await client.SendAsync(new StartRequest(Config(client, port), CaptureModeDto.SystemProxy, port, false, false), TestContext.Current.CancellationToken)).Ok);
        await client.DisposeAsync(); // интерфейс «упал»
        Assert.True(await Eventually(async () => !await PortOpen(port)), "ядро должно остановиться без управляющего клиента");
    }

    [Fact]
    public async Task Crashed_core_is_restarted()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        await using var client = (await Connect())!;
        var statuses = new List<StatusEvent>();
        client.Status += (_, s) => { lock (statuses) statuses.Add(s); };
        var port = FreePort();
        Assert.True((await client.SendAsync(new StartRequest(Config(client, port), CaptureModeDto.SystemProxy, port, false, false), TestContext.Current.CancellationToken)).Ok);

        // Убиваем только ядро этого теста: оно одно слушает порт, который мы ему дали. По времени запуска
        // искать нельзя — параллельные тесты запускают свои ядра в то же время.
        using var core = Process.GetProcessById(ListeningPid(port));
        Assert.Equal("sing-box", core.ProcessName);
        core.Kill();

        Assert.True(await Eventually(() => Task.FromResult(statuses.Any(s => s.Message?.Contains("перезапущено", StringComparison.Ordinal) == true))));
        Assert.True(await Eventually(() => PortOpen(port)));
        await client.SendAsync(new StopRequest(), TestContext.Current.CancellationToken);
    }

    /// <summary>PID процесса, который слушает 127.0.0.1:port (по таблице netstat).</summary>
    private static int ListeningPid(int port)
    {
        var psi = new ProcessStartInfo("netstat", "-ano -p TCP") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 5 && parts[1] == $"127.0.0.1:{port}" && parts[3] == "LISTENING")
                return int.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture);
        }

        throw new InvalidOperationException($"Никто не слушает порт {port}.");
    }

    [Fact]
    public async Task Engine_connects_through_service()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var proxy = new FakeProxyStore();
        var original = proxy.Read();
        await using var engine = TropaEngine.Open(
            new EnginePaths(Path.Combine(_work.FullName, "r"), Path.Combine(_work.FullName, "l")), proxy, Cores,
            connectService: ct => ServiceClient.TryConnectAsync(_pipe, Self, TimeSpan.FromSeconds(3), ct));
        var port = FreePort();
        engine.UpdateSettings(s => s with { Connection = s.Connection with { SocksPort = port, AutoSelect = false } });
        await engine.ImportTextAsync($"vless://{Uuid}@127.0.0.1:{_vlessPort}?security=none#local", null, TestContext.Current.CancellationToken);

        await engine.ConnectAsync(TestContext.Current.CancellationToken);
        Assert.True(engine.Status.State == ConnectionState.Connected, engine.Status.Message);
        Assert.True(engine.ServiceAvailable);
        Assert.False(engine.TunAvailable);
        Assert.Contains("Служба", engine.Status.Message, StringComparison.Ordinal); // TUN выбран, но прав нет — объяснение
        Assert.Equal($"127.0.0.1:{port}", proxy.Values["ProxyServer"]);
        Assert.True(await PortOpen(port));

        await engine.DisconnectAsync();
        Assert.Equal(original, proxy.Values);
        Assert.True(await Eventually(async () => !await PortOpen(port)));
    }

    /// <summary>
    /// «Сначала обход DPI»: группа, чей адрес проверки открывается напрямую, остаётся на обходе;
    /// группа, где проверка не проходит, сама переключается на сервер (Clash API, без переподключения).
    /// </summary>
    [Fact]
    public async Task Dpi_first_switches_failing_groups_to_proxy()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        using var site = new TcpListener(IPAddress.Loopback, 0);
        site.Start();
        var sitePort = ((IPEndPoint)site.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var c = await site.AcceptTcpClientAsync(ct);
                var s = c.GetStream();
                var buf = new byte[4096];
                _ = await s.ReadAsync(buf, ct);
                await s.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
            }
        }, ct);
        var dead = FreePort(); // никто не слушает — «обход не открыл сервис»

        await using var engine = TropaEngine.Open(
            new EnginePaths(Path.Combine(_work.FullName, "dr"), Path.Combine(_work.FullName, "dl")), new FakeProxyStore(), Cores);
        engine.DpiCheckInterval = TimeSpan.FromSeconds(1);
        engine.DpiProbeUrlOverride = g => new Uri($"http://127.0.0.1:{(g.Key == "youtube" ? sitePort : dead)}/");
        var port = FreePort();
        engine.UpdateSettings(s => s with
        {
            Connection = s.Connection with { Mode = CaptureMode.SystemProxy, SocksPort = port, AutoSelect = false },
            Routing = s.Routing with { Preset = RoutePreset.BlockedOnly, DpiFirst = true },
        });
        await engine.ImportTextAsync($"vless://{Uuid}@127.0.0.1:{_vlessPort}?security=none#local", null, ct);
        await engine.ConnectAsync(ct);
        Assert.True(engine.Status.State == ConnectionState.Connected, engine.Status.Message);

        Assert.True(await Eventually(() => Task.FromResult(engine.DpiStatus.Count == Core.Routing.DpiGroups.All.Count), 30));
        Assert.True(engine.DpiStatus["youtube"]);
        Assert.False(engine.DpiStatus["discord"]);
        Assert.False(engine.DpiStatus["blocked"]);
        await engine.DisconnectAsync();
    }

    /// <summary>Ядро «Автоматически»: проверка сначала через Xray; заработал — запоминается Xray.</summary>
    [Fact]
    public async Task Auto_core_is_detected_xray_first()
    {
        if (!CoresPresent)
            Assert.Skip("Ядра не скачаны");
        var ct = TestContext.Current.CancellationToken;
        await using var engine = TropaEngine.Open(
            new EnginePaths(Path.Combine(_work.FullName, "cr"), Path.Combine(_work.FullName, "cl")), new FakeProxyStore(), Cores);
        using var site = new TcpListener(IPAddress.Loopback, 0);
        site.Start();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                using var c = await site.AcceptTcpClientAsync(ct);
                var stream = c.GetStream();
                _ = await stream.ReadAsync(new byte[4096], ct);
                await stream.WriteAsync("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
            }
        }, ct);
        engine.UpdateSettings(s => s with { Connection = s.Connection with { TestUrl = $"http://127.0.0.1:{((IPEndPoint)site.LocalEndpoint).Port}/" } });
        await engine.ImportTextAsync($"vless://{Uuid}@127.0.0.1:{_vlessPort}?security=none#auto", null, ct);
        await engine.ImportTextAsync($"vless://{Uuid}@localhost:{_vlessPort}?security=none#fixed", null, ct);
        var auto = engine.State.Profiles.First(p => p.Profile.Name == "auto").Profile;
        var fixedOne = engine.State.Profiles.First(p => p.Profile.Name == "fixed").Profile;
        await engine.SetServerCoreAsync(fixedOne.Id, CoreChoice.SingBox, ct);

        await engine.TestAsync([auto, fixedOne], Infrastructure.Testing.TestKinds.Delay, ct: ct);
        var a = engine.State.Profiles.First(p => p.Profile.Id == auto.Id);
        var f = engine.State.Profiles.First(p => p.Profile.Id == fixedOne.Id);
        Assert.True(a.LastTest?.DelayMs is not null, a.LastTest?.Error);
        Assert.Equal(CoreChoice.Xray, a.DetectedCore);
        Assert.Equal(CoreChoice.Xray, a.EffectiveCore);
        Assert.Equal(CoreChoice.SingBox, f.EffectiveCore); // выбор пользователя важнее
    }
}
