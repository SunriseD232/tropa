using System.Collections.ObjectModel;
using Tropa.Core.Model;

namespace Tropa.Core.Compatibility;

/// <summary>
/// Итог проверки совместимости: эффективные настройки (с принудительными значениями)
/// и причины, по которым пункты сейчас недоступны. Ключи — как в info.ru.json.
/// Сохранённые пользователем значения не меняются: когда условие снимается, они возвращаются.
/// </summary>
public sealed record CompatResult(AppSettings Effective, IReadOnlyDictionary<string, string> Disabled)
{
    public bool IsDisabled(string key) => Disabled.ContainsKey(key);
}

/// <summary>
/// Единый источник правил «что с чем несовместимо» (docs/04-domain-model.md, §5).
/// Используется и интерфейсом, и валидатором перед запуском ядра.
/// </summary>
public static class CompatRules
{
    private const string TunOnly = "работает только в режиме «Весь компьютер»";
    private const string ProxyOnly = "нужно только в режиме «Только браузеры»";

    private sealed record SettingRule(string Key, Func<AppSettings, Profile?, bool> When, string Reason, Func<AppSettings, AppSettings>? Force = null);

    // Порядок важен: правило видит эффективные значения, уже изменённые предыдущими правилами.
    private static readonly SettingRule[] Rules =
    [
        new("killSwitch", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly,
            s => s with { General = s.General with { KillSwitch = false } }),
        new("fakeip", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly,
            s => s with { Dns = s.Dns with { Fakeip = false } }),
        new("dnsHijack", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly,
            s => s with { Dns = s.Dns with { DnsHijack = false } }),
        new("tunStack", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly),
        new("mtu", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly),
        new("strictRoute", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly),
        new("lanBypass", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly),
        new("appRules", (s, _) => s.Connection.Mode != CaptureMode.Tun, TunOnly),
        new("sysBypass", (s, _) => s.Connection.Mode != CaptureMode.SystemProxy, ProxyOnly),
        new("uwpLoopback", (s, _) => s.Connection.Mode != CaptureMode.SystemProxy, ProxyOnly),
        new("localPass", (s, _) => s.Connection.Mode == CaptureMode.SystemProxy,
            "системный прокси Windows не умеет передавать пароль — браузеры начали бы его спрашивать",
            s => s with { General = s.General with { LocalPass = false } }),
        new("lanPort", (s, _) => !s.Connection.LanAllow, "включите подключения из локальной сети"),
        new("autoInterval", (s, _) => !s.Connection.AutoSelect, "включите авто-выбор"),
        new("autoTol", (s, _) => !s.Connection.AutoSelect, "включите авто-выбор"),
        new("stunServer", (s, _) => !s.Connection.UdpTestOn, "включите проверку UDP"),
        new("routeOnly", (s, _) => !s.Dns.Sniffing, "включите определение домена",
            s => s with { Dns = s.Dns with { RouteOnly = false } }),
        new("sniffProto", (s, _) => !s.Dns.Sniffing, "определение домена выключено в разделе DNS",
            s => s with { Expert = s.Expert with { SniffHttp = false, SniffTls = false, SniffQuic = false } }),
        new("fragment", (s, _) => s.Dpi.Bypass, "фрагментацию включает кнопка «Обход DPI» на главной",
            s => s with { Dpi = s.Dpi with { Fragment = true } }),
        new("fragParams", (s, _) => !s.Dpi.Fragment, "включите фрагментацию"),
        new("noise", (s, _) => s.Cores.CoreChoice == CoreChoice.SingBox, "шум есть только в ядре Xray, а выбрано «Всегда sing-box»",
            s => s with { Dpi = s.Dpi with { Noise = false } }),
        new("noiseParams", (s, _) => !s.Dpi.Noise, "включите шум"),
        new("mux", (_, p) => p?.Flow == VlessFlow.XtlsRprxVision,
            "активный сервер использует XTLS Vision, а Vision с Mux не работает",
            s => s with { Dpi = s.Dpi with { Mux = false } }),
        new("mux", (_, p) => p?.Protocol == Protocol.Hysteria2,
            "Hysteria2 работает поверх QUIC со своим мультиплексированием",
            s => s with { Dpi = s.Dpi with { Mux = false } }),
        new("mux", (_, p) => p?.Transport.Type == TransportType.Xhttp,
            "у XHTTP своё мультиплексирование",
            s => s with { Dpi = s.Dpi with { Mux = false } }),
        new("muxConc", (s, _) => !s.Dpi.Mux, "Mux выключен"),
        new("templateText", (s, _) => !s.Expert.Template, "включите свой шаблон"),
        new("dpiFirst", (s, _) => s.Dpi.Bypass, "включено кнопкой «Обход DPI» на главной",
            s => s with { Routing = s.Routing with { DpiFirst = true } }),
        new("dpiFirst", (s, _) => s.Routing.Preset == RoutePreset.All, "при «Всё через сервер» обход DPI не используется",
            s => s with { Routing = s.Routing with { DpiFirst = false } }),
        new("subUACustom", (s, _) => s.Subscriptions.SubUA != UserAgentMode.Custom, "выберите «Свой»"),
    ];

    public static CompatResult Evaluate(AppSettings settings, Profile? activeProfile = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var effective = settings;
        var disabled = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rule in Rules)
        {
            if (disabled.ContainsKey(rule.Key) || !rule.When(effective, activeProfile))
                continue;
            disabled[rule.Key] = rule.Reason;
            if (rule.Force is not null)
                effective = rule.Force(effective);
        }

        return new CompatResult(effective, new ReadOnlyDictionary<string, string>(disabled));
    }
}

/// <summary>
/// Совместимость параметров сервера. <see cref="Issues"/> — что не так в текущем профиле
/// (такой профиль не запускается), <see cref="DisabledOptions"/> — какие варианты нельзя
/// выбрать в окне редактирования при текущем выборе.
/// </summary>
public static class ProfileCompat
{
    public static IReadOnlyList<string> Issues(Profile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var issues = new List<string>();
        var sec = p.Security.Type;
        if (p.Protocol == Protocol.Shadowsocks)
        {
            if (p.Transport.Type != TransportType.Tcp || sec != SecurityType.None)
                issues.Add("Shadowsocks в Тропе работает без транспорта и TLS.");
            if (p.SsMethod is null)
                issues.Add("Для Shadowsocks не указан метод шифрования.");
            return issues;
        }

        if (p.Protocol == Protocol.Hysteria2)
        {
            if (sec != SecurityType.Tls)
                issues.Add("Hysteria2 всегда работает поверх TLS (QUIC).");
            if (p.Transport.Type != TransportType.Tcp)
                issues.Add("У Hysteria2 нет отдельного транспорта.");
            return issues;
        }

        if (sec == SecurityType.Reality && p.Protocol != Protocol.Vless)
            issues.Add("Reality поддерживается только для VLESS.");
        if (sec == SecurityType.Reality && p.Transport.Type == TransportType.Ws)
            issues.Add("Reality не работает с WebSocket.");
        if (sec == SecurityType.Reality && p.Security.Reality is null)
            issues.Add("Для Reality не указан публичный ключ.");
        if (p.Protocol == Protocol.Trojan && sec == SecurityType.None)
            issues.Add("Trojan всегда работает поверх TLS.");
        if (p.Flow == VlessFlow.XtlsRprxVision)
        {
            if (p.Protocol != Protocol.Vless)
                issues.Add("XTLS Vision есть только у VLESS.");
            if (p.Transport.Type != TransportType.Tcp)
                issues.Add("Vision работает только поверх транспорта TCP.");
            if (sec == SecurityType.None)
                issues.Add("Vision требует TLS или Reality.");
        }

        return issues;
    }

    /// <summary>Ключи вида «protocol.vmess», «security.reality», «transport.ws», «flow».</summary>
    public static IReadOnlyDictionary<string, string> DisabledOptions(Profile p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string key, bool when, string reason)
        {
            if (when)
                d.TryAdd(key, reason);
        }

        var sec = p.Security.Type;
        var simple = p.Protocol is Protocol.Shadowsocks or Protocol.Hysteria2;
        Add("transport.xhttp", simple, "у Shadowsocks и Hysteria2 нет выбора транспорта");
        Add("transport.ws", simple, "у Shadowsocks и Hysteria2 нет выбора транспорта");
        Add("transport.grpc", simple, "у Shadowsocks и Hysteria2 нет выбора транспорта");
        Add("transport.httpupgrade", simple, "у Shadowsocks и Hysteria2 нет выбора транспорта");
        Add("security.reality", simple, "Reality только для VLESS");
        Add("security.tls", p.Protocol == Protocol.Shadowsocks, "Shadowsocks шифрует сам, TLS к нему не добавляется");
        Add("security.none", p.Protocol == Protocol.Hysteria2, "Hysteria2 всегда работает поверх TLS");
        Add("protocol.vmess", sec == SecurityType.Reality, "VMess не работает с Reality — сначала выберите TLS");
        Add("protocol.trojan", sec == SecurityType.Reality, "Reality в Тропе только для VLESS — сначала выберите TLS");
        Add("protocol.trojan", sec == SecurityType.None, "Trojan всегда работает поверх TLS — сначала выберите TLS");
        Add("security.reality", p.Protocol != Protocol.Vless, "Reality поддерживается только для VLESS");
        Add("security.reality", p.Transport.Type == TransportType.Ws, "Reality не работает с WebSocket");
        Add("security.none", p.Protocol == Protocol.Trojan, "Trojan без TLS не бывает");
        Add("transport.ws", sec == SecurityType.Reality, "WebSocket несовместим с Reality");
        Add("flow", p.Protocol != Protocol.Vless, "XTLS Vision есть только у VLESS");
        Add("flow", p.Transport.Type != TransportType.Tcp, "Vision работает только поверх транспорта TCP");
        Add("flow", sec == SecurityType.None, "Vision требует TLS или Reality");
        return d;
    }
}
