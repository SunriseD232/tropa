using System.Text.RegularExpressions;

namespace Tropa.Core.Telemetry;

/// <summary>Строка журнала после разбора: класс (код события) и обезличенный шаблон.</summary>
public sealed record ClassifiedLine(string Code, string Template, string Source);

/// <summary>
/// Классификатор сетевых строк журнала ядер (ADR-050). Не точное совпадение текста, а «мягкие» признаки:
/// у sing-box и Xray разные формулировки одной и той же беды, и они меняются от версии к версии.
/// Из строки убирается всё, что указывает на пользователя и его сайты: адреса, домены, порты, числа,
/// метки времени — остаётся шаблон, по которому одинаковые ошибки разных людей складываются вместе.
/// </summary>
public static partial class NetLogClassifier
{
    // 9 — сетевые ошибки ядер. Порядок важен: первое совпадение побеждает (частное раньше общего).
    public const string Timeout = "9.1";
    public const string Refused = "9.2";
    public const string Reset = "9.3";
    public const string Dns = "9.4";
    public const string Tls = "9.5";
    public const string Reality = "9.6";
    public const string Unreachable = "9.7";
    public const string PortBusy = "9.8";
    public const string Tun = "9.9";
    public const string ProxyAuth = "9.10";
    public const string OtherError = "9.11";
    public const string Fatal = "9.12";
    public const string Udp = "9.13";

    public static IReadOnlyList<(string Code, string Text)> Catalog { get; } =
    [
        (Timeout, "Сеть: таймаут"),
        (Refused, "Сеть: соединение отклонено"),
        (Reset, "Сеть: соединение сброшено или оборвано"),
        (Dns, "Сеть: ошибка DNS"),
        (Tls, "Сеть: ошибка TLS"),
        (Reality, "Сеть: сервер не принял Reality/ключ"),
        (Unreachable, "Сеть: нет маршрута / сеть недоступна"),
        (PortBusy, "Сеть: порт занят"),
        (Tun, "Сеть: ошибка TUN / wintun"),
        (ProxyAuth, "Сеть: локальный прокси требует пароль (407)"),
        (OtherError, "Сеть: прочая ошибка ядра"),
        (Fatal, "Ядро: фатальная ошибка"),
        (Udp, "Сеть: ошибка UDP"),
    ];

    private static readonly (string Code, Regex Pattern)[] Rules =
    [
        (Fatal, FatalRx()),
        (PortBusy, PortBusyRx()),
        (Tun, TunRx()),
        (ProxyAuth, ProxyAuthRx()),
        (Reality, RealityRx()),
        (Tls, TlsRx()),
        (Dns, DnsRx()),
        (Timeout, TimeoutRx()),
        (Refused, RefusedRx()),
        (Reset, ResetRx()),
        (Unreachable, UnreachableRx()),
        (Udp, UdpRx()),
    ];

    /// <summary>Строка от ядра (sing-box или Xray), а не сообщение самой Тропы.</summary>
    public static bool IsCoreLine(string line) => Source(line) is not null;

    /// <summary>
    /// null — строка не похожа на ошибку или предупреждение (обычное сообщение Тропы, информационная
    /// строка ядра — в ней только сайты пользователя, для журнала она не нужна).
    /// </summary>
    public static ClassifiedLine? Classify(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!LevelRx().IsMatch(line))
            return null;
        var source = Source(line) ?? "tropa";
        var template = Template(line);
        foreach (var (code, pattern) in Rules)
        {
            if (pattern.IsMatch(line))
                return new ClassifiedLine(code, template, source);
        }

        return new ClassifiedLine(OtherError, template, source);
    }

    private static string? Source(string line) => XrayRx().IsMatch(line) ? "xray" : SingBoxRx().IsMatch(line) ? "sing-box" : null;

    /// <summary>
    /// Обезличенный шаблон: без времени, адресов, доменов, портов, чисел и идентификаторов соединений.
    /// Остаются только слова ядра — «outbound/vless[proxy]: dial tcp &lt;addr&gt;: i/o timeout».
    /// </summary>
    public static string Template(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var s = line;
        s = TimestampRx().Replace(s, "");
        s = UrlRx().Replace(s, "<url>");
        s = Ipv6Rx().Replace(s, "<addr>");
        s = Ipv4Rx().Replace(s, "<addr>");
        s = DomainRx().Replace(s, "<addr>");
        s = PortRx().Replace(s, "<addr>");
        s = HexRx().Replace(s, "<id>");
        s = NumberRx().Replace(s, "N");
        s = SpacesRx().Replace(s, " ").Trim();
        return s.Length > 300 ? s[..300] + "…" : s;
    }

    /// <summary>Мягкое обезличивание обычных строк Тропы: адреса и домены прячутся, числа (порты, коды) остаются.</summary>
    public static string Anonymize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var s = UrlRx().Replace(line, "<url>");
        s = Ipv6Rx().Replace(s, "<addr>");
        s = Ipv4Rx().Replace(s, "<addr>");
        s = DomainRx().Replace(s, "<addr>");
        return s;
    }

    // Источник и уровень
    [GeneratedRegex(@"\[(Warning|Error|Info|Debug)\]|app/proxyman|transport/internet|proxy/(vless|vmess|trojan|freedom|socks|http)", RegexOptions.IgnoreCase)]
    private static partial Regex XrayRx();

    [GeneratedRegex(@"\b(inbound|outbound|router|dns|connection)/[\w-]+\[|\b(ERROR|WARN|FATAL)\b\s*\[", RegexOptions.None)]
    private static partial Regex SingBoxRx();

    [GeneratedRegex(@"\b(ERROR|FATAL|WARN(ING)?|PANIC)\b|\[(Warning|Error)\]|\bfailed\b", RegexOptions.IgnoreCase)]
    private static partial Regex LevelRx();

    // Классы
    [GeneratedRegex(@"\b(FATAL|PANIC)\b|start service|failed to start|failed to load config|decode config|parse config", RegexOptions.IgnoreCase)]
    private static partial Regex FatalRx();

    [GeneratedRegex(@"address already in use|only one usage of each socket address|bind: |listen (tcp|udp)", RegexOptions.IgnoreCase)]
    private static partial Regex PortBusyRx();

    [GeneratedRegex(@"\btun\b.*(fail|error)|wintun|configure tun|create tun|auto[-_ ]route|strict[-_ ]route", RegexOptions.IgnoreCase)]
    private static partial Regex TunRx();

    [GeneratedRegex(@"\b407\b|proxy authentication|authentication failed|unauthorized|socks5? auth", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyAuthRx();

    [GeneratedRegex(@"reality|invalid (user|uuid|password)|not a valid user|processed invalid|auth(entication)? (failed|error)", RegexOptions.IgnoreCase)]
    private static partial Regex RealityRx();

    [GeneratedRegex(@"\btls\b|handshake|x509|certificate|\bsni\b|utls|alert", RegexOptions.IgnoreCase)]
    private static partial Regex TlsRx();

    [GeneratedRegex(@"\bdns\b|lookup|resolve|no such host|exchange|NXDOMAIN|SERVFAIL", RegexOptions.IgnoreCase)]
    private static partial Regex DnsRx();

    [GeneratedRegex(@"timeout|timed out|deadline exceeded|did not properly respond", RegexOptions.IgnoreCase)]
    private static partial Regex TimeoutRx();

    [GeneratedRegex(@"refused|actively refused|forcibly rejected", RegexOptions.IgnoreCase)]
    private static partial Regex RefusedRx();

    [GeneratedRegex(@"reset by peer|forcibly closed|connection reset|\bEOF\b|broken pipe|closed pipe|use of closed|aborted", RegexOptions.IgnoreCase)]
    private static partial Regex ResetRx();

    [GeneratedRegex(@"unreachable|no route|network is down|no buffer space|host is down", RegexOptions.IgnoreCase)]
    private static partial Regex UnreachableRx();

    [GeneratedRegex(@"\budp\b|packet|quic|hysteria", RegexOptions.IgnoreCase)]
    private static partial Regex UdpRx();

    // Обезличивание
    [GeneratedRegex(@"^[+-]\d{4}\s+|\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2}(\.\d+)?|\[\d+\s+[\d.]+m?s\]")]
    private static partial Regex TimestampRx();

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"\[?\b(?:[0-9a-f]{1,4}:){2,7}[0-9a-f]{0,4}\b\]?(:\d{1,5})?", RegexOptions.IgnoreCase)]
    private static partial Regex Ipv6Rx();

    [GeneratedRegex(@"\b\d{1,3}(\.\d{1,3}){3}(:\d{1,5})?\b")]
    private static partial Regex Ipv4Rx();

    // Домен: метки через точку и зона из букв — у ядер слов с точками внутри почти нет (кроме имён файлов).
    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+(?!(?:go|json|exe|dll|srs|db)\b)[a-z]{2,24}\b(:\d{1,5})?", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRx();

    [GeneratedRegex(@"<addr>:\d{1,5}")]
    private static partial Regex PortRx();

    [GeneratedRegex(@"\b[0-9a-f]{8,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex HexRx();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRx();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRx();
}
