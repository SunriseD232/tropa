using System.Text.Encodings.Web;
using System.Text.Json;
using Tropa.Core.Model;
using Tropa.Core.Telemetry;
using Tropa.Core.Tests.Cores;

namespace Tropa.Core.Tests.Telemetry;

public sealed class TelemetryTests
{
    private static readonly JsonSerializerOptions CodesJson = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Fact]
    public void Codes_are_unique_and_well_formed()
    {
        var all = Ev.Catalog.Concat(NetLogClassifier.Catalog).ToList();
        Assert.Equal(all.Count, all.Select(c => c.Code).Distinct().Count());
        Assert.All(all, c => Assert.Matches(@"^\d{1,2}\.\d{1,3}$", c.Code));
        // Каждая константа Ev есть в каталоге.
        var constants = typeof(Ev).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet();
        Assert.Subset(all.Select(c => c.Code).ToHashSet(), constants);
    }

    /// <summary>Расшифровка для сервера журнала: tools/tropa-telemetry-codes.json. Обновить — TROPA_UPDATE_GOLDEN=1.</summary>
    [Fact]
    public void Server_codes_file_matches_catalog()
    {
        var codes = Ev.Catalog.Concat(NetLogClassifier.Catalog).ToDictionary(c => c.Code, c => c.Text);
        var json = JsonSerializer.Serialize(new Dictionary<string, object> { ["schema"] = 1, ["codes"] = codes },
            CodesJson) + "\n";
        var path = Path.Combine(CoresLockTests.RepoRoot(), "tools", "tropa-telemetry-codes.json");
        if (Environment.GetEnvironmentVariable("TROPA_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, json);
            return;
        }

        Assert.True(File.Exists(path), "Нет tools/tropa-telemetry-codes.json. Запустите тесты с TROPA_UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), json.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Settings_diff_names_changed_keys()
    {
        var before = new AppSettings();
        var after = before with
        {
            General = before.General with { KillSwitch = true },
            Connection = before.Connection with { Mode = CaptureMode.SystemProxy },
        };
        var diff = SettingsFlattener.Diff(before, after);
        Assert.Equal(2, diff.Count);
        Assert.Equal("false → true", diff["general.killSwitch"]);
        Assert.Equal("Tun → SystemProxy", diff["connection.mode"]);
        Assert.Empty(SettingsFlattener.Diff(before, before with { }));
    }

    [Fact]
    public void Private_settings_are_not_flattened_as_text()
    {
        var s = new AppSettings
        {
            Dns = new DnsSettings { Hosts = "secret.example.com 10.0.0.1" },
            Telemetry = new TelemetrySettings { Name = "Вася" },
            Routing = new RoutingSettings
            {
                Rules = [new Rule { Match = new RuleMatch { Domains = ["my-private-site.example"] }, Tcp = RuleAction.Direct, Udp = RuleAction.Direct }],
            },
        };
        var flat = SettingsFlattener.Flatten(s);
        var text = string.Join("\n", flat.Select(kv => kv.Key + "=" + kv.Value));
        Assert.DoesNotContain("secret.example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("my-private-site", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Вася", text, StringComparison.Ordinal);
        Assert.StartsWith("[1] #", flat["routing.rules"], StringComparison.Ordinal);
        Assert.False(flat.ContainsKey("cores.lastUpdateCheck"));
    }

    [Theory]
    [InlineData("+0300 2026-10-30 12:00:01 ERROR [123456 5.2s] outbound/vless[proxy]: dial tcp 185.253.45.124:443: i/o timeout", NetLogClassifier.Timeout, "sing-box")]
    [InlineData("2026/10/30 12:00:01 [Warning] [1234] app/proxyman/outbound: failed to process outbound traffic > proxy/vless/outbound: failed to find an available destination > dial tcp 1.2.3.4:443: connectex: No connection could be made because the target machine actively refused it.", NetLogClassifier.Refused, "xray")]
    [InlineData("ERROR [1 2ms] outbound/vless[proxy]: read tcp 10.0.0.2:51234->5.6.7.8:443: wsarecv: An existing connection was forcibly closed by the remote host.", NetLogClassifier.Reset, "sing-box")]
    [InlineData("ERROR [7 0ms] dns: exchange failed for www.youtube.com. IN A: context deadline exceeded", NetLogClassifier.Dns, "sing-box")]
    [InlineData("2026/10/30 12:00:01 [Warning] [9] transport/internet/tls: tls: handshake failure", NetLogClassifier.Tls, "xray")]
    [InlineData("ERROR [3 1ms] outbound/vless[proxy]: REALITY: processed invalid connection", NetLogClassifier.Reality, "sing-box")]
    [InlineData("FATAL[0000] start service: start inbound/tun[tun-in]: configure tun interface: Cannot create a file when that file already exists.", NetLogClassifier.Fatal, "sing-box")]
    [InlineData("ERROR [4 0ms] inbound/mixed[mixed-in]: listen tcp 127.0.0.1:10880: bind: Only one usage of each socket address is normally permitted.", NetLogClassifier.PortBusy, "sing-box")]
    [InlineData("ERROR [5 0ms] outbound/direct[direct]: dial udp 8.8.8.8:53: connect: network is unreachable", NetLogClassifier.Unreachable, "sing-box")]
    [InlineData("ERROR [5 0ms] outbound/direct[direct]: dial tcp 9.9.9.9:443: connect: network is unreachable", NetLogClassifier.Unreachable, "sing-box")]
    public void Network_lines_are_classified(string line, string code, string source)
    {
        var c = NetLogClassifier.Classify(line);
        Assert.NotNull(c);
        Assert.Equal(code, c.Code);
        Assert.Equal(source, c.Source);
    }

    [Fact]
    public void Templates_hide_addresses_and_group_same_errors()
    {
        var a = NetLogClassifier.Template("+0300 2026-10-30 12:00:01 ERROR [123456 5.2s] outbound/vless[proxy]: dial tcp 185.253.45.124:443: i/o timeout");
        var b = NetLogClassifier.Template("+0300 2026-10-31 09:15:44 ERROR [99 1.1s] outbound/vless[proxy]: dial tcp 85.159.231.149:8443: i/o timeout");
        Assert.Equal(a, b);
        Assert.DoesNotContain("185.253", a, StringComparison.Ordinal);

        var site = NetLogClassifier.Template("ERROR [7 0ms] dns: exchange failed for private-site.example.org. IN A: timeout");
        Assert.DoesNotContain("private-site", site, StringComparison.Ordinal);
        var v6 = NetLogClassifier.Template("ERROR dial tcp [2a01:4f8:c0c:1234::1]:443: i/o timeout");
        Assert.DoesNotContain("2a01", v6, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_lines_are_not_network_errors()
    {
        Assert.Null(NetLogClassifier.Classify("Сеть сменилась — переподключаюсь."));
        Assert.Null(NetLogClassifier.Classify("INFO [1 0ms] inbound/mixed[mixed-in]: inbound connection to www.youtube.com:443"));
        Assert.True(NetLogClassifier.IsCoreLine("INFO [1 0ms] inbound/mixed[mixed-in]: inbound connection to www.youtube.com:443"));
        Assert.False(NetLogClassifier.IsCoreLine("Сеть сменилась — переподключаюсь."));
    }

    [Fact]
    public void Anonymize_keeps_ports_but_hides_hosts()
    {
        var s = NetLogClassifier.Anonymize("Порт 10880 занят. Сервер example.com:443 и 1.2.3.4 не ответили.");
        Assert.Contains("10880", s, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", s, StringComparison.Ordinal);
        Assert.DoesNotContain("1.2.3.4", s, StringComparison.Ordinal);
    }
}
