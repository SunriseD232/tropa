using Tropa.Core.Routing;

namespace Tropa.Core.Tests.Routing;

public sealed class SiteListImportTests
{
    [Fact]
    public void Plain_list_hosts_and_urls()
    {
        var r = SiteListImport.Parse("""
            # мой список
            discord.com
            *.discord.gg
            .youtube.com
            https://chat.openai.com/c/123
            0.0.0.0 ads.example.net
            149.154.160.0/20
            91.108.4.1
            not a domain at all
            """);
        Assert.Equal(["chat.openai.com", "discord.com", "discord.gg", "youtube.com"], r.DomainSuffixes);
        Assert.Equal(["ads.example.net"], r.Domains);
        Assert.Equal(["149.154.160.0/20", "91.108.4.1/32"], r.IpCidrs);
        Assert.Equal(1, r.Skipped);
    }

    [Fact]
    public void Clash_rules_and_payload()
    {
        var r = SiteListImport.Parse("""
            payload:
              - DOMAIN-SUFFIX,x.com,PROXY
              - DOMAIN,api.twitter.com
              - DOMAIN-KEYWORD,telegram
              - IP-CIDR,8.8.8.0/24,no-resolve
              - '+.instagram.com'
              - GEOSITE,google
              - DOMAIN-REGEX,^.*\.ru$
            """);
        Assert.Equal(["instagram.com", "x.com"], r.DomainSuffixes);
        Assert.Equal(["api.twitter.com"], r.Domains);
        Assert.Equal(["telegram"], r.Keywords);
        Assert.Equal(["8.8.8.0/24"], r.IpCidrs);
        Assert.Contains(r.Warnings, w => w.Contains("geosite", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.Contains("регулярных", StringComparison.Ordinal));
    }

    [Fact]
    public void V2rayN_routing_json()
    {
        var r = SiteListImport.Parse("""
            [ { "remarks": "proxy", "outboundTag": "proxy",
                "domain": ["domain:discord.com", "full:gateway.discord.gg", "keyword:spotify", "geosite:youtube", "regexp:.*"],
                "ip": ["162.159.128.0/19", "geoip:telegram"] } ]
            """);
        Assert.Equal(["discord.com"], r.DomainSuffixes);
        Assert.Equal(["gateway.discord.gg"], r.Domains);
        Assert.Equal(["spotify"], r.Keywords);
        Assert.Equal(["162.159.128.0/19"], r.IpCidrs);
    }

    [Fact]
    public void Duplicates_and_covered_names_collapse_and_rule_is_built()
    {
        var r = SiteListImport.Parse("DOMAIN,discord.com\nDOMAIN-SUFFIX,discord.com\ndiscord.com\n");
        Assert.Equal(["discord.com"], r.DomainSuffixes);
        Assert.Empty(r.Domains);
        var rule = r.ToRule("Импорт", Tropa.Core.Model.RuleAction.Proxy, Tropa.Core.Model.RuleAction.Proxy);
        Assert.False(rule.Match.IsEmpty);
        Assert.Equal("Импорт", rule.Label);
    }

    [Fact]
    public void Empty_and_huge_inputs_are_safe()
    {
        Assert.Equal(0, SiteListImport.Parse("").Count);
        Assert.Equal(0, SiteListImport.Parse("{ broken json").Count);
        var many = string.Join('\n', Enumerable.Range(0, SiteListImport.MaxEntries + 10).Select(i => $"s{i}.example.com"));
        Assert.Equal(SiteListImport.MaxEntries, SiteListImport.Parse(many).Count);
    }
}
