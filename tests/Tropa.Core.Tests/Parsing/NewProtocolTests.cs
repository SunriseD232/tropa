using Tropa.Core.Compatibility;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Parsing;

namespace Tropa.Core.Tests.Parsing;

public sealed class NewProtocolTests
{
    private const string Key16 = "AAECAwQFBgcICQoLDA0ODw==";

    private static Profile Ok(string link)
    {
        var r = ShareLink.Parse(link);
        Assert.True(r.Success, r.Error);
        return r.Profile!;
    }

    [Fact]
    public void Shadowsocks_2022_plain_userinfo()
    {
        var p = Ok($"ss://2022-blake3-aes-128-gcm:{Uri.EscapeDataString(Key16)}@ss.example.com:8388#Моя%20SS");
        Assert.Equal(Protocol.Shadowsocks, p.Protocol);
        Assert.Equal("2022-blake3-aes-128-gcm", p.SsMethod);
        Assert.Equal(Key16, p.Credential.Reveal());
        Assert.Equal("Моя SS", p.Name);
        Assert.Empty(ProfileCompat.Issues(p));
    }

    [Fact]
    public void Shadowsocks_sip002_base64_and_legacy()
    {
        // SIP002: base64url(«метод:пароль»), без паддинга.
        var sip = Ok("ss://YWVzLTI1Ni1nY206c2VjcmV0@1.2.3.4:8388#A");
        Assert.Equal("aes-256-gcm", sip.SsMethod);
        Assert.Equal("secret", sip.Credential.Reveal());

        // Старый формат: base64 от всего «метод:пароль@адрес:порт».
        var legacy = Ok("ss://" + Convert.ToBase64String("chacha20-ietf-poly1305:pw@5.6.7.8:443"u8.ToArray()) + "#B");
        Assert.Equal("chacha20-ietf-poly1305", legacy.SsMethod);
        Assert.Equal("5.6.7.8", legacy.Address);
        Assert.Equal(443, legacy.Port);
    }

    [Theory]
    [InlineData("ss://YWVzLTI1Ni1nY206c2VjcmV0@1.2.3.4:8388/?plugin=obfs-local%3Bobfs%3Dhttp#P", "Плагины")]
    [InlineData("ss://bm9uZTpwdw@1.2.3.4:8388#none", "не поддерживается")]
    [InlineData("ss://2022-blake3-aes-256-gcm:AAECAwQFBgcICQoLDA0ODw%3D%3D@1.2.3.4:8388#short", "32 байт")]
    [InlineData("ss://2022-blake3-aes-128-gcm:notbase64!@1.2.3.4:8388#bad", "base64")]
    public void Shadowsocks_bad_links(string link, string expected)
    {
        var r = ShareLink.Parse(link);
        Assert.False(r.Success);
        Assert.Contains(expected, r.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Hysteria2_link()
    {
        var p = Ok("hysteria2://pa%40ss@hy.example.com:443/?sni=real.example.com&obfs=salamander&obfs-password=ob&insecure=1#HY");
        Assert.Equal(Protocol.Hysteria2, p.Protocol);
        Assert.Equal("pa@ss", p.Credential.Reveal());
        Assert.Equal("ob", p.Obfs!.Reveal());
        Assert.Equal(SecurityType.Tls, p.Security.Type);
        Assert.Equal("real.example.com", p.Security.Sni);
        Assert.True(p.Security.AllowInsecure);
        Assert.Empty(ProfileCompat.Issues(p));

        var hy2 = ShareLink.Parse("hy2://pw@1.2.3.4:20000-30000#range");
        Assert.True(hy2.Success);
        Assert.Equal(20000, hy2.Profile!.Port);
        Assert.Contains(hy2.Warnings, w => w.Contains("Диапазон", StringComparison.Ordinal));

        Assert.False(ShareLink.Parse("hysteria2://pw@1.2.3.4:443?obfs=gfw#x").Success);
    }

    [Theory]
    [InlineData("ss://2022-blake3-aes-128-gcm:AAECAwQFBgcICQoLDA0ODw%3D%3D@ss.example.com:8388#SS")]
    [InlineData("hysteria2://pw@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=o#HY")]
    public void Build_round_trip(string link)
    {
        var p = Ok(link);
        var again = Ok(ShareLink.Build(p));
        Assert.Equal(p.IdentityKey, again.IdentityKey);
        Assert.Equal(p.SsMethod, again.SsMethod);
        Assert.Equal(p.Obfs?.Reveal(), again.Obfs?.Reveal());
        Assert.Equal(p.Security.Sni, again.Security.Sni);
    }

    [Fact]
    public void Compatibility_of_new_protocols()
    {
        var ss = Ok($"ss://aes-128-gcm:pw@1.2.3.4:8388#ss");
        Assert.NotEmpty(ProfileCompat.Issues(ss with { Security = new SecuritySettings { Type = SecurityType.Tls } }));
        var hy = Ok("hysteria2://pw@1.2.3.4:443#hy");
        Assert.NotEmpty(ProfileCompat.Issues(hy with { Security = SecuritySettings.None }));

        // Mux для Hysteria2 выключается с объяснением.
        var settings = new AppSettings { Dpi = new DpiSettings { Mux = true } };
        var compat = CompatRules.Evaluate(settings, hy);
        Assert.True(compat.IsDisabled("mux"));
        Assert.False(compat.Effective.Dpi.Mux);

        // В окне сервера у них нет выбора транспорта.
        Assert.Contains("transport.ws", ProfileCompat.DisabledOptions(ss).Keys);
        Assert.Contains("security.none", ProfileCompat.DisabledOptions(hy).Keys);
    }

    [Fact]
    public void Hysteria2_never_goes_to_xray()
    {
        var hy = Ok("hysteria2://pw@1.2.3.4:443#hy");
        var ss = Ok("ss://aes-128-gcm:pw@1.2.3.4:8388#ss");
        var plan = CorePlan.Make(new AppSettings { Cores = new CoreSettings { CoreChoice = CoreChoice.Xray } }, [hy, ss]);
        Assert.Equal([ss.Id], plan.XrayProfiles.Select(p => p.Id));
    }
}

public sealed class SettingsValidationTests
{
    [Theory]
    [InlineData("100-200", true)]
    [InlineData("5-5", true)]
    [InlineData("200-100", false)]
    [InlineData("abc", false)]
    [InlineData("1-99999", false)]
    public void Range(string value, bool ok) => Assert.Equal(ok, SettingsValidation.Range(value, 1, 1000) is null);

    [Theory]
    [InlineData("Ctrl+Alt+P", true)]
    [InlineData("ctrl+shift+F5", true)]
    [InlineData("", true)]
    [InlineData("P", false)]
    [InlineData("Ctrl+Alt+Delete", false)]
    public void Hotkey(string value, bool ok) => Assert.Equal(ok, SettingsValidation.Hotkey(value) is null);

    [Theory]
    [InlineData("https://1.1.1.1/dns-query", true)]
    [InlineData("tls://8.8.8.8", true)]
    [InlineData("77.88.8.8", true)]
    [InlineData("dns.example.com", false)]
    public void Dns(string value, bool ok) => Assert.Equal(ok, SettingsValidation.Dns(value) is null);

    [Fact]
    public void Ports_and_misc()
    {
        Assert.Null(SettingsValidation.Port(10880, 10881));
        Assert.NotNull(SettingsValidation.Port(10881, 10881));
        Assert.NotNull(SettingsValidation.Port(80));
        Assert.Null(SettingsValidation.HostPort("stun.l.google.com:19302"));
        Assert.NotNull(SettingsValidation.HostPort("stun.l.google.com"));
        Assert.Null(SettingsValidation.Hosts("# мой роутер\nrouter.lan 192.168.1.1\n10.0.0.2 nas.lan"));
        Assert.NotNull(SettingsValidation.Hosts("router.lan"));
        Assert.Null(SettingsValidation.FragPackets("tlshello"));
        Assert.Null(SettingsValidation.FragPackets("1-3"));
        Assert.NotNull(SettingsValidation.SysBypass("localhost;\"evil\""));
        Assert.NotNull(SettingsValidation.HttpUrl("http://speed.example", httpsOnly: true));
    }
}

public sealed class SettingsBackupTests
{
    [Fact]
    public void Export_has_no_machine_state_and_round_trips()
    {
        var server = Guid.NewGuid();
        var settings = new AppSettings
        {
            Connection = new ConnectionSettings { SocksPort = 20000, UwpLoopback = ["S-1-15-2-1-2-3"] },
            Cores = new CoreSettings { LastManifestSequence = 9 },
            Routing = new RoutingSettings
            {
                Rules =
                [
                    new Rule { Match = new RuleMatch { Processes = ["Discord.exe"] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Direct },
                    new Rule { Match = new RuleMatch { DomainSuffixes = ["x.com"] }, Tcp = new RuleAction(RuleActionKind.Server, server), Udp = RuleAction.Block },
                ],
            },
        };
        var json = SettingsBackup.Export(settings, DateTimeOffset.UnixEpoch);
        Assert.DoesNotContain("S-1-15-2", json, StringComparison.Ordinal);
        Assert.DoesNotContain(server.ToString(), json, StringComparison.Ordinal);

        var current = new AppSettings { Cores = new CoreSettings { LastManifestSequence = 12 } };
        var (imported, warnings) = SettingsBackup.Import(json, current, new HashSet<Guid>());
        Assert.Empty(warnings);
        Assert.Equal(20000, imported.Connection.SocksPort);
        Assert.Equal(12, imported.Cores.LastManifestSequence); // защита от отката не из файла
        Assert.Equal(2, imported.Routing.Rules.Count);
        Assert.Equal(RuleActionKind.Proxy, imported.Routing.Rules[1].Tcp.Kind);
    }

    [Fact]
    public void Bad_values_are_replaced_with_warnings()
    {
        var json = SettingsBackup.Export(new AppSettings(), DateTimeOffset.UnixEpoch)
            .Replace("\"socksPort\": 10880", "\"socksPort\": 80", StringComparison.Ordinal)
            .Replace("\"remoteDns\": \"https://1.1.1.1/dns-query\"", "\"remoteDns\": \"evil.example\"", StringComparison.Ordinal);
        var current = new AppSettings();
        var (imported, warnings) = SettingsBackup.Import(json, current, new HashSet<Guid>());
        Assert.Equal(10880, imported.Connection.SocksPort);
        Assert.Equal(current.Dns.RemoteDns, imported.Dns.RemoteDns);
        Assert.Equal(2, warnings.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"kind\":\"other\",\"schema\":1,\"settings\":{}}")]
    [InlineData("{\"kind\":\"tropa-settings\",\"schema\":99,\"settings\":{}}")]
    public void Foreign_files_are_refused(string json) =>
        Assert.Throws<InvalidDataException>(() => SettingsBackup.Import(json, new AppSettings(), new HashSet<Guid>()));
}
