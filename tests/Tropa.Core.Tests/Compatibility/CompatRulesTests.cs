using Tropa.Core.Compatibility;
using Tropa.Core.Model;
using static Tropa.Core.Tests.Parsing.ShareLinkTests;

namespace Tropa.Core.Tests.Compatibility;

public sealed class CompatRulesTests
{
    private static readonly AppSettings Defaults = new();

    private static AppSettings Mode(CaptureMode mode) =>
        Defaults with { Connection = Defaults.Connection with { Mode = mode } };

    [Fact]
    public void Defaults_disable_only_expected_items()
    {
        var r = CompatRules.Evaluate(Defaults);
        // По умолчанию TUN: недоступны только настройки системного прокси и зависимые от выключенных опций.
        Assert.Equal(
            ["fragParams", "muxConc", "noiseParams", "subUACustom", "sysBypass", "templateText", "uwpLoopback"],
            r.Disabled.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(CaptureMode.SystemProxy)]
    [InlineData(CaptureMode.PortsOnly)]
    public void Non_tun_mode_forces_off_tun_only_features(CaptureMode mode)
    {
        var settings = Mode(mode) with
        {
            General = Defaults.General with { KillSwitch = true },
            Dns = Defaults.Dns with { Fakeip = true, DnsHijack = true },
        };
        var r = CompatRules.Evaluate(settings);

        foreach (var key in new[] { "killSwitch", "fakeip", "dnsHijack", "tunStack", "mtu", "strictRoute", "lanBypass", "appRules" })
            Assert.True(r.IsDisabled(key), key);
        Assert.False(r.Effective.General.KillSwitch);
        Assert.False(r.Effective.Dns.Fakeip);
        Assert.False(r.Effective.Dns.DnsHijack);
        // Сохранённое значение пользователя не тронуто.
        Assert.True(settings.General.KillSwitch);
    }

    [Fact]
    public void System_proxy_options_enabled_only_in_proxy_mode()
    {
        Assert.False(CompatRules.Evaluate(Mode(CaptureMode.SystemProxy)).IsDisabled("sysBypass"));
        Assert.True(CompatRules.Evaluate(Mode(CaptureMode.Tun)).IsDisabled("uwpLoopback"));
    }

    [Fact]
    public void Lan_access_forces_password()
    {
        var settings = Defaults with
        {
            Connection = Defaults.Connection with { LanAllow = true },
            General = Defaults.General with { LocalPass = false },
        };
        var r = CompatRules.Evaluate(settings);
        Assert.True(r.IsDisabled("localPass"));
        Assert.True(r.Effective.General.LocalPass);
    }

    [Fact]
    public void Sniffing_off_disables_dependents()
    {
        var settings = Defaults with
        {
            Dns = Defaults.Dns with { Sniffing = false, RouteOnly = true },
            Expert = Defaults.Expert with { SniffQuic = true },
        };
        var r = CompatRules.Evaluate(settings);
        Assert.True(r.IsDisabled("routeOnly"));
        Assert.True(r.IsDisabled("sniffProto"));
        Assert.False(r.Effective.Dns.RouteOnly);
        Assert.False(r.Effective.Expert.SniffQuic);
    }

    [Fact]
    public void Sing_box_only_core_disables_noise_and_its_params()
    {
        var settings = Defaults with
        {
            Cores = Defaults.Cores with { CoreChoice = CoreChoice.SingBox },
            Dpi = Defaults.Dpi with { Noise = true },
        };
        var r = CompatRules.Evaluate(settings);
        Assert.True(r.IsDisabled("noise"));
        Assert.False(r.Effective.Dpi.Noise);
        // Цепочка: шум принудительно выключен → его параметры тоже недоступны.
        Assert.True(r.IsDisabled("noiseParams"));
    }

    [Fact]
    public void Vision_profile_disables_mux()
    {
        var vision = Ok(VlessReality);
        var settings = Defaults with { Dpi = Defaults.Dpi with { Mux = true } };
        var r = CompatRules.Evaluate(settings, vision);
        Assert.True(r.IsDisabled("mux"));
        Assert.False(r.Effective.Dpi.Mux);
        Assert.True(r.IsDisabled("muxConc"));

        var plain = vision with { Flow = VlessFlow.None };
        Assert.False(CompatRules.Evaluate(settings, plain).IsDisabled("mux"));
    }

    [Fact]
    public void Xhttp_profile_disables_mux()
    {
        var xhttp = Ok(VlessReality) with { Flow = VlessFlow.None, Transport = new TransportSettings { Type = TransportType.Xhttp, Path = "/" } };
        Assert.Contains("XHTTP", CompatRules.Evaluate(Defaults, xhttp).Disabled["mux"], StringComparison.Ordinal);
    }
}

public sealed class ProfileCompatTests
{
    private static readonly Profile Reality = Ok(VlessReality);

    [Fact]
    public void Valid_reality_vision_has_no_issues() => Assert.Empty(ProfileCompat.Issues(Reality));

    [Fact]
    public void Reality_profile_disables_vmess_trojan_and_ws()
    {
        var d = ProfileCompat.DisabledOptions(Reality);
        Assert.Contains("protocol.vmess", d.Keys);
        Assert.Contains("protocol.trojan", d.Keys);
        Assert.Contains("transport.ws", d.Keys);
        Assert.DoesNotContain("flow", d.Keys);
    }

    [Fact]
    public void Ws_transport_disables_reality_and_flow()
    {
        var ws = Reality with
        {
            Flow = VlessFlow.None,
            Transport = new TransportSettings { Type = TransportType.Ws, Path = "/" },
            Security = new SecuritySettings { Type = SecurityType.Tls, Sni = "a.example.com" },
        };
        var d = ProfileCompat.DisabledOptions(ws);
        Assert.Contains("security.reality", d.Keys);
        Assert.Contains("flow", d.Keys);
        Assert.Empty(ProfileCompat.Issues(ws));
    }

    [Fact]
    public void Trojan_disables_none_and_reality()
    {
        var trojan = Ok("trojan://pw@fi.example.com:443?sni=fi.example.com#t");
        var d = ProfileCompat.DisabledOptions(trojan);
        Assert.Contains("security.none", d.Keys);
        Assert.Contains("security.reality", d.Keys);
        Assert.Contains("flow", d.Keys);
    }

    [Fact]
    public void Imported_incompatible_profiles_are_flagged()
    {
        // Такие ссылки встречаются в подписках: разбираются, но запускать их нельзя.
        var visionOverWs = Ok($"vless://{Uuid}@a.example.com:443?flow=xtls-rprx-vision&type=ws&security=tls&sni=a.example.com#x");
        Assert.Contains(ProfileCompat.Issues(visionOverWs), i => i.Contains("TCP", StringComparison.Ordinal));

        var realityWs = Ok($"vless://{Uuid}@a.example.com:443?type=ws&security=reality&sni=a.example.com&pbk={Pbk}#x");
        Assert.Contains(ProfileCompat.Issues(realityWs), i => i.Contains("WebSocket", StringComparison.Ordinal));

        var trojanNone = Ok("trojan://pw@fi.example.com:443?security=none#t");
        Assert.Contains(ProfileCompat.Issues(trojanNone), i => i.Contains("Trojan", StringComparison.Ordinal));

        var vmessReality = Ok($"vless://{Uuid}@a.example.com:443?security=reality&sni=a.example.com&pbk={Pbk}#x") with { Protocol = Protocol.Vmess };
        Assert.Contains(ProfileCompat.Issues(vmessReality), i => i.Contains("Reality", StringComparison.Ordinal));
    }
}
