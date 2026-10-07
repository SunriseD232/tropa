using System.Text;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using static Tropa.Core.Tests.Parsing.ShareLinkTests;

namespace Tropa.Core.Tests.Parsing;

public sealed class SubscriptionParserTests
{
    private static readonly string Lines = string.Join("\r\n",
        VlessReality,
        "",
        "# комментарий",
        "trojan://secret@fi.example.com:443?sni=fi.example.com#Helsinki",
        "ss://YWVzLTI1Ni1nY206cGFzcw@1.2.3.4:8388#ss",
        "vless://broken@a.example.com:443",
        VmessWs);

    [Fact]
    public void Plain_link_list()
    {
        var r = SubscriptionParser.Parse(Lines);
        Assert.Equal(SubscriptionFormat.LinkList, r.Format);
        Assert.Equal(3, r.Profiles.Count);
        Assert.Equal(1, r.SkippedUnsupported);
        var error = Assert.Single(r.Errors);
        Assert.Equal(4, error.Position);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Base64_variants(bool urlSafe, bool stripPadding)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(Lines));
        if (urlSafe)
            b64 = b64.Replace('+', '-').Replace('/', '_');
        if (stripPadding)
            b64 = b64.TrimEnd('=');
        // Некоторые панели переносят base64 по 76 символов.
        b64 = string.Join("\n", b64.Chunk(76).Select(c => new string(c)));

        var r = SubscriptionParser.Parse(b64);
        Assert.Equal(SubscriptionFormat.Base64LinkList, r.Format);
        Assert.Equal(3, r.Profiles.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_body(string? body) =>
        Assert.Equal(SubscriptionFormat.Empty, SubscriptionParser.Parse(body).Format);

    [Theory]
    [InlineData("<html><body>Access denied</body></html>")]
    [InlineData("!!!!")]
    [InlineData("{ broken json")]
    public void Garbage_is_reported_not_thrown(string body)
    {
        var r = SubscriptionParser.Parse(body);
        Assert.Empty(r.Profiles);
        Assert.NotEmpty(r.Errors);
    }

    [Fact]
    public void Profile_limit_is_enforced()
    {
        var link = $"vless://{Uuid}@a.example.com:443?security=none#x";
        var body = string.Join("\n", Enumerable.Repeat(link, SubscriptionParser.MaxProfiles + 10));
        var r = SubscriptionParser.Parse(body);
        Assert.Equal(SubscriptionParser.MaxProfiles, r.Profiles.Count);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void Sing_box_json_takes_only_outbounds()
    {
        var json = $$"""
        {
          "log": { "output": "C:\\Windows\\System32\\evil.log" },
          "inbounds": [ { "type": "mixed", "listen": "0.0.0.0", "listen_port": 1080 } ],
          "experimental": { "clash_api": { "external_controller": "0.0.0.0:9090" } },
          "outbounds": [
            { "type": "vless", "tag": "NL Reality", "server": "nl1.example.com", "server_port": 443,
              "uuid": "{{Uuid}}", "flow": "xtls-rprx-vision",
              "tls": { "enabled": true, "server_name": "www.microsoft.com",
                       "utls": { "enabled": true, "fingerprint": "chrome" },
                       "reality": { "enabled": true, "public_key": "{{Pbk}}", "short_id": "a1b2" } } },
            { "type": "trojan", "tag": "FI", "server": "fi.example.com", "server_port": 443, "password": "pw",
              "tls": { "enabled": true, "server_name": "fi.example.com", "alpn": ["h2"] },
              "transport": { "type": "ws", "path": "/ws", "headers": { "Host": "cdn.example.com" } } },
            { "type": "shadowsocks", "tag": "ss" },
            { "type": "direct", "tag": "direct" }
          ]
        }
        """;
        var r = SubscriptionParser.Parse(json);
        Assert.Equal(SubscriptionFormat.SingBoxJson, r.Format);
        Assert.Equal(2, r.Profiles.Count);
        Assert.Equal(1, r.SkippedUnsupported);

        var nl = r.Profiles[0].Profile;
        Assert.Equal("NL Reality", nl.Name);
        Assert.Equal(VlessFlow.XtlsRprxVision, nl.Flow);
        Assert.Equal(SecurityType.Reality, nl.Security.Type);
        Assert.Equal("a1b2", nl.Security.Reality!.ShortId);

        var fi = r.Profiles[1].Profile;
        Assert.Equal(TransportType.Ws, fi.Transport.Type);
        Assert.Equal("cdn.example.com", fi.Transport.Host);
        Assert.Equal("h2", fi.Security.Alpn);
    }

    [Fact]
    public void Xray_config_array_with_remarks()
    {
        var json = $$"""
        [
          { "remarks": "🇩🇪 Frankfurt",
            "outbounds": [
              { "tag": "proxy", "protocol": "vless",
                "settings": { "vnext": [ { "address": "de.example.com", "port": 443,
                  "users": [ { "id": "{{Uuid}}", "encryption": "none", "flow": "" } ] } ] },
                "streamSettings": { "network": "xhttp", "security": "reality",
                  "realitySettings": { "serverName": "www.example.org", "publicKey": "{{Pbk}}", "shortId": "", "fingerprint": "firefox" },
                  "xhttpSettings": { "path": "/x", "mode": "stream-up" } } },
              { "tag": "direct", "protocol": "freedom" }
            ] },
          { "remarks": "bad", "outbounds": [ { "tag": "proxy", "protocol": "vmess",
              "settings": { "vnext": [ { "address": "x.example.com", "port": 443, "users": [ { "id": "{{Uuid}}", "alterId": 64 } ] } ] } } ] }
        ]
        """;
        var r = SubscriptionParser.Parse(json);
        Assert.Equal(SubscriptionFormat.XrayJson, r.Format);
        var p = Assert.Single(r.Profiles).Profile;
        Assert.Equal("🇩🇪 Frankfurt", p.Name);
        Assert.Equal(TransportType.Xhttp, p.Transport.Type);
        Assert.Equal(XhttpMode.StreamUp, p.Transport.XhttpMode);
        Assert.Equal("firefox", p.Security.Fingerprint);
        Assert.Contains("alterId", Assert.Single(r.Errors).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Errors_do_not_contain_raw_line()
    {
        var r = SubscriptionParser.Parse("vless://supersecretvalue@a.example.com:443");
        Assert.DoesNotContain("supersecretvalue", Assert.Single(r.Errors).Message, StringComparison.Ordinal);
    }
}

public sealed class SubscriptionHeadersTests
{
    private static SubscriptionInfo Parse(params (string Key, string Value)[] headers) =>
        SubscriptionHeaders.Parse(headers.Select(h => new KeyValuePair<string, string>(h.Key, h.Value)));

    [Fact]
    public void User_info_and_interval()
    {
        var info = Parse(
            ("Subscription-Userinfo", "upload=1000; download=2000;total=10000; expire=1767225600"),
            ("profile-update-interval", "12"));
        Assert.Equal(1000, info.Upload);
        Assert.Equal(2000, info.Download);
        Assert.Equal(10000, info.Total);
        Assert.Equal(7000, info.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), info.Expire);
        Assert.Equal(12, info.UpdateIntervalHours);
    }

    [Fact]
    public void Zero_expire_means_unlimited() =>
        Assert.Null(Parse(("subscription-userinfo", "total=0; expire=0")).Expire);

    [Fact]
    public void Base64_title_is_decoded_and_sanitized()
    {
        var title = "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Мой\u202E VPN\n"));
        Assert.Equal("Мой VPN", Parse(("profile-title", title)).Title);
    }

    [Theory]
    [InlineData("https://t.me/support", true)]
    [InlineData("tg://resolve?domain=support", true)]
    [InlineData("http://example.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    public void Only_safe_support_urls(string url, bool allowed) =>
        Assert.Equal(allowed, Parse(("support-url", url)).SupportUrl is not null);

    [Fact]
    public void Garbage_values_are_ignored()
    {
        var info = Parse(("subscription-userinfo", "upload=abc; total=-5; =1; expire=99999999999999"), ("profile-update-interval", "0"));
        Assert.Null(info.Upload);
        Assert.Null(info.Total);
        Assert.Null(info.Expire);
        Assert.Null(info.UpdateIntervalHours);
    }
}
