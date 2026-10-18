using System.Net;
using System.Net.Sockets;
using System.Text;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Infrastructure;

namespace Tropa.Integration.Tests;

/// <summary>
/// Подписка от администратора (ADR-048) глазами друга: серверы приходят сразу, выбор сервера и правила —
/// только после «Доверять администратору»; ручной выбор друга держится до следующей смены у администратора.
/// </summary>
public sealed class AdminSubscriptionTests : IDisposable
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private readonly HttpListener _listener = new();
    private readonly int _port;
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-admin-");
    private string _document = "";

    public AdminSubscriptionTests()
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
                    var bytes = Encoding.UTF8.GetBytes(_document);
                    await ctx.Response.OutputStream.WriteAsync(bytes);
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

    private static Profile P(string name) =>
        ShareLink.Parse($"vless://{Uuid}@{name.ToLowerInvariant()}.example.com:443?security=tls&sni={name.ToLowerInvariant()}.example.com#{name}").Profile!;

    private void Serve(IReadOnlyList<Profile> servers, Profile? active, params Rule[] rules) =>
        _document = AdminDocument.Build(servers, active, rules, "Серверы от Васи", 5, DateTimeOffset.UtcNow);

    private static Guid Id(TropaEngine e, string name) => e.State.Profiles.Single(p => p.Profile.Name == name).Profile.Id;

    [Fact]
    public async Task Friend_gets_servers_then_admin_choice_and_rules_after_trust()
    {
        var ct = TestContext.Current.CancellationToken;
        Profile nl = P("NL"), fi = P("FI");
        var site = new Rule { Match = new RuleMatch { DomainSuffixes = ["admin-rule.example"] }, Tcp = RuleAction.Direct, Udp = RuleAction.Direct };
        Serve([nl, fi], fi, site);

        await using var engine = TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, "r"), Path.Combine(_work.FullName, "l")), new FakeProxyStore());
        var report = await engine.AddSubscriptionAsync($"http://127.0.0.1:{_port}/friends", null, ct);

        var sub = Assert.Single(engine.State.Subscriptions);
        Assert.Equal("Серверы от Васи", sub.Name);
        Assert.NotNull(sub.Admin);
        Assert.Equal(2, engine.State.Profiles.Count);
        Assert.Contains(report.Messages, m => m.Contains("Доверять администратору", StringComparison.Ordinal));
        // Без доверия: первый сервер, правила администратора в конфиг не попадают.
        Assert.Equal(Id(engine, "NL"), engine.State.ActiveProfileId);
        Assert.DoesNotContain("admin-rule.example", engine.PreviewConfig(), StringComparison.Ordinal);

        await engine.SetTrustAdminAsync(sub.Id, true, ct);
        Assert.Equal(Id(engine, "FI"), engine.State.ActiveProfileId);
        Assert.Contains("admin-rule.example", engine.PreviewConfig(), StringComparison.Ordinal);

        // Друг выбрал сам — повторное обновление с тем же выбором администратора его не перебивает.
        await engine.SetActiveAsync(Id(engine, "NL"), ct);
        await engine.UpdateSubscriptionAsync(sub.Id, ct);
        Assert.Equal(Id(engine, "NL"), engine.State.ActiveProfileId);

        // Администратор добавил сервер и выбрал его — друг переключается.
        var de = P("DE");
        Serve([nl, fi, de], de, site);
        await engine.UpdateSubscriptionAsync(sub.Id, ct);
        Assert.Equal(3, engine.State.Profiles.Count);
        Assert.Equal(Id(engine, "DE"), engine.State.ActiveProfileId);

        // Больше не доверяет — правила администратора уходят, серверы остаются.
        await engine.SetTrustAdminAsync(sub.Id, false, ct);
        Assert.DoesNotContain("admin-rule.example", engine.PreviewConfig(), StringComparison.Ordinal);
        Assert.Equal(3, engine.State.Profiles.Count);
    }

    [Fact]
    public async Task Publishing_needs_https_address_and_servers()
    {
        await using var engine = TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, "r2"), Path.Combine(_work.FullName, "l2")), new FakeProxyStore());
        Assert.NotNull(await engine.PublishForFriendsAsync(TestContext.Current.CancellationToken));
        Assert.NotNull(engine.SetAdminUrl($"http://127.0.0.1:{_port}/friends"));
        Assert.Null(engine.SetAdminUrl("https://friends.example.com/tropa/0123456789abcdef"));
        Assert.Equal("https://friends.example.com/••••", engine.AdminUrlMasked);
        Assert.NotNull(engine.RevealAdminKey());
        Assert.Contains("Нет серверов", await engine.PublishForFriendsAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
