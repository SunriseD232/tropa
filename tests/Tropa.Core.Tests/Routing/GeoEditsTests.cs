using Tropa.Core.Model;
using Tropa.Core.Routing;

namespace Tropa.Core.Tests.Routing;

public sealed class GeoEditsTests
{
    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4/32")]
    [InlineData(" 10.1.2.3/8 ", "10.0.0.0/8")]
    [InlineData("5.6.0.0/16", "5.6.0.0/16")]
    [InlineData("2A00:1450:4001::1/32", "2a00:1450::/32")]
    [InlineData("::1", "::1/128")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    public void Cidrs_are_normalized(string input, string expected) => Assert.Equal(expected, GeoEdits.Cidr(input));

    [Theory]
    [InlineData("")]
    [InlineData("example.com")]
    [InlineData("1.2.3.4/33")]
    [InlineData("1.2.3.4/-1")]
    [InlineData("1.2.3.4/ 8")]
    [InlineData("::ffff:1.2.3.4")]
    public void Bad_cidrs(string input) => Assert.Null(GeoEdits.Cidr(input));

    [Fact]
    public void Input_is_split_and_checked()
    {
        var (entries, bad) = GeoEdits.ParseInput("1.2.3.4, 5.6.0.0/16\nfoo\n1.2.3.4", GeoEntryKind.Cidr);
        Assert.Equal([new GeoEntry(GeoEntryKind.Cidr, "1.2.3.4/32"), new GeoEntry(GeoEntryKind.Cidr, "5.6.0.0/16")], entries);
        Assert.Equal(["foo"], bad);
    }

    [Fact]
    public void Sites_keep_www_only_for_exact_addresses()
    {
        Assert.Equal([new GeoEntry(GeoEntryKind.Suffix, "example.com")], GeoEdits.ParseInput("https://www.Example.com/x", GeoEntryKind.Suffix).Entries);
        Assert.Equal([new GeoEntry(GeoEntryKind.Domain, "www.example.com")], GeoEdits.ParseInput("www.example.com", GeoEntryKind.Domain).Entries);
        Assert.Equal(["1.2.3.4"], GeoEdits.ParseInput("1.2.3.4", GeoEntryKind.Suffix).Bad);
    }

    [Fact]
    public void Untrusted_edits_are_sanitized()
    {
        var clean = GeoEdits.Sanitize(
        [
            new GeoListEdit { Tag = "geosite-unknown", Added = [new(GeoEntryKind.Suffix, "a.example")] },
            new GeoListEdit
            {
                Tag = RuleSets.GeoipRu,
                Added = [new(GeoEntryKind.Cidr, "10.1.2.3/8"), new(GeoEntryKind.Suffix, "wrong-kind.example"), new(GeoEntryKind.Cidr, "nope")],
                Removed = [new(GeoEntryKind.Cidr, "1.1.1.1")],
            },
            new GeoListEdit { Tag = RuleSets.GeoipRu, Added = [new(GeoEntryKind.Cidr, "9.9.9.9")] }, // повтор тега
            new GeoListEdit
            {
                Tag = RuleSets.GeositeRuBlocked,
                // Слова и выражения проверяются; неподходящее для RE2 выражение отбрасывается.
                Added = [new(GeoEntryKind.Keyword, "Linked"), new(GeoEntryKind.Regex, "(?<=a)b"), new(GeoEntryKind.Keyword, "bad word"), new(GeoEntryKind.Suffix, "ok.example")],
                Removed = [new(GeoEntryKind.Regex, "^x\\.example$"), new(GeoEntryKind.Suffix, "with space"), new(GeoEntryKind.Keyword, "tor")],
            },
            new GeoListEdit { Tag = RuleSets.GeoipRuBlocked, Added = [new(GeoEntryKind.Cidr, "bad")] }, // пустая после очистки
        ]);

        Assert.Equal(2, clean.Count);
        Assert.Equal([new GeoEntry(GeoEntryKind.Cidr, "10.0.0.0/8")], clean[0].Added);
        Assert.Equal([new GeoEntry(GeoEntryKind.Cidr, "1.1.1.1/32")], clean[0].Removed);
        Assert.Equal([new GeoEntry(GeoEntryKind.Keyword, "linked"), new GeoEntry(GeoEntryKind.Suffix, "ok.example")], clean[1].Added);
        Assert.Equal([new GeoEntry(GeoEntryKind.Regex, "^x\\.example$"), new GeoEntry(GeoEntryKind.Keyword, "tor")], clean[1].Removed);
    }

    [Theory]
    [InlineData("linked", "linkedin.com", true)]       // пример владельца
    [InlineData("linked", "www.linkedin.cn", true)]
    [InlineData("linked", "example.com", false)]
    [InlineData("LINKED", "linkedin.com", true)]
    public void Keyword_matches_part_of_address(string keyword, string host, bool expected) =>
        Assert.Equal(expected, GeoEdits.Matches(new GeoEntry(GeoEntryKind.Keyword, GeoEdits.Keyword(keyword)!), GeoEdits.Host(host), null));

    [Theory]
    [InlineData(GeoEntryKind.Suffix, "media-watch.ru", "media-watch.ru", true)]
    [InlineData(GeoEntryKind.Suffix, "media-watch.ru", "cdn.media-watch.ru", true)]
    [InlineData(GeoEntryKind.Suffix, "media-watch.ru", "notmedia-watch.ru", false)]
    [InlineData(GeoEntryKind.Suffix, ".media-watch.ru", "media-watch.ru", false)]  // с точкой — только поддомены
    [InlineData(GeoEntryKind.Suffix, ".media-watch.ru", "a.media-watch.ru", true)]
    [InlineData(GeoEntryKind.Domain, "www.example.com", "www.example.com", true)]
    [InlineData(GeoEntryKind.Domain, "www.example.com", "example.com", false)]
    [InlineData(GeoEntryKind.Regex, @"^(.+\.)?linkedin\.(com|cn)$", "www.linkedin.cn", true)]
    [InlineData(GeoEntryKind.Regex, @"^(.+\.)?linkedin\.(com|cn)$", "linkedin.com.evil.example", false)]
    [InlineData(GeoEntryKind.Regex, "tube", "www.youtube.com", true)] // без якорей — поиск в любом месте
    public void Domain_kinds_match_like_sing_box(GeoEntryKind kind, string value, string host, bool expected) =>
        Assert.Equal(expected, GeoEdits.Matches(new GeoEntry(kind, value), host, null));

    [Theory]
    [InlineData("5.6.0.0/16", "5.6.200.7", true)]
    [InlineData("5.6.0.0/16", "5.7.0.1", false)]
    [InlineData("1.1.1.1/32", "1.1.1.1", true)]
    [InlineData("2a00:1450::/32", "2a00:1450:4001::1", true)]
    [InlineData("5.6.0.0/16", "2a00:1450::1", false)]
    [InlineData("0.0.0.0/0", "9.9.9.9", true)]
    public void Networks_contain_addresses(string cidr, string ip, bool expected) =>
        Assert.Equal(expected, GeoEdits.Matches(new GeoEntry(GeoEntryKind.Cidr, cidr), null, System.Net.IPAddress.Parse(ip)));

    [Theory]
    [InlineData(@"^(.+\.)?linkedin\.com$", true)]
    [InlineData("tube", true)]
    [InlineData("(?<=a)b", false)]   // просмотр назад — нет в RE2
    [InlineData(@"(a)\1", false)]    // обратная ссылка
    [InlineData(@"\p{L}+", false)]   // классы Unicode .NET
    [InlineData("[a-z-[aeiou]]", false)] // вычитание классов .NET
    [InlineData("(", false)]
    public void Regexes_are_limited_to_re2(string pattern, bool ok) => Assert.Equal(ok, GeoEdits.Regex(pattern) is not null);

    [Fact]
    public void Regexes_are_split_by_lines_only()
    {
        var (entries, _) = GeoEdits.ParseInput("^a{1,3}\\.example$\n tube ", GeoEntryKind.Regex);
        Assert.Equal(["^a{1,3}\\.example$", "tube"], entries.Select(e => e.Value));
    }

    [Theory]
    [InlineData("https://www.LinkedIn.com/feed", "www.linkedin.com")]
    [InlineData("linkedin.com:443", "linkedin.com")]
    [InlineData("1.2.3.4", null)]
    [InlineData("not a host", null)]
    public void Hosts_to_check(string input, string? expected) => Assert.Equal(expected, GeoEdits.Host(input));

    [Fact]
    public void V2ray_list_is_pasted_as_is()
    {
        // Вставка владельца из v2rayN — с запятыми в конце строк.
        var r = GeoEdits.ParseAuto("domain:faceit.com,\ndomain:xsolla.com,\ndomain:otvet.mail.ru,\ndomain:cybershoke.net", ip: true, sites: true);
        Assert.Equal(
            [new GeoEntry(GeoEntryKind.Suffix, "faceit.com"), new GeoEntry(GeoEntryKind.Suffix, "xsolla.com"),
             new GeoEntry(GeoEntryKind.Suffix, "otvet.mail.ru"), new GeoEntry(GeoEntryKind.Suffix, "cybershoke.net")],
            r.Entries);
        Assert.Empty(r.Bad);
        // domain: — домен и поддомены, как в v2ray.
        Assert.True(GeoEdits.Matches(r.Entries[0], "www.faceit.com", null));
    }

    [Fact]
    public void V2ray_prefixes_and_other_formats()
    {
        var r = GeoEdits.ParseAuto("""
            [
              "full:otvet.mail.ru",
              "keyword:xsolla",
              "regexp:^(.+\\.)?faceit\\.(com|net)$",
              "geosite:category-games",
              "91.108.4.0/22", "2001:b28:f23d::/48",
              plain.example.com
            ]
            DOMAIN-SUFFIX,clash.example,PROXY
            +.plus.example
            # комментарий
            bogus:thing
            """, ip: true, sites: true);
        Assert.Equal(
            [new GeoEntry(GeoEntryKind.Domain, "otvet.mail.ru"), new GeoEntry(GeoEntryKind.Keyword, "xsolla"),
             new GeoEntry(GeoEntryKind.Regex, @"^(.+\.)?faceit\.(com|net)$"), new GeoEntry(GeoEntryKind.Cidr, "91.108.4.0/22"),
             new GeoEntry(GeoEntryKind.Cidr, "2001:b28:f23d::/48"), new GeoEntry(GeoEntryKind.Suffix, "plain.example.com"),
             new GeoEntry(GeoEntryKind.Suffix, "clash.example"), new GeoEntry(GeoEntryKind.Suffix, "plus.example")],
            r.Entries);
        Assert.Equal(["geosite:category-games"], r.GeoRefs);
        Assert.Equal(["bogus:thing"], r.Bad);
    }

    [Fact]
    public void Ip_entries_go_only_where_allowed()
    {
        var r = GeoEdits.ParseAuto("domain:a.example\n1.2.3.4", ip: false, sites: true);
        Assert.Equal([new GeoEntry(GeoEntryKind.Suffix, "a.example")], r.Entries);
        Assert.Equal(["1.2.3.4"], r.WrongPlace);
    }

    [Fact]
    public void Custom_lists_are_sanitized()
    {
        var id = Guid.NewGuid();
        var clean = GeoEdits.SanitizeCustom(
        [
            new CustomGeoList { Id = id, Name = "Игры‮", Action = RuleActionKind.Server, Entries = [new(GeoEntryKind.Suffix, "faceit.com"), new(GeoEntryKind.Cidr, "nope"), new(GeoEntryKind.Regex, "(?<=x)")] },
            new CustomGeoList { Id = id, Name = "Повтор" },
            new CustomGeoList { Id = Guid.Empty, Name = "Без Id" },
            new CustomGeoList { Id = Guid.NewGuid(), Name = "  " },
        ]);
        Assert.Equal(2, clean.Count);
        Assert.Equal("Игры", clean[0].Name);
        Assert.Equal(RuleActionKind.Proxy, clean[0].Action);
        Assert.Equal([new GeoEntry(GeoEntryKind.Suffix, "faceit.com")], clean[0].Entries);
        Assert.Equal("Мой список", clean[1].Name);
    }

    [Fact]
    public void Added_wins_over_removed()
    {
        GeoEntry a = new(GeoEntryKind.Suffix, "a.example"), b = new(GeoEntryKind.Suffix, "b.example"), c = new(GeoEntryKind.Suffix, "c.example");
        var result = GeoEdits.Apply([a, b], new GeoListEdit { Tag = "x", Added = [b, c], Removed = [a, b] });
        Assert.Equal([b, c], result);
    }

    [Fact]
    public void Catalog_covers_pinned_sets_only()
    {
        var pinned = Tropa.Core.Cores.GeoLock.Parse(File.ReadAllText(Path.Combine(Cores.CoresLockTests.RepoRoot(), "tools", "geo.lock.json")))
            .Files.Select(f => f.Name).ToHashSet();
        Assert.All(GeoEdits.Catalog, c => Assert.Contains(c.Tag, pinned));
        Assert.Equal(pinned.Count, GeoEdits.Catalog.Count);
    }
}
