using Tropa.Core.Model;
using Tropa.Core.Parsing;

namespace Tropa.Core.Tests.Parsing;

public sealed class ShareLinkTests
{
    internal const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    internal const string Pbk = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    internal const string VlessReality =
        "vless://" + Uuid + "@nl1.example.com:443?encryption=none&flow=xtls-rprx-vision&security=reality" +
        "&sni=www.microsoft.com&fp=chrome&pbk=" + Pbk + "&sid=a1b2c3&spx=%2F&type=tcp&headerType=none#%F0%9F%87%B3%F0%9F%87%B1%20Amsterdam-1";

    // {"ps":"🇸🇪 Stockholm","add":"se.example.com","port":443,"net":"ws","path":"/ws?ed=2048","tls":"tls",...}
    internal const string VmessWs = "vmess://eyJ2IjogIjIiLCAicHMiOiAi8J+HuPCfh6ogU3RvY2tob2xtIiwgImFkZCI6ICJzZS5leGFtcGxlLmNvbSIsICJwb3J0IjogNDQzLCAiaWQiOiAiYjgzMTM4MWQtNjMyNC00ZDUzLWFkNGYtOGNkYTQ4YjMwODExIiwgImFpZCI6ICIwIiwgInNjeSI6ICJhdXRvIiwgIm5ldCI6ICJ3cyIsICJ0eXBlIjogIm5vbmUiLCAiaG9zdCI6ICJjZG4uZXhhbXBsZS5jb20iLCAicGF0aCI6ICIvd3M/ZWQ9MjA0OCIsICJ0bHMiOiAidGxzIiwgInNuaSI6ICJjZG4uZXhhbXBsZS5jb20iLCAiYWxwbiI6ICJoMixodHRwLzEuMSIsICJmcCI6ICJjaHJvbWUifQ==";

    // То же, но net=grpc, path=svc, port="8443" строкой, aid=0 числом; base64url без паддинга.
    private const string VmessGrpcUrlSafeNoPad = "vmess://eyJ2IjogIjIiLCAicHMiOiAi8J-HuPCfh6ogU3RvY2tob2xtIiwgImFkZCI6ICJzZS5leGFtcGxlLmNvbSIsICJwb3J0IjogIjg0NDMiLCAiaWQiOiAiYjgzMTM4MWQtNjMyNC00ZDUzLWFkNGYtOGNkYTQ4YjMwODExIiwgImFpZCI6IDAsICJzY3kiOiAiYXV0byIsICJuZXQiOiAiZ3JwYyIsICJ0eXBlIjogIm5vbmUiLCAiaG9zdCI6ICJjZG4uZXhhbXBsZS5jb20iLCAicGF0aCI6ICJzdmMiLCAidGxzIjogInRscyIsICJzbmkiOiAiY2RuLmV4YW1wbGUuY29tIiwgImFscG4iOiAiaDIsaHR0cC8xLjEiLCAiZnAiOiAiY2hyb21lIn0";

    // aid = "64" — устаревший режим без AEAD.
    private const string VmessAlterId = "vmess://eyJ2IjogIjIiLCAicHMiOiAiXHVkODNjXHVkZGY4XHVkODNjXHVkZGVhIFN0b2NraG9sbSIsICJhZGQiOiAic2UuZXhhbXBsZS5jb20iLCAicG9ydCI6IDQ0MywgImlkIjogImI4MzEzODFkLTYzMjQtNGQ1My1hZDRmLThjZGE0OGIzMDgxMSIsICJhaWQiOiAiNjQiLCAic2N5IjogImF1dG8iLCAibmV0IjogIndzIiwgInR5cGUiOiAibm9uZSIsICJob3N0IjogImNkbi5leGFtcGxlLmNvbSIsICJwYXRoIjogIi93cz9lZD0yMDQ4IiwgInRscyI6ICJ0bHMiLCAic25pIjogImNkbi5leGFtcGxlLmNvbSIsICJhbHBuIjogImgyLGh0dHAvMS4xIiwgImZwIjogImNocm9tZSJ9";

    internal static Profile Ok(string link)
    {
        var result = ShareLink.Parse(link);
        Assert.True(result.Success, result.Error);
        return result.Profile!;
    }

    private static string Fail(string link)
    {
        var result = ShareLink.Parse(link);
        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        return result.Error!;
    }

    [Fact]
    public void Vless_reality_vision()
    {
        var p = Ok(VlessReality);
        Assert.Equal(Protocol.Vless, p.Protocol);
        Assert.Equal("nl1.example.com", p.Address);
        Assert.Equal(443, p.Port);
        Assert.Equal(Uuid, p.Credential.Reveal());
        Assert.Equal(VlessFlow.XtlsRprxVision, p.Flow);
        Assert.Equal(TransportType.Tcp, p.Transport.Type);
        Assert.Equal(SecurityType.Reality, p.Security.Type);
        Assert.Equal("www.microsoft.com", p.Security.Sni);
        Assert.Equal("chrome", p.Security.Fingerprint);
        Assert.Equal(Pbk, p.Security.Reality!.PublicKey);
        Assert.Equal("a1b2c3", p.Security.Reality.ShortId);
        Assert.Equal("/", p.Security.Reality.SpiderX);
        Assert.Equal("🇳🇱 Amsterdam-1", p.Name);
    }

    [Fact]
    public void Vless_raw_type_is_tcp_and_ipv6_host()
    {
        var p = Ok($"vless://{Uuid}@[2001:db8::1]:8443?type=raw&security=reality&sni=example.com&pbk={Pbk}#v6");
        Assert.Equal("2001:db8::1", p.Address);
        Assert.Equal(8443, p.Port);
        Assert.Equal(TransportType.Tcp, p.Transport.Type);
        Assert.Equal("chrome", p.Security.Fingerprint); // для Reality отпечаток обязателен
    }

    [Fact]
    public void Vless_xhttp_with_mode_and_extra_warns()
    {
        var r = ShareLink.Parse($"vless://{Uuid}@de.example.com:443?type=xhttp&path=%2Fx&mode=packet-up&extra=%7B%7D&security=tls&sni=de.example.com#de");
        Assert.True(r.Success, r.Error);
        Assert.Equal(TransportType.Xhttp, r.Profile!.Transport.Type);
        Assert.Equal("/x", r.Profile.Transport.Path);
        Assert.Equal(XhttpMode.PacketUp, r.Profile.Transport.XhttpMode);
        Assert.Contains(r.Warnings, w => w.Contains("extra", StringComparison.Ordinal));
    }

    [Fact]
    public void Trojan_defaults_to_tls_and_decodes_password()
    {
        var p = Ok("trojan://p%40ss%3Aword%23x@fi.example.com:443?sni=fi.example.com&alpn=h2,http/1.1#Helsinki");
        Assert.Equal(Protocol.Trojan, p.Protocol);
        Assert.Equal("p@ss:word#x", p.Credential.Reveal());
        Assert.Equal(SecurityType.Tls, p.Security.Type);
        Assert.Equal("h2,http/1.1", p.Security.Alpn);
        Assert.Equal("Helsinki", p.Name);
    }

    [Fact]
    public void Trojan_allow_insecure_warns()
    {
        var r = ShareLink.Parse("trojan://secret@fi.example.com:443?security=tls&allowInsecure=1#x");
        Assert.True(r.Success);
        Assert.True(r.Profile!.Security.AllowInsecure);
        Assert.Contains(r.Warnings, w => w.Contains("allowInsecure", StringComparison.Ordinal));
    }

    [Fact]
    public void Vmess_ws_tls()
    {
        var p = Ok(VmessWs);
        Assert.Equal(Protocol.Vmess, p.Protocol);
        Assert.Equal("se.example.com", p.Address);
        Assert.Equal(TransportType.Ws, p.Transport.Type);
        Assert.Equal("/ws?ed=2048", p.Transport.Path);
        Assert.Equal("cdn.example.com", p.Transport.Host);
        Assert.Equal(SecurityType.Tls, p.Security.Type);
        Assert.Equal("🇸🇪 Stockholm", p.Name);
    }

    [Fact]
    public void Vmess_grpc_url_safe_base64_without_padding_and_string_port()
    {
        var p = Ok(VmessGrpcUrlSafeNoPad);
        Assert.Equal(8443, p.Port);
        Assert.Equal(TransportType.Grpc, p.Transport.Type);
        Assert.Equal("svc", p.Transport.ServiceName);
    }

    [Fact]
    public void Vmess_with_alter_id_is_rejected() =>
        Assert.Contains("alterId", Fail(VmessAlterId), StringComparison.Ordinal);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("ss://YWVzLTI1Ni1nY206cGFzcw@1.2.3.4:8388#ss")]
    [InlineData("vless://not-a-uuid@a.example.com:443?security=none")]
    [InlineData("vless://" + Uuid + "@a.example.com:0?security=none")]
    [InlineData("vless://" + Uuid + "@a.example.com:70000?security=none")]
    [InlineData("vless://" + Uuid + "@a.example.com?security=none")]
    [InlineData("vless://" + Uuid + "@-bad-.example.com:443?security=none")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&sni=a.example.com&pbk=short")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&pbk=" + Pbk)]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&sni=a.example.com&pbk=" + Pbk + "&sid=xyz")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?type=kcp")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?type=tcp&headerType=http")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=xtls")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?flow=xtls-rprx-direct")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?encryption=mlkem768x25519plus.native.0rtt.abc")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?type=ws&path=%2Fa%0D%0AHost:%20evil")]
    [InlineData("vmess://!!!notbase64")]
    [InlineData("vmess://bm90IGpzb24")]
    [InlineData("trojan://@a.example.com:443")]
    public void Rejects_invalid_links(string link) => Fail(link);

    [Fact]
    public void Rejects_too_long_link() =>
        Fail("vless://" + Uuid + "@a.example.com:443?path=" + new string('a', 9000));

    [Fact]
    public void Sanitizes_hostile_names()
    {
        // U+202E (RLO) разворачивает текст, U+200B невидим, перенос строки ломает таблицу.
        var p = Ok($"vless://{Uuid}@a.example.com:443?security=none#%E2%80%AEgnp.exe%E2%80%8B%0Aline2");
        Assert.Equal("gnp.exe line2", p.Name);
    }

    [Fact]
    public void Empty_name_falls_back_to_address()
    {
        var p = Ok($"vless://{Uuid}@a.example.com:443?security=none");
        Assert.Equal("a.example.com:443", p.Name);
    }

    [Fact]
    public void Idn_host_is_converted_to_punycode()
    {
        var p = Ok($"vless://{Uuid}@сервер.рф:443?security=none#x");
        Assert.Equal("xn--b1afb6bcb.xn--p1ai", p.Address);
    }

    [Fact]
    public void Unknown_fingerprint_falls_back_to_chrome_with_warning()
    {
        var r = ShareLink.Parse($"vless://{Uuid}@a.example.com:443?security=tls&fp=netscape#x");
        Assert.Equal("chrome", r.Profile!.Security.Fingerprint);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public void Profile_to_string_does_not_leak_secret()
    {
        var p = Ok(VlessReality);
        Assert.DoesNotContain(Uuid, p.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Uuid, $"{p.Credential}", StringComparison.Ordinal);
    }

    public static TheoryData<string> RoundTripLinks => new()
    {
        VlessReality,
        $"vless://{Uuid}@[2001:db8::1]:8443?type=xhttp&path=%2Fx&host=h.example.com&mode=stream-up&security=tls&sni=h.example.com&alpn=h2&fp=firefox#x",
        $"vless://{Uuid}@a.example.com:443?type=grpc&serviceName=svc&security=tls&sni=a.example.com#g",
        "trojan://p%40ss@fi.example.com:443?type=ws&path=%2Fws&host=cdn.example.com&security=tls&sni=cdn.example.com&allowInsecure=1#t",
        VmessWs,
        VmessGrpcUrlSafeNoPad,
    };

    [Theory]
    [MemberData(nameof(RoundTripLinks))]
    public void Build_then_parse_round_trips(string link)
    {
        var original = Ok(link);
        var rebuilt = Ok(ShareLink.Build(original));
        Assert.Equal(original with { Id = rebuilt.Id }, rebuilt);
    }
}
