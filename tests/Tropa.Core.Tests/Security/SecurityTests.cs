using Tropa.Core.Security;
using Tropa.Core.Tests.Generation;
using static Tropa.Core.Tests.Parsing.ShareLinkTests;

namespace Tropa.Core.Tests.Security;

public sealed class SecretScrubberTests
{
    [Theory]
    [InlineData(VlessReality)]
    [InlineData(VmessWs)]
    [InlineData("trojan://hunter2@fi.example.com:443?sni=x#t")]
    public void Share_links_are_masked(string link)
    {
        var scrubbed = SecretScrubber.PatternsOnly.Scrub($"импорт: {link} готово");
        Assert.DoesNotContain(link, scrubbed, StringComparison.Ordinal);
        Assert.Contains("://***", scrubbed, StringComparison.Ordinal);
        Assert.EndsWith("готово", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Uuids_and_reality_keys_are_masked()
    {
        var scrubbed = SecretScrubber.PatternsOnly.Scrub($"user {Uuid} key {Pbk} done");
        Assert.DoesNotContain(Uuid, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(Pbk, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"password\": \"hunter2\"", "hunter2")]
    [InlineData("password=hunter2&x=1", "hunter2")]
    [InlineData("\"short_id\":\"a1b2c3\"", "a1b2c3")]
    [InlineData("secret: s3cr3t", "s3cr3t")]
    public void Key_value_secrets_are_masked(string text, string secret) =>
        Assert.DoesNotContain(secret, SecretScrubber.PatternsOnly.Scrub(text), StringComparison.Ordinal);

    [Fact]
    public void Subscription_url_tokens_are_masked_but_plain_urls_kept()
    {
        var s = SecretScrubber.PatternsOnly.Scrub("GET https://panel.example.com/sub/AbCdEf0123456789xyz ok; test https://www.gstatic.com/generate_204");
        Assert.DoesNotContain("AbCdEf0123456789xyz", s, StringComparison.Ordinal);
        Assert.Contains("https://panel.example.com/***", s, StringComparison.Ordinal);
        Assert.Contains("https://www.gstatic.com/generate_204", s, StringComparison.Ordinal);
    }

    [Fact]
    public void Known_secrets_are_replaced_even_without_pattern()
    {
        var scrubber = new SecretScrubber(["my odd phrase", "abc"]); // слишком короткие значения не учитываются
        Assert.Equal("note: *** abc", scrubber.Scrub("note: my odd phrase abc"));
    }

    [Fact]
    public void Golden_configs_contain_no_secrets_after_scrubbing()
    {
        foreach (var file in Directory.EnumerateFiles(SingBoxGoldenTests.GoldenDirectory, "*.json"))
        {
            var scrubbed = SecretScrubber.PatternsOnly.Scrub(File.ReadAllText(file));
            Assert.DoesNotContain(Uuid, scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain(Pbk, scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain("tropa-pass", scrubbed, StringComparison.Ordinal);
            Assert.DoesNotContain("test-secret", scrubbed, StringComparison.Ordinal);
        }
    }
}

public sealed class ConfigGuardTests
{
    private static readonly GuardPolicy Policy = new() { AllowedDirectories = [SingBoxGoldenTests.GeoDir] };

    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(SingBoxGoldenTests.GoldenDirectory, "*.singbox.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void Generated_configs_pass_guard(string file)
    {
        var json = File.ReadAllText(Path.Combine(SingBoxGoldenTests.GoldenDirectory, file));
        var lan = json.Contains("\"0.0.0.0\"", StringComparison.Ordinal);
        Assert.Empty(ConfigGuard.CheckSingBox(json, Policy with { AllowLanInbound = lan }));
    }

    private const string Base = """
    {
      "log": { "level": "warn" },
      "dns": { "servers": [ { "type": "udp", "tag": "local", "server": "77.88.8.8" } ], "final": "local" },
      "inbounds": [ { "type": "mixed", "tag": "in", "listen": "127.0.0.1", "listen_port": 10808 } ],
      "outbounds": [ { "type": "direct", "tag": "direct" } ],
      "route": { "rules": [], "final": "direct" }
    }
    """;

    private const string DirectOutbound = """{ "type": "direct", "tag": "direct" }""";
    private const string LocalDns = """{ "type": "udp", "tag": "local", "server": "77.88.8.8" }""";

    private static string With(string from, string to) => Base.Replace(from, to, StringComparison.Ordinal);

    [Fact]
    public void Minimal_config_passes() => Assert.Empty(ConfigGuard.CheckSingBox(Base, Policy));

    public static TheoryData<string, string> Malicious => new()
    {
        { "запись лога в файл", With("\"level\": \"warn\"", """ "level": "warn", "output": "C:\\Windows\\evil.dll" """) },
        { "порт наружу", With("127.0.0.1", "0.0.0.0") },
        { "неизвестный раздел", With("\"log\":", """ "endpoints": [], "log": """) },
        { "удалённый набор правил", With("\"rules\": []", """ "rule_set": [ { "type": "remote", "tag": "x", "url": "https://evil.example/x.srs" } ], "rules": [] """) },
        { "набор правил вне каталога", With("\"rules\": []", """ "rule_set": [ { "type": "local", "tag": "x", "path": "C:\\tropa-test-geo\\..\\Windows\\x.srs" } ], "rules": [] """) },
        { "набор правил по UNC", With("\"rules\": []", """ "rule_set": [ { "type": "local", "tag": "x", "path": "\\\\evil\\share\\x.srs" } ], "rules": [] """) },
        { "API наружу", With("\"route\":", """ "experimental": { "clash_api": { "external_controller": "0.0.0.0:9090", "secret": "s" } }, "route": """) },
        { "веб-панель API", With("\"route\":", """ "experimental": { "clash_api": { "external_controller": "127.0.0.1:9090", "secret": "s", "external_ui": "ui" } }, "route": """) },
        { "API без секрета", With("\"route\":", """ "experimental": { "clash_api": { "external_controller": "127.0.0.1:9090" } }, "route": """) },
        { "v2ray api", With("\"route\":", """ "experimental": { "v2ray_api": {} }, "route": """) },
        { "путь к сертификату", With(DirectOutbound, """{ "type": "trojan", "tag": "t", "server": "a", "server_port": 1, "password": "p", "tls": { "enabled": true, "certificate_path": "C:\\x.pem" } }, """ + DirectOutbound) },
        { "неизвестный outbound", With("\"type\": \"direct\"", "\"type\": \"tor\"") },
        { "ссылка в никуда", With("\"final\": \"direct\"", "\"final\": \"proxy\"") },
        { "socks на чужой сервер", With(DirectOutbound, """{ "type": "socks", "tag": "s", "server": "evil.example", "server_port": 1080 }, """ + DirectOutbound) },
        { "чтение hosts-файла", With(LocalDns, LocalDns + """, { "type": "hosts", "tag": "h", "path": ["C:\\Windows\\System32\\drivers\\etc\\hosts"] }""") },
        { "битый JSON", "{ not json" },
    };

    [Theory]
    [MemberData(nameof(Malicious))]
    public void Malicious_configs_are_rejected(string name, string json) =>
        Assert.True(ConfigGuard.CheckSingBox(json, Policy).Count > 0, name);

    [Fact]
    public void Lan_inbound_requires_policy_and_password()
    {
        var open = With("127.0.0.1", "0.0.0.0");
        Assert.NotEmpty(ConfigGuard.CheckSingBox(open, Policy with { AllowLanInbound = true })); // без пароля
        var withUsers = open.Replace("\"listen_port\": 10808", """ "listen_port": 10808, "users": [ { "username": "u", "password": "p" } ] """, StringComparison.Ordinal);
        Assert.Empty(ConfigGuard.CheckSingBox(withUsers, Policy with { AllowLanInbound = true }));
        Assert.NotEmpty(ConfigGuard.CheckSingBox(withUsers, Policy));
    }

    [Theory]
    [InlineData(@"C:\tropa-test-geo\a.srs", true)]
    [InlineData(@"C:\tropa-test-geo\sub\a.srs", true)]
    [InlineData(@"C:\tropa-test-geo-evil\a.srs", false)]
    [InlineData(@"C:\tropa-test-geo\..\a.srs", false)]
    [InlineData(@"C:\tropa-test-geo\a.srs:stream", false)]
    [InlineData(@"\\?\C:\tropa-test-geo\a.srs", false)]
    [InlineData(@"tropa-test-geo\a.srs", false)]
    [InlineData("", false)]
    public void Path_containment(string path, bool allowed) =>
        Assert.Equal(allowed, ConfigGuard.IsWithin(path, [SingBoxGoldenTests.GeoDir]));
}
