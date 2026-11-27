using System.Net;
using System.Net.Sockets;
using Tropa.Infrastructure;

namespace Tropa.Integration.Tests;

/// <summary>
/// Мёртвый адрес подписки не скачивается раз в минуту (журнал разработчика 2026-11-24…27: до 2800 попыток
/// за четыре дня у одной установки): после неудачи — отсрочка 5, 10, 20… минут, не реже интервала и раза в час.
/// </summary>
public sealed class SubscriptionRetryTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-subretry-");
    private int _hits;

    public SubscriptionRetryTests()
    {
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            _port = ((IPEndPoint)l.LocalEndpoint).Port;
        }

        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                try
                {
                    var ctx = await _listener.GetContextAsync();
                    Interlocked.Increment(ref _hits);
                    ctx.Response.StatusCode = 503;
                    ctx.Response.Close();
                }
                catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
                {
                    return;
                }
            }
        });
    }

    public void Dispose()
    {
        _listener.Close();
        _work.Delete(recursive: true);
    }

    [Fact]
    public async Task Failed_subscription_is_retried_with_backoff_not_every_minute()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        await using var engine = TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, "r"), Path.Combine(_work.FullName, "l")),
            new FakeProxyStore(), now: () => now);

        await engine.AddSubscriptionAsync($"http://127.0.0.1:{_port}/dead", null, ct);
        Assert.Equal(1, _hits);
        Assert.NotNull(Assert.Single(engine.State.Subscriptions).LastError);

        async Task Tick(int minutes)
        {
            now = now.AddMinutes(minutes);
            await engine.UpdateDueSubscriptionsAsync(ct);
        }

        await Tick(1);
        await Tick(1);
        await Tick(1);
        Assert.Equal(1, _hits); // раньше — каждую минуту

        await Tick(2);  // 5 минут после первой неудачи
        Assert.Equal(2, _hits);

        await Tick(5);
        Assert.Equal(2, _hits);
        await Tick(5);  // 10 минут после второй
        Assert.Equal(3, _hits);
    }

    [Theory]
    [InlineData(1, 720, 5)]
    [InlineData(2, 720, 10)]
    [InlineData(3, 720, 20)]
    [InlineData(5, 720, 60)]   // не реже раза в час
    [InlineData(9, 720, 60)]
    [InlineData(4, 5, 5)]      // подписка администратора: не реже её интервала
    public void Retry_delay_grows_and_is_capped(int fails, int everyMinutes, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), TropaEngine.SubscriptionRetryDelay(fails, TimeSpan.FromMinutes(everyMinutes)));
}
