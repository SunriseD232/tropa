using System.Net;
using System.Net.Sockets;
using System.Text;
using Tropa.Core.Model;
using Tropa.Core.Security;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Net;

namespace Tropa.Integration.Tests;

/// <summary>
/// Загрузка подписки с локального HTTP-сервера. Пользовательский интерфейс http:// не принимает;
/// загрузчик разрешает его, только если пользователь сам добавил такой адрес, — этим и пользуется тест.
/// </summary>
public sealed class SubscriptionFetchTests : IDisposable
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-sub-");
    private Func<HttpListenerContext, Task> _handler = _ => Task.CompletedTask;
    private string? _lastUserAgent;
    private string? _lastHwid;

    public SubscriptionFetchTests()
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
                    _lastUserAgent = ctx.Request.UserAgent;
                    _lastHwid = ctx.Request.Headers["x-hwid"];
                    await _handler(ctx);
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
    }

    public void Dispose()
    {
        _listener.Close();
        _work.Delete(recursive: true);
    }

    private string Url(string path) => $"http://127.0.0.1:{_port}/{path}";

    private static async Task Write(HttpListenerContext ctx, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private TropaEngine Engine() =>
        TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, "r"), Path.Combine(_work.FullName, "l")), new FakeProxyStore());

    [Fact]
    public async Task Base64_subscription_with_headers_is_imported()
    {
        var links = $"vless://{Uuid}@nl.example.com:443?security=none#%F0%9F%87%B3%F0%9F%87%B1%20NL\ntrojan://pw@fi.example.com:443?sni=fi.example.com#FI";
        _handler = async ctx =>
        {
            ctx.Response.Headers["subscription-userinfo"] = "upload=1; download=2; total=1073741824; expire=1893456000";
            ctx.Response.Headers["profile-title"] = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Мой провайдер"));
            ctx.Response.Headers["profile-update-interval"] = "6";
            await Write(ctx, Convert.ToBase64String(Encoding.UTF8.GetBytes(links)));
        };

        await using var engine = Engine();
        var report = await engine.ImportTextAsync(Url("sub/TOKEN"), null, TestContext.Current.CancellationToken);

        Assert.Equal(2, report.Added);
        var sub = Assert.Single(engine.State.Subscriptions);
        Assert.Equal("Мой провайдер", sub.Name);
        Assert.Equal(6, sub.UpdateHours);
        Assert.Equal(1073741824 - 3, sub.Info!.Remaining);
        Assert.NotNull(engine.State.ActiveProfileId); // первый сервер выбран сам
        Assert.StartsWith("Tropa/", _lastUserAgent, StringComparison.Ordinal);
        Assert.Null(_lastHwid); // HWID не отправляется без согласия
    }

    [Fact]
    public async Task Update_keeps_selected_server()
    {
        var host = "a.example.com";
        _handler = ctx => Write(ctx, $"vless://{Uuid}@{host}:443?security=none#A\nvless://{Uuid}@b.example.com:443?security=none#B");
        await using var engine = Engine();
        await engine.ImportTextAsync(Url("sub"), null, TestContext.Current.CancellationToken);
        var b = engine.State.Profiles.Single(p => p.Profile.Name == "B").Profile.Id;
        await engine.SetActiveAsync(b, TestContext.Current.CancellationToken);

        host = "c.example.com"; // провайдер заменил один сервер
        var report = await engine.UpdateSubscriptionAsync(engine.State.Subscriptions[0].Id, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Added);
        Assert.Equal(b, engine.State.ActiveProfileId);
        Assert.Contains(engine.State.Profiles, p => p.Profile.Address == "a.example.com" && p.RemovedByProvider is not null);
    }

    [Fact]
    public async Task Removed_server_stays_hidden_order_survives_and_subscription_can_be_deleted()
    {
        var body = $"vless://{Uuid}@a.example.com:443?security=none#A\nvless://{Uuid}@b.example.com:443?security=none#B\nvless://{Uuid}@c.example.com:443?security=none#C";
        _handler = ctx => Write(ctx, body);
        await using var engine = Engine();
        await engine.ImportTextAsync(Url("sub"), null, TestContext.Current.CancellationToken);
        await engine.ImportTextAsync(Url("sub2"), "Вторая", TestContext.Current.CancellationToken);
        var subId = engine.State.Subscriptions[0].Id;
        Guid Id(string name) => engine.State.Profiles.First(p => p.Profile.Name == name && p.Profile.SubscriptionId == subId).Profile.Id;
        List<string> Visible() => engine.State.Profiles.Where(p => p.Profile.SubscriptionId == subId && !p.Hidden)
            .OrderBy(p => p.Order).Select(p => p.Profile.Name).ToList();

        // A выбран; удаляем его — выбирается следующий видимый, а при обновлении A не возвращается.
        await engine.SetActiveAsync(Id("A"), TestContext.Current.CancellationToken);
        engine.RemoveProfile(Id("A"));
        Assert.NotEqual(Id("A"), engine.State.ActiveProfileId);
        // C поднимаем над B.
        engine.MoveProfile(Id("C"), -1, [Id("B"), Id("C")]);
        Assert.Equal(["C", "B"], Visible());

        await engine.UpdateSubscriptionAsync(subId, TestContext.Current.CancellationToken);
        Assert.Equal(["C", "B"], Visible());
        Assert.True(engine.State.Profiles.Single(p => p.Profile.Id == Id("A")).Hidden);

        engine.RestoreHidden(subId);
        Assert.Contains("A", Visible());

        // Сдвиг подписок и удаление подписки вместе с серверами.
        var second = engine.State.Subscriptions[1].Id;
        engine.MoveSubscription(second, -1);
        Assert.Equal(second, engine.State.Subscriptions[0].Id);
        engine.RemoveSubscription(subId);
        Assert.Single(engine.State.Subscriptions);
        Assert.DoesNotContain(engine.State.Profiles, p => p.Profile.SubscriptionId == subId);
        Assert.Contains(engine.State.Profiles, p => p.Profile.Id == engine.State.ActiveProfileId);
    }

    [Fact]
    public async Task Empty_answer_suggests_user_agent_change()
    {
        _handler = ctx => Write(ctx, "<html>Unsupported client</html>");
        await using var engine = Engine();
        var report = await engine.ImportTextAsync(Url("sub"), "X", TestContext.Current.CancellationToken);
        Assert.Equal(0, report.Added);
        Assert.Contains(report.Messages, m => m.Contains("v2rayN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Too_large_answer_is_refused()
    {
        _handler = async ctx =>
        {
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 7; i++)
                await ctx.Response.OutputStream.WriteAsync(chunk);
        };
        var sub = new Subscription { Name = "x", Url = new Secret(Url("big")) };
        var ex = await Assert.ThrowsAsync<SubscriptionFetchException>(() => SubscriptionFetcher.FetchAsync(sub, null, TestContext.Current.CancellationToken));
        Assert.Contains("большая", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Redirect_to_other_scheme_is_refused()
    {
        _handler = ctx =>
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.RedirectLocation = "file:///C:/Windows/win.ini";
            return Task.CompletedTask;
        };
        var sub = new Subscription { Name = "x", Url = new Secret(Url("redir")) };
        await Assert.ThrowsAsync<SubscriptionFetchException>(() => SubscriptionFetcher.FetchAsync(sub, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error_message_does_not_leak_url()
    {
        _handler = ctx =>
        {
            ctx.Response.StatusCode = 403;
            return Task.CompletedTask;
        };
        var sub = new Subscription { Name = "x", Url = new Secret(Url("sub/SECRET-TOKEN-123")), FetchViaProxy = false };
        var ex = await Assert.ThrowsAsync<SubscriptionFetchException>(() => SubscriptionFetcher.FetchAsync(sub, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain("SECRET-TOKEN-123", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hwid_and_user_agent_are_sent_when_enabled()
    {
        _handler = ctx => Write(ctx, $"vless://{Uuid}@a.example.com:443?security=none#A");
        var sub = new Subscription { Name = "x", Url = new Secret(Url("sub")), SendHwid = true, UserAgent = UserAgentMode.V2rayN };
        await SubscriptionFetcher.FetchAsync(sub, null, TestContext.Current.CancellationToken);
        Assert.StartsWith("v2rayN/", _lastUserAgent, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{32}$", _lastHwid);
    }
}
