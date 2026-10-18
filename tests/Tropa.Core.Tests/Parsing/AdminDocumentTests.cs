using Tropa.Core.Model;
using Tropa.Core.Parsing;

namespace Tropa.Core.Tests.Parsing;

public sealed class AdminDocumentTests
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private static Profile P(string link) => ShareLink.Parse(link).Profile!;

    [Fact]
    public void Build_then_parse_keeps_servers_active_and_rules()
    {
        var nl = P($"vless://{Uuid}@nl.example.com:443?security=tls&sni=nl.example.com#NL");
        var fi = P($"vless://{Uuid}@fi.example.com:443?security=tls&sni=fi.example.com#FI");
        var rules = new[]
        {
            new Rule { Label = "Discord", Match = new RuleMatch { Processes = ["Discord.exe"] }, Tcp = RuleAction.Proxy, Udp = new RuleAction(RuleActionKind.Server, fi.Id) },
            new Rule { Match = new RuleMatch { DomainSuffixes = ["ya.ru"], IpCidrs = ["10.0.0.0/8"], Ports = [new PortRange(80, 443)] }, Tcp = RuleAction.Direct, Udp = RuleAction.Block },
            new Rule { Enabled = false, Match = new RuleMatch { Processes = ["off.exe"] }, Tcp = RuleAction.Direct, Udp = RuleAction.Direct },
        };

        var json = AdminDocument.Build([nl, fi], fi, rules, "Серверы от Васи", 1, DateTimeOffset.UnixEpoch);
        Assert.True(AdminDocument.Looks(json));
        var parsed = AdminDocument.TryParse(json)!;

        Assert.Equal(["NL", "FI"], parsed.Servers.Profiles.Select(p => p.Profile.Name));
        Assert.Equal("FI", parsed.Channel.ActiveServer);
        Assert.Equal("Серверы от Васи", parsed.Channel.Title);
        Assert.Equal(AdminDocument.MinRefreshMinutes, parsed.Channel.RefreshMinutes); // 1 минута — не чаще 5
        Assert.Equal(2, parsed.Channel.Rules.Count); // выключенное правило не уходит
        var discord = parsed.Channel.Rules[0];
        Assert.Equal(["discord.exe"], discord.Match.Processes.Select(x => x.ToLowerInvariant()));
        Assert.Equal(new AdminAction(RuleActionKind.Server, "FI"), discord.Udp);
        var site = parsed.Channel.Rules[1];
        Assert.Equal(["ya.ru"], site.Match.DomainSuffixes);
        Assert.Equal(["10.0.0.0/8"], site.Match.IpCidrs);
        Assert.Equal([new PortRange(80, 443)], site.Match.Ports);
        Assert.Equal(RuleActionKind.Direct, site.Tcp.Kind);
        Assert.Equal(RuleActionKind.Block, site.Udp.Kind);
    }

    [Fact]
    public void Untrusted_document_is_sanitized()
    {
        const string json = $$"""
        {
          "kind": "tropa-admin", "version": 1, "refresh_minutes": 100000, "evil": { "exec": "calc.exe" },
          "active": "NL",
          "servers": [ "vless://{{Uuid}}@nl.example.com:443?security=none#NL", "not a link", null ],
          "rules": [
            { "ports": [ "443" ], "tcp": "direct" },
            { "domains": [ "bad domain!", "" ], "processes": [ "a<b>|c" ] },
            { "domain_suffixes": [ "example.com" ], "tcp": "run-script", "udp": "server:" },
            null
          ]
        }
        """;
        var parsed = AdminDocument.TryParse(json)!;
        Assert.Single(parsed.Servers.Profiles);
        Assert.Equal(2, parsed.Servers.Errors.Count);
        Assert.Equal(24 * 60, parsed.Channel.RefreshMinutes);
        // Только порт — слишком широкое правило; мусорные адреса и процессы отбрасываются.
        var rule = Assert.Single(parsed.Channel.Rules);
        Assert.Equal(["example.com"], rule.Match.DomainSuffixes);
        Assert.Equal(RuleActionKind.Proxy, rule.Tcp.Kind); // неизвестное действие — через сервер, не напрямую
        Assert.Equal(RuleActionKind.Proxy, rule.Udp.Kind);
        Assert.Contains(parsed.Warnings, w => w.Contains("Пропущено правил", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{ "kind": "tropa-settings", "servers": [] }""")]
    [InlineData("""{ "outbounds": [] }""")]
    [InlineData("{ broken")]
    public void Other_json_is_not_an_admin_document(string json) => Assert.Null(AdminDocument.TryParse(json));

    [Fact]
    public void Refresh_interval_prefers_admin_then_settings()
    {
        var url = new Tropa.Core.Security.Secret("https://example.com/s");
        var plain = new Subscription { Name = "A", Url = url };
        Assert.Equal(TimeSpan.FromHours(12), plain.RefreshInterval(new SubscriptionDefaults()));
        Assert.Equal(TimeSpan.FromMinutes(15), plain.RefreshInterval(new SubscriptionDefaults { UpdateMinutes = 15 }));
        Assert.Null(plain.RefreshInterval(new SubscriptionDefaults { UpdateHours = 0 }));
        var admin = plain with { Admin = new AdminChannel { RefreshMinutes = 5 } };
        Assert.Equal(TimeSpan.FromMinutes(5), admin.RefreshInterval(new SubscriptionDefaults { UpdateHours = 0 }));
    }
}
