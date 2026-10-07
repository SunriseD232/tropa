using System.Net;
using Tropa.Core.Diagnostics;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Core.Security;

namespace Tropa.Core.Tests.Diagnostics;

public sealed class DnsMessageTests
{
    /// <summary>Ответ на A-запрос с указателем на имя из вопроса (как отвечают настоящие серверы).</summary>
    private static byte[] Response(byte[] query, int rcode, params IPAddress[] answers)
    {
        var r = new List<byte>(query);
        r[2] = 0x81;
        r[3] = (byte)(0x80 | rcode);
        r[7] = (byte)answers.Length;
        foreach (var a in answers)
        {
            var bytes = a.GetAddressBytes();
            r.AddRange([0xC0, 0x0C, 0, (byte)(bytes.Length == 4 ? 1 : 28), 0, 1, 0, 0, 0, 60, 0, (byte)bytes.Length]);
            r.AddRange(bytes);
        }

        return [.. r];
    }

    [Fact]
    public void Query_and_answer_round_trip()
    {
        var q = DnsMessage.BuildQuery("Discord.com", DnsMessage.TypeA, 0x1234);
        Assert.Equal(0x12, q[0]);
        Assert.Equal(0x34, q[1]);
        var answer = DnsMessage.Parse(Response(q, 0, IPAddress.Parse("162.159.128.233"), IPAddress.Parse("162.159.137.232")), 0x1234);
        Assert.NotNull(answer);
        Assert.Equal(["162.159.128.233", "162.159.137.232"], answer.Addresses.Select(a => a.ToString()));
        Assert.False(answer.IsNxDomain);
    }

    [Fact]
    public void Idn_domain_is_encoded()
    {
        var q = DnsMessage.BuildQuery("пример.рф", DnsMessage.TypeA, 1);
        Assert.Contains("xn--", System.Text.Encoding.ASCII.GetString(q), StringComparison.Ordinal);
    }

    [Fact]
    public void Nxdomain_and_foreign_id()
    {
        var q = DnsMessage.BuildQuery("rutracker.org", DnsMessage.TypeA, 7);
        Assert.True(DnsMessage.Parse(Response(q, 3), 7)!.IsNxDomain);
        Assert.Null(DnsMessage.Parse(Response(q, 0), 8)); // ответ не на наш запрос
        Assert.Null(DnsMessage.Parse(q, 7)); // это запрос, а не ответ
    }

    [Fact]
    public void Truncated_and_looping_packets_are_rejected()
    {
        var q = DnsMessage.BuildQuery("x.com", DnsMessage.TypeA, 9);
        var full = Response(q, 0, IPAddress.Parse("1.2.3.4"));
        for (var cut = 12; cut < full.Length; cut++)
            Assert.Null(DnsMessage.Parse(full.AsSpan(0, cut), 9));

        // Метки без конца: разбор не должен зависнуть или выйти за границы.
        var evil = new byte[300];
        evil[1] = 9;
        evil[2] = 0x80;
        evil[5] = 1;
        for (var i = 12; i < evil.Length; i++)
            evil[i] = 1;
        Assert.Null(DnsMessage.Parse(evil, 9));
    }

    [Fact]
    public void Bad_names_are_refused()
    {
        Assert.Throws<ArgumentException>(() => DnsMessage.BuildQuery("a..b", DnsMessage.TypeA, 1));
        Assert.Throws<ArgumentException>(() => DnsMessage.BuildQuery(new string('a', 64) + ".com", DnsMessage.TypeA, 1));
    }
}

public sealed class DiagnosisTests
{
    private static DnsAnswer Ok(params string[] ips) => new(0, ips.Select(IPAddress.Parse).ToList());

    [Theory]
    [InlineData(5, StepStatus.Ok)]
    [InlineData(-45, StepStatus.Warn)]
    [InlineData(600, StepStatus.Bad)]
    public void Clock_thresholds(int seconds, StepStatus expected)
    {
        var r = Diagnosis.Clock(TimeSpan.FromSeconds(seconds));
        Assert.Equal(expected, r.Status);
        if (seconds < 0)
            Assert.Contains("отстают", r.Title, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.0.0.0", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.10.34.35", true)]
    [InlineData("192.168.1.1", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("162.159.128.233", false)]
    [InlineData("2606:4700::1", false)]
    public void Bogus_addresses(string ip, bool bogus) => Assert.Equal(bogus, Diagnosis.IsBogus(IPAddress.Parse(ip)));

    [Fact]
    public void Dns_comparison()
    {
        var real = Ok("162.159.128.233");
        Assert.Equal(DnsVerdict.Spoofed, Diagnosis.CompareDns(Ok("0.0.0.0"), real));
        Assert.Equal(DnsVerdict.Spoofed, Diagnosis.CompareDns(new DnsAnswer(3, []), real));
        Assert.Equal(DnsVerdict.NoAnswer, Diagnosis.CompareDns(null, real));
        Assert.Equal(DnsVerdict.Same, Diagnosis.CompareDns(Ok("1.1.1.1", "162.159.128.233"), real));
        // Разные, но настоящие адреса — не подмена (CDN отвечает по месту запроса).
        Assert.Equal(DnsVerdict.Different, Diagnosis.CompareDns(Ok("104.16.1.1"), real));
        Assert.Equal(DnsVerdict.Unknown, Diagnosis.CompareDns(Ok("1.1.1.1"), null));
    }

    [Fact]
    public void Poison_step_names_spoofed_domains()
    {
        var r = Diagnosis.DnsPoison([("discord.com", DnsVerdict.Spoofed), ("youtube.com", DnsVerdict.Same)]);
        Assert.Equal(StepStatus.Warn, r.Status);
        Assert.Contains("discord.com", r.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("youtube.com", r.Title, StringComparison.Ordinal);
        Assert.Equal(StepStatus.Ok, Diagnosis.DnsPoison([("x.com", DnsVerdict.Different)]).Status);
        Assert.Equal(StepStatus.Skipped, Diagnosis.DnsPoison([("x.com", DnsVerdict.Unknown)]).Status);
    }

    [Fact]
    public void Own_tun_is_not_a_foreign_vpn()
    {
        AdapterInfo[] adapters =
        [
            new("Tropa", "sing-box TUN (Wintun)", true, [Diagnosis.OwnTunAddress], false),
            new("Ethernet", "Realtek PCIe GbE", true, [IPAddress.Parse("192.168.1.5")], true),
            new("WG", "WireGuard Tunnel", true, [IPAddress.Parse("10.8.0.2")], false),
            new("Old", "TAP-Windows Adapter V9", false, [], false),
            new("WG-WireSock VPN Client Filter-0000", "WireGuard Tunnel-WireSock VPN Client Filter-0000", true, [], false),
        ];
        var foreign = Diagnosis.ForeignVpnAdapters(adapters);
        Assert.Equal(["WG"], foreign.Select(a => a.Name));
        Assert.Equal(StepStatus.Warn, Diagnosis.OtherVpn(foreign, []).Status);
        Assert.Equal(StepStatus.Ok, Diagnosis.OtherVpn([], []).Status);
        Assert.Contains("v2rayN", Diagnosis.OtherVpn([], ["v2rayN"]).Details, StringComparison.Ordinal);
    }

    [Fact]
    public void Leaks_follow_mode_and_settings()
    {
        AdapterInfo[] adapters = [new("Ethernet", "Realtek", true, [IPAddress.Parse("192.168.1.5"), IPAddress.Parse("2a02:6b8::5")], true)];
        var tun = new AppSettings();
        Assert.Equal(StepStatus.Ok, Diagnosis.Leaks(tun, connected: true, adapters).Status);
        Assert.Equal(StepStatus.Skipped, Diagnosis.Leaks(tun, connected: false, adapters).Status);

        var noHijack = tun with { Dns = tun.Dns with { DnsHijack = false } };
        Assert.Equal(StepStatus.Warn, Diagnosis.Leaks(noHijack, true, adapters).Status);

        var proxy = tun with { Connection = tun.Connection with { Mode = CaptureMode.SystemProxy } };
        var r = Diagnosis.Leaks(proxy, true, adapters);
        Assert.Equal(StepStatus.Warn, r.Status);
        Assert.Contains("IPv6", r.Details, StringComparison.Ordinal);
    }
}

public sealed class DiagnosticReportTests
{
    [Fact]
    public void Report_has_no_secrets_and_hides_server_addresses()
    {
        const string uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
        var p1 = ShareLink.Parse($"vless://{uuid}@203.0.113.7:443?security=reality&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw&sid=ab12&sni=www.microsoft.com&fp=chrome#Мой сервер").Profile!;
        var p2 = ShareLink.Parse("trojan://hunter2pass@nl.example.org:443?security=tls&sni=nl.example.org#NL").Profile!;
        var report = DiagnosticReport.Build(new ReportInput
        {
            AppVersion = "0.1.0",
            CoreVersions = "sing-box 1.14.2",
            WindowsVersion = "Windows 11",
            Settings = new AppSettings(),
            Profiles = [p1, p2],
            Steps = [new StepResult("tcping", StepStatus.Bad, "Сервер не отвечает", "Нет ответа от 203.0.113.7:443"), new StepResult("clock", StepStatus.Ok, "Часы точные")],
            Log = ["connect to nl.example.org:443 failed", $"auth uuid={uuid}", "203.0.113.70 is someone else"],
            Scrubber = new SecretScrubber([uuid, "hunter2pass", "Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw"]),
        });

        Assert.DoesNotContain(uuid, report, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2pass", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Мой сервер", report, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.7:", report, StringComparison.Ordinal);
        Assert.DoesNotContain("nl.example.org", report, StringComparison.Ordinal);
        Assert.Contains("server-1:443", report, StringComparison.Ordinal);
        Assert.Contains("server-2:443", report, StringComparison.Ordinal);
        // Чужой адрес, который начинается так же, не превращается в «server-10».
        Assert.Contains("203.0.113.70", report, StringComparison.Ordinal);
        Assert.Contains("[Bad] Сервер не отвечает", report, StringComparison.Ordinal);
    }
}
