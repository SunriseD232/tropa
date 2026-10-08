using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Security;
using static Tropa.Core.Tests.Parsing.ShareLinkTests;

namespace Tropa.Core.Tests.Generation;

/// <summary>
/// Эталонные конфиги Xray. «xray -test» неизвестные поля не ловит, поэтому главная проверка схемы —
/// сквозные тесты с настоящим Xray-сервером (Tropa.Integration.Tests/HybridTests).
/// </summary>
public sealed class XrayGoldenTests
{
    private static readonly LocalAuth Auth = new("xray-user", "xray-pass");
    private static readonly AppSettings Defaults = new();

    private static Profile WithId(Profile p, int n) => p with { Id = new Guid(n, 0, 0, new byte[8]) };

    private static readonly Profile Xhttp = SingBoxGoldenTests.Xhttp;
    private static readonly Profile Ws = WithId(Ok($"vless://{Uuid}@cdn.example.com:443?type=ws&path=%2Fws&host=cdn.example.com&security=tls&sni=cdn.example.com&alpn=h2,http/1.1#WS"), 7);
    private static readonly Profile Trojan = WithId(Ok("trojan://pw@fi.example.com:443?type=grpc&serviceName=svc&sni=fi.example.com#FI"), 8);
    private static readonly Profile Vision = WithId(Ok(VlessReality), 9);

    public static TheoryData<string> Scenarios => new(All.Keys);

    private static readonly Dictionary<string, Func<string>> All = new()
    {
        ["x01-servers"] = () => XrayConfigBuilder.Build(
            [new(Xhttp, 31200), new(Ws, 31201), new(Trojan, 31202), new(Vision, 31203)], Defaults, Auth),
        ["x02-direct-noise-fragment"] = () => XrayConfigBuilder.Build(
            [], Defaults with { Dpi = Defaults.Dpi with { Fragment = true, Noise = true, NoiseLen = "5-10" } }, Auth, directPort: 31210),
        ["x04-shadowsocks"] = () => XrayConfigBuilder.Build(
            [new(SingBoxGoldenTests.Ss2022, 31220)], Defaults, Auth),
        ["x03-mux"] = () => XrayConfigBuilder.Build(
            [new(Ws, 31201), new(Vision, 31203)], Defaults with { Dpi = Defaults.Dpi with { Mux = true, MuxConc = 4 } }, Auth),
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Matches_golden(string scenario)
    {
        var actual = All[scenario]();
        var path = Path.Combine(SingBoxGoldenTests.GoldenDirectory, scenario + ".xray.json");
        if (Environment.GetEnvironmentVariable("TROPA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Нет эталона {path}. Запустите тесты с TROPA_UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void Generated_configs_pass_guard(string scenario) => Assert.Empty(XrayConfigGuard.Check(All[scenario]()));

    [Fact]
    public void Vision_disables_mux_per_server()
    {
        var json = All["x03-mux"]();
        // Mux есть у WS, но не у Vision: у них один «concurrency».
        Assert.Equal(1, json.Split("\"concurrency\"").Length - 1);
    }

    [Fact]
    public void Chains_are_refused_for_xray() =>
        Assert.Throws<UnsupportedProfileException>(() =>
            XrayConfigBuilder.Build([new(Xhttp with { ChainVia = Ws.Id }, 1)], Defaults, Auth));

    private static string Base => All["x01-servers"]();

    public static TheoryData<string, string> Malicious => new()
    {
        { "env", Base.Replace("\"log\": {", "\"env\": { \"PATH\": \"C:\\\\evil\" }, \"log\": {", StringComparison.Ordinal) },
        { "лог в файл", Base.Replace("\"loglevel\": \"warning\"", "\"loglevel\": \"warning\", \"access\": \"C:\\\\Windows\\\\x.log\"", StringComparison.Ordinal) },
        { "api", Base.Replace("\"log\": {", "\"api\": { \"tag\": \"api\" }, \"log\": {", StringComparison.Ordinal) },
        { "порт наружу", Base.Replace("\"listen\": \"127.0.0.1\"", "\"listen\": \"0.0.0.0\"", StringComparison.Ordinal) },
        { "без пароля", Base.Replace("\"auth\": \"password\"", "\"auth\": \"noauth\"", StringComparison.Ordinal) },
        { "dokodemo", Base.Replace("\"protocol\": \"socks\"", "\"protocol\": \"dokodemo-door\"", StringComparison.Ordinal) },
        { "сертификат с диска", Base.Replace("\"allowInsecure\": false", "\"allowInsecure\": false, \"certificates\": [ { \"certificateFile\": \"C:\\\\x.pem\" } ]", StringComparison.Ordinal) },
        { "неизвестный выход", Base.Replace("\"protocol\": \"blackhole\"", "\"protocol\": \"dokodemo-door\"", StringComparison.Ordinal) },
        { "битый JSON", "{" },
        { "DNS из файла", Base.Replace("\"log\": {", "\"dns\": { \"servers\": [ \"file:///C:/x\" ] }, \"log\": {", StringComparison.Ordinal) },
        { "DNS hosts", Base.Replace("\"log\": {", "\"dns\": { \"hosts\": { \"a\": \"1.2.3.4\" } }, \"log\": {", StringComparison.Ordinal) },
        { "DNS по http", Base.Replace("\"log\": {", "\"dns\": { \"servers\": [ \"http://1.1.1.1/dns-query\" ] }, \"log\": {", StringComparison.Ordinal) },
    };

    [Theory]
    [MemberData(nameof(Malicious))]
    public void Malicious_xray_configs_are_rejected(string name, string json) =>
        Assert.True(XrayConfigGuard.Check(json).Count > 0, name);
}
