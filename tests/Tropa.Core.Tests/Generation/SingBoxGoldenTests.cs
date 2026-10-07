using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Tests.Cores;
using static Tropa.Core.Tests.Parsing.ShareLinkTests;

namespace Tropa.Core.Tests.Generation;

/// <summary>
/// Эталонные конфиги sing-box (docs/05-config-generation.md, §4).
/// Обновить эталоны после осознанного изменения генератора:
/// <c>TROPA_UPDATE_GOLDEN=1 dotnet test</c>, затем просмотреть diff.
/// Каждый эталон дополнительно проверяется настоящим «sing-box check» в Tropa.Integration.Tests.
/// </summary>
public sealed class SingBoxGoldenTests
{
    /// <summary>Каталог гео-баз в эталонах; интеграционный тест подменяет его на временный.</summary>
    public const string GeoDir = @"C:\tropa-test-geo";

    private static readonly LocalAuth Auth = new("tropa-user", "tropa-pass");

    private static Profile WithId(Profile p, int n) => p with { Id = new Guid(n, 0, 0, new byte[8]) };

    private static readonly Profile Reality = WithId(Ok(VlessReality), 1);
    private static readonly Profile VmessWsProfile = WithId(Ok(VmessWs), 2);
    private static readonly Profile Trojan = WithId(Ok("trojan://pw@fi.example.com:443?sni=fi.example.com&alpn=h2,http/1.1#Helsinki"), 3);
    private static readonly Profile Relay = WithId(Ok($"vless://{Uuid}@relay.example.ru:443?security=reality&sni=ya.ru&pbk={Pbk}&sid=01#Relay RU"), 4);
    private static readonly Profile Grpc = WithId(Ok($"vless://{Uuid}@us.example.com:443?type=grpc&serviceName=svc&security=tls&sni=us.example.com#NY"), 5);

    internal static readonly Profile Xhttp = WithId(Ok($"vless://{Uuid}@de.example.com:443?type=xhttp&path=%2Fx&mode=packet-up&security=reality&sni=www.example.org&fp=firefox&pbk={Pbk}&sid=a1b2#DE%20XHTTP"), 6);
    private static readonly LocalAuth XrayAuth = new("xray-user", "xray-pass");

    internal static readonly Profile Ss2022 = WithId(Ok("ss://2022-blake3-aes-128-gcm:AAECAwQFBgcICQoLDA0ODw%3D%3D@ss.example.com:8388#SS-2022"), 14);
    internal static readonly Profile Hy2 = WithId(Ok("hysteria2://hy2pass@hy.example.com:443?sni=hy.example.com&obfs=salamander&obfs-password=obfspw#HY2"), 15);

    private static readonly AppSettings Defaults = new();

    public static TheoryData<string> Scenarios => new(All.Keys);

    private static readonly Dictionary<string, Func<SingBoxInput>> All = new()
    {
        ["01-reality-vision-tun-except-ru"] = () => Input(Defaults, Reality),

        // Shadowsocks-2022 активный, Hysteria2 в группе авто-выбора; Mux включён — у Hysteria2 его быть не должно.
        ["12-shadowsocks-hysteria2"] = () => Input(
            Defaults with { Dpi = Defaults.Dpi with { Mux = true } }, Ss2022, [Ss2022, Hy2], [Ss2022, Hy2]),

        ["02-reality-vision-system-proxy"] = () => Input(
            Defaults with { Connection = Defaults.Connection with { Mode = CaptureMode.SystemProxy } }, Reality),

        ["03-vmess-ws-blocked-only"] = () => Input(
            Defaults with
            {
                Routing = Defaults.Routing with { Preset = RoutePreset.BlockedOnly },
                Dns = Defaults.Dns with { LocalDnsRule = LocalDnsRule.DirectRules },
            },
            VmessWsProfile),

        ["04-trojan-app-rules-tcp-udp"] = () => Input(
            Defaults with
            {
                Routing = Defaults.Routing with
                {
                    Rules =
                    [
                        new Rule
                        {
                            Id = new Guid(10, 0, 0, new byte[8]),
                            Match = new RuleMatch { Processes = ["VALORANT.exe"] },
                            Tcp = RuleAction.Proxy,
                            Udp = RuleAction.Direct,
                        },
                        new Rule
                        {
                            Id = new Guid(11, 0, 0, new byte[8]),
                            Match = new RuleMatch { Processes = ["qbittorrent.exe"] },
                            Tcp = RuleAction.Block,
                            Udp = RuleAction.Block,
                        },
                        new Rule
                        {
                            Id = new Guid(12, 0, 0, new byte[8]),
                            Match = new RuleMatch { DomainSuffixes = ["discord.com", "discord.gg"], GeoSite = ["discord"], Ports = [PortRange.Of(443), new PortRange(50000, 65535)] },
                            Tcp = new RuleAction(RuleActionKind.Server, Reality.Id),
                            Udp = new RuleAction(RuleActionKind.Server, Reality.Id),
                        },
                        new Rule
                        {
                            Id = new Guid(13, 0, 0, new byte[8]),
                            Enabled = false,
                            Match = new RuleMatch { Domains = ["disabled.example.com"] },
                            Tcp = RuleAction.Block,
                            Udp = RuleAction.Block,
                        },
                    ],
                },
            },
            Trojan, profiles: [Trojan, Reality]),

        ["05-chain-relay"] = () => Input(Defaults, Reality with { ChainVia = Relay.Id }, profiles: [Reality, Relay]),

        ["06-auto-select"] = () => Input(Defaults, Reality, profiles: [Reality, Trojan, Grpc], group: [Reality, Trojan, Grpc]),

        ["07-dpi-fragment-udp-direct"] = () => Input(
            Defaults with
            {
                Dpi = Defaults.Dpi with { DpiPreset = DpiPreset.Soft, Fragment = true, FragScope = FragmentScope.List },
                Routing = Defaults.Routing with { BlockQuic = false, UdpProxy = false, Preset = RoutePreset.All },
                General = Defaults.General with { Ipv6Block = false },
                Dns = Defaults.Dns with { Fakeip = false, Sniffing = true, LocalDnsRule = LocalDnsRule.None },
                Expert = Defaults.Expert with { SniffQuic = true, LogLevel = CoreLogLevel.Info },
            },
            Grpc),

        ["08-lan-hosts-custom-dns"] = () => Input(
            Defaults with
            {
                Connection = Defaults.Connection with { LanAllow = true, Mode = CaptureMode.SystemProxy, SocksPort = 20808 },
                General = Defaults.General with { LocalPass = true }, // в режиме прокси будет принудительно выключен (ADR-013)
                Dns = Defaults.Dns with
                {
                    RemoteDns = "https://dns.google/dns-query",
                    LocalDns = "tls://77.88.8.8",
                    Hosts = "router.local 192.168.1.1\n# комментарий\n10.0.0.5 nas.local\nbroken line here",
                    DnsCache = false,
                },
            },
            Reality, clashApi: false),

        // Гибрид: сервер с XHTTP обслуживает Xray, sing-box — фронт с TUN. Трафик xray.exe идёт напрямую.
        ["10-hybrid-xhttp-tun"] = () => Input(Defaults, Xhttp, profiles: [Xhttp, Reality], group: [Xhttp, Reality]) with
        {
            XrayPorts = new Dictionary<Guid, int> { [Xhttp.Id] = 31200 },
            XrayAuth = XrayAuth,
            XrayPath = @"C:\Program Files\Tropa\cores\xray.exe",
        },

        // Шум: прямой UDP — через Xray, прямой TCP — в sing-box с фрагментацией.
        ["11-noise-direct-udp"] = () => Input(
            Defaults with
            {
                Dpi = Defaults.Dpi with { DpiPreset = DpiPreset.Hard, Fragment = true, FragScope = FragmentScope.All, Noise = true },
                Routing = Defaults.Routing with { Preset = RoutePreset.BlockedOnly },
            },
            Reality) with
        {
            XrayDirectPort = 31201,
            XrayAuth = XrayAuth,
            XrayPath = @"C:\Program Files\Tropa\cores\xray.exe",
        },
    };


    private static SingBoxInput Input(AppSettings settings, Profile active, IReadOnlyList<Profile>? profiles = null,
        IReadOnlyList<Profile>? group = null, bool clashApi = true) => new()
    {
        Settings = settings,
        Active = active,
        Profiles = profiles ?? [active],
        AutoSelectGroup = group ?? [],
        RuleSetDirectory = GeoDir,
        LocalAuth = Auth,
        ClashApiPort = clashApi ? 19090 : null,
        ClashApiSecret = clashApi ? "test-secret" : null,
    };

    public static string GoldenDirectory =>
        Path.Combine(CoresLockTests.RepoRoot(), "tests", "Tropa.Core.Tests", "Generation", "Golden");

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Matches_golden(string scenario) => AssertGolden(scenario, SingBoxConfigBuilder.Build(All[scenario]()));

    [Fact]
    public void Test_instance_matches_golden()
    {
        var json = SingBoxConfigBuilder.BuildTest(
            [new(Reality with { ChainVia = Relay.Id }, 31001), new(VmessWsProfile, 31002), new(Trojan, 31003)],
            Defaults, Auth, [Reality, Relay, VmessWsProfile, Trojan], pingPort: 31000);
        AssertGolden("09-test-instance", json);
    }

    private static void AssertGolden(string scenario, string actual)
    {
        var path = Path.Combine(GoldenDirectory, scenario + ".singbox.json");
        if (Environment.GetEnvironmentVariable("TROPA_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(GoldenDirectory);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Нет эталона {path}. Запустите тесты с TROPA_UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Xhttp_requires_xray()
    {
        var xhttp = Ok($"vless://{Uuid}@de.example.com:443?type=xhttp&path=%2Fx&security=tls&sni=de.example.com#de");
        var ex = Assert.Throws<UnsupportedProfileException>(() => SingBoxConfigBuilder.Build(Input(Defaults, xhttp)));
        Assert.Contains("Xray", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Incompatible_profile_is_refused()
    {
        var bad = Ok($"vless://{Uuid}@a.example.com:443?flow=xtls-rprx-vision&type=ws&security=tls&sni=a.example.com#x");
        Assert.Throws<UnsupportedProfileException>(() => SingBoxConfigBuilder.Build(Input(Defaults, bad)));
    }

    [Fact]
    public void Process_rules_are_skipped_outside_tun()
    {
        var settings = Defaults with
        {
            Connection = Defaults.Connection with { Mode = CaptureMode.SystemProxy },
            Routing = Defaults.Routing with
            {
                Rules = [new Rule { Match = new RuleMatch { Processes = ["Discord.exe"] }, Tcp = RuleAction.Block, Udp = RuleAction.Block }],
            },
        };
        var json = SingBoxConfigBuilder.Build(Input(settings, Reality));
        Assert.DoesNotContain("Discord.exe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("find_process", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/ws?ed=2048", "/ws", 2048)]
    [InlineData("/ws?ed=2048&x=1", "/ws?x=1", 2048)]
    [InlineData("/ws", "/ws", 0)]
    [InlineData("/ws?ed=abc", "/ws?ed=abc", 0)]
    public void Early_data_is_split_from_path(string input, string path, int ed) =>
        Assert.Equal((path, ed), SingBoxConfigBuilder.SplitEarlyData(input));
}
