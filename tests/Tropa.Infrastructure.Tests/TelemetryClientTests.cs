using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tropa.Core.Model;
using Tropa.Core.Telemetry;
using Tropa.Infrastructure.Telemetry;

namespace Tropa.Infrastructure.Tests;

public sealed class TelemetryClientTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-telemetry-");
    private readonly HttpListener _listener = new();
    private readonly List<string> _bodies = [];
    private readonly List<string?> _auth = [];
    private readonly string _url;
    private int _status = 204;
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    public TelemetryClientTests()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        _url = $"http://127.0.0.1:{port}/tropa-logs/test-path-0123456789/";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
                    var body = await reader.ReadToEndAsync();
                    lock (_bodies)
                    {
                        _bodies.Add(body);
                        _auth.Add(ctx.Request.Headers["Authorization"]);
                    }

                    ctx.Response.StatusCode = _status;
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    return;
                }
            }
        });
    }

    public void Dispose()
    {
        _listener.Close();
        _dir.Delete(recursive: true);
    }

    private TelemetryClient Client() => new(_dir.FullName, new TelemetryConfig(_url, "test-token-0123456789"), () => Now) { AppVersion = "0.1.17", Name = "Вася" };

    [Fact]
    public async Task Events_survive_in_queue_and_are_sent_in_one_batch()
    {
        await using (var first = Client())
        {
            first.Record(Ev.ConnectStart, new Dictionary<string, string> { ["src"] = "home" });
            first.Record(Ev.ConnectFailed, new Dictionary<string, string> { ["msg"] = "timeout" });
        }

        // Новый экземпляр (как после перезапуска Тропы) видит очередь и тот же id.
        await using var client = Client();
        Assert.Equal(2, client.Pending);
        Assert.True(await client.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, client.Pending);

        var batch = JsonSerializer.Deserialize(Assert.Single(_bodies), TelemetryJsonContext.Default.TelemetryBatch)!;
        Assert.Equal(client.InstallId, batch.Id);
        Assert.Matches("^[0-9a-f]{32}$", batch.Id);
        Assert.Equal("Вася", batch.Name);
        Assert.Equal([Ev.ConnectStart, Ev.ConnectFailed], batch.Events.Select(e => e.Code));
        Assert.Equal("home", batch.Events[0].Data!["src"]);
        Assert.Equal("Bearer test-token-0123456789", Assert.Single(_auth));
    }

    [Fact]
    public async Task Failed_send_keeps_queue()
    {
        _status = 500;
        await using var client = Client();
        client.Record(Ev.AppStart);
        Assert.False(await client.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1, client.Pending);
        Assert.Contains("500", client.LastResult, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opt_out_records_nothing_and_clears_queue()
    {
        await using var client = Client();
        client.Record(Ev.AppStart);
        client.Clear();
        client.Allowed = false;
        client.Record(Ev.AppStart);
        Assert.Equal(0, client.Pending);
        Assert.False(await client.FlushAsync(TestContext.Current.CancellationToken));
        Assert.Empty(_bodies);
    }

    [Fact]
    public async Task Engine_logs_settings_changes_without_secrets()
    {
        var paths = new EnginePaths(Path.Combine(_dir.FullName, "roaming"), Path.Combine(_dir.FullName, "local"));
        await using var engine = TropaEngine.Open(paths, new FakeProxyStore(), now: () => Now,
            telemetry: new TelemetryConfig(_url, "test-token-0123456789"));
        engine.UpdateSettings(s => s with { General = s.General with { KillSwitch = true }, Dns = s.Dns with { Hosts = "private.example 10.0.0.1" } });
        Assert.True(await engine.Telemetry!.FlushAsync(TestContext.Current.CancellationToken));

        var body = Assert.Single(_bodies);
        Assert.Contains("general.killSwitch", body, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", body, StringComparison.Ordinal);

        // Выключили журнал — изменение не записано.
        engine.UpdateSettings(s => s with { Telemetry = s.Telemetry with { Off = true } });
        engine.UpdateSettings(s => s with { General = s.General with { KillSwitch = false } });
        Assert.Equal(0, engine.Telemetry.Pending);
    }
}
