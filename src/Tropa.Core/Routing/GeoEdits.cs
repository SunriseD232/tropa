using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Tropa.Core.Model;

namespace Tropa.Core.Routing;

/// <summary>Гео-база, которую можно открыть и поправить на экране «Правила».</summary>
public sealed record GeoListInfo(string Tag, string Title, string Description)
{
    public bool IsIp => RuleSets.IsIpSet(Tag);
}

/// <summary>
/// Правки гео-баз (ADR-039): проверка ввода, очистка недоверенных правок (резервная копия настроек)
/// и применение правок к содержимому файла. Закреплённые файлы .srs не меняются никогда.
/// </summary>
public static class GeoEdits
{
    /// <summary>Сколько записей можно добавить или убрать в одном наборе.</summary>
    public const int MaxEntries = 20_000;

    private const int MaxValueLength = 253;

    public static IReadOnlyList<GeoListInfo> Catalog { get; } =
    [
        new(RuleSets.GeoipRuBlocked, "Заблокированные IP-адреса", "Адреса, заблокированные в России. Всегда идут через сервер."),
        new(RuleSets.GeoipRuBlockedCommunity, "Заблокированные IP-адреса (сообщество)", "Дополнительный список заблокированных адресов от пользователей. Через сервер."),
        new(RuleSets.GeoipRu, "Российские IP-адреса", "Сети российских провайдеров. В маршруте «Всё, кроме российского» идут напрямую."),
        new(RuleSets.GeositeRuBlocked, "Заблокированные сайты", "Сайты, заблокированные в России. Всегда идут через сервер."),
        new(RuleSets.GeositeAiNonCn, "ИИ-сервисы", "ChatGPT, Claude, Gemini и другие сервисы, закрытые для России. Через сервер."),
        new(RuleSets.GeositeCategoryRu, "Российские сайты", "В маршруте «Всё, кроме российского» идут напрямую."),
        new(RuleSets.GeositeGovRu, "Госсайты", "Госуслуги и другие государственные сайты. Всегда напрямую."),
        new(RuleSets.GeositeBankRu, "Банки", "Сайты и приложения банков. Всегда напрямую."),
        new(RuleSets.GeositeRuOnlyInside, "Доступные только из России", "Сайты, которые не открываются из-за границы. Всегда напрямую."),
    ];

    public static GeoListInfo? Find(string tag) => Catalog.FirstOrDefault(c => c.Tag == tag);

    /// <summary>Виды записей, которые можно добавить в набор: в IP-наборы — сети, в наборы сайтов — четыре вида доменов.</summary>
    public static IReadOnlyList<GeoEntryKind> KindsFor(bool ip) =>
        ip ? [GeoEntryKind.Cidr] : [GeoEntryKind.Suffix, GeoEntryKind.Domain, GeoEntryKind.Keyword, GeoEntryKind.Regex];

    /// <summary>
    /// Разбирает ввод пользователя: записи через пробел, запятую или с новой строки (выражения — только
    /// с новой строки: в них бывают пробелы и запятые). Каждая запись проверяется так же, как при загрузке.
    /// </summary>
    public static (List<GeoEntry> Entries, List<string> Bad) ParseInput(string? text, GeoEntryKind kind)
    {
        var entries = new List<GeoEntry>();
        var bad = new List<string>();
        var seen = new HashSet<GeoEntry>();
        char[] separators = kind == GeoEntryKind.Regex ? ['\r', '\n'] : [' ', '\t', '\r', '\n', ',', ';'];
        foreach (var part in (text ?? "").Split(separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var raw = part.Trim();
            if (raw.Length == 0)
                continue;
            var value = Validate(raw, kind);
            if (value is null)
                bad.Add(raw);
            else if (seen.Add(new GeoEntry(kind, value)))
                entries.Add(new GeoEntry(kind, value));
        }

        return (entries, bad);
    }

    /// <summary>Что получилось из вставленного списка v2ray / Xray / Clash.</summary>
    public sealed record AutoParse(List<GeoEntry> Entries, List<string> Bad, List<string> GeoRefs, List<string> WrongPlace);

    /// <summary>
    /// Вставка из v2rayN / Xray: «domain:» — домен и поддомены, «full:» — точный адрес, «keyword:» — слово,
    /// «regexp:» — выражение, IP и сети — как есть; строка без префикса — домен и поддомены. Понимает и
    /// JSON-массив ("domain:a.com", …), запятые в конце строк, правила Clash (DOMAIN-SUFFIX,a.com) и «+.a.com».
    /// «geosite:» / «geoip:» не переносятся — это ссылки на чужие базы (в Тропе они готовые).
    /// </summary>
    /// <param name="ip">Разрешены ли IP и сети.</param>
    /// <param name="sites">Разрешены ли домены.</param>
    public static AutoParse ParseAuto(string? text, bool ip, bool sites)
    {
        var result = new AutoParse([], [], [], []);
        var seen = new HashSet<GeoEntry>();
        foreach (var rawLine in (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = Unwrap(rawLine);
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            var tokens = line.StartsWith("regexp:", StringComparison.OrdinalIgnoreCase) || ClashRule(line) is not null
                ? [line]
                : line.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(Unwrap).Where(t => t.Length > 0);
            foreach (var token in tokens)
            {
                var (kind, value) = ClashRule(token) ?? Prefixed(token);
                if (kind is null)
                {
                    if (value == "geo")
                        result.GeoRefs.Add(token);
                    else
                        result.Bad.Add(token);
                    continue;
                }

                if ((kind == GeoEntryKind.Cidr && !ip) || (kind != GeoEntryKind.Cidr && !sites))
                {
                    result.WrongPlace.Add(token);
                    continue;
                }

                if (Validate(value, kind.Value) is not { } ok)
                {
                    result.Bad.Add(token);
                    continue;
                }

                var entry = new GeoEntry(kind.Value, ok);
                if (seen.Add(entry))
                    result.Entries.Add(entry);
            }
        }

        return result;
    }

    private static string Unwrap(string s)
    {
        s = s.Trim().Trim(',', ';').Trim().Trim('[', ']').Trim().TrimEnd(',').Trim();
        // Строка JSON из конфига v2rayN: "regexp:^(.+\\.)?a\\.com$" — снимаем экранирование, иначе \\ попадёт в выражение.
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"')
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(s);
                return doc.RootElement.GetString()?.Trim() ?? "";
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }

        return s.Trim('"', '\'').Trim();
    }

    private static (GeoEntryKind? Kind, string Value) Prefixed(string token)
    {
        var colon = token.IndexOf(':', StringComparison.Ordinal);
        // «2a00::1» и «1.2.3.4/32» — адреса, а не префиксы.
        if (Cidr(token) is not null)
            return (GeoEntryKind.Cidr, token);
        if (colon > 0 && !token.Contains("://", StringComparison.Ordinal))
        {
            var prefix = token[..colon].ToLowerInvariant();
            var value = token[(colon + 1)..].Trim();
            return prefix switch
            {
                "domain" => (GeoEntryKind.Suffix, value),
                "full" => (GeoEntryKind.Domain, value),
                "keyword" => (GeoEntryKind.Keyword, value),
                "regexp" => (GeoEntryKind.Regex, value),
                "geosite" or "geoip" or "ext" or "ext-ip" => (null, "geo"),
                // «example.com:443» — адрес с портом, а не префикс.
                _ when int.TryParse(value, out _) => (GeoEntryKind.Suffix, token),
                _ => (null, ""),
            };
        }

        if (token.StartsWith("+.", StringComparison.Ordinal))
            token = token[2..];
        return (GeoEntryKind.Suffix, token);
    }

    /// <summary>Строка Clash: «DOMAIN-SUFFIX,example.com[,PROXY]».</summary>
    private static (GeoEntryKind? Kind, string Value)? ClashRule(string line)
    {
        var parts = line.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
            return null;
        GeoEntryKind? kind = parts[0].ToUpperInvariant() switch
        {
            "DOMAIN-SUFFIX" => GeoEntryKind.Suffix,
            "DOMAIN" => GeoEntryKind.Domain,
            "DOMAIN-KEYWORD" => GeoEntryKind.Keyword,
            "DOMAIN-REGEX" => GeoEntryKind.Regex,
            "IP-CIDR" or "IP-CIDR6" => GeoEntryKind.Cidr,
            _ => null,
        };
        return kind is null ? null : (kind, parts[1]);
    }

    /// <summary>Сколько своих списков можно завести.</summary>
    public const int MaxCustomLists = 50;

    /// <summary>Тег встроенного набора своего списка: домены и сети отдельно — сети срабатывают только после resolve.</summary>
    public static string CustomTag(CustomGeoList list, bool ip)
    {
        ArgumentNullException.ThrowIfNull(list);
        return "custom-" + list.Id.ToString("N")[..12] + (ip ? "-ip" : "");
    }

    /// <summary>Свои списки из недоверенного источника: имя очищено, записи проверены, действие известное.</summary>
    public static List<CustomGeoList> SanitizeCustom(IReadOnlyList<CustomGeoList>? lists)
    {
        var result = new List<CustomGeoList>();
        var ids = new HashSet<Guid>();
        foreach (var l in (lists ?? []).Take(MaxCustomLists))
        {
            if (l is null || l.Id == Guid.Empty || !ids.Add(l.Id))
                continue;
            var seen = new HashSet<GeoEntry>();
            var entries = new List<GeoEntry>();
            foreach (var e in l.Entries)
            {
                if (entries.Count >= MaxEntries)
                    break;
                if (e?.Value is not null && Validate(e.Value, e.Kind) is { } v && seen.Add(e with { Value = v }))
                    entries.Add(e with { Value = v });
            }

            result.Add(l with
            {
                Name = Security.NameSanitizer.Sanitize(l.Name, "Мой список"),
                Action = l.Action is RuleActionKind.Direct or RuleActionKind.Block ? l.Action : RuleActionKind.Proxy,
                Entries = entries,
            });
        }

        return result;
    }

    /// <summary>Проверенное значение записи этого вида или null.</summary>
    public static string? Validate(string? value, GeoEntryKind kind) => kind switch
    {
        GeoEntryKind.Cidr => Cidr(value),
        GeoEntryKind.Domain or GeoEntryKind.Suffix => Domain(value, kind),
        GeoEntryKind.Keyword => Keyword(value),
        GeoEntryKind.Regex => Regex(value),
        _ => null,
    };

    /// <summary>Часть адреса: «linked» совпадёт с linkedin.com. Латиница, цифры, точка, дефис, подчёркивание.</summary>
    public static string? Keyword(string? input)
    {
        var k = input?.Trim().ToLowerInvariant() ?? "";
        return k.Length is >= 1 and <= 63 && k.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_') ? k : null;
    }

    // Конструкции, которые понимает .NET, но не RE2 (sing-box написан на Go): с ними ядро не запустится.
    private static readonly string[] NotRe2 = [@"\p", @"\P", @"\G", @"\Z", @"\h", @"\k", "-[", "(?#", "(?'"];

    /// <summary>
    /// Регулярное выражение для адреса сайта (как в sing-box, без привязки к началу и концу: «^» и «$» ставьте сами).
    /// Проверяется движком .NET без возвратов — он, как и RE2, не знает обратных ссылок и просмотра вперёд/назад.
    /// </summary>
    public static string? Regex(string? input)
    {
        var r = input?.Trim() ?? "";
        if (r.Length is 0 or > 256 || r.Any(char.IsControl) || NotRe2.Any(t => r.Contains(t, StringComparison.Ordinal)))
            return null;
        try
        {
            _ = new System.Text.RegularExpressions.Regex(r, System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            return r;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Адрес для проверки: «https://www.LinkedIn.com/feed» → «www.linkedin.com» (www не убирается — это другой
    /// адрес), кириллица → punycode. null — не адрес сайта.
    /// </summary>
    public static string? Host(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || Cidr(input) is not null)
            return null;
        var d = RuleInput.NormalizeDomain(input);
        return d is not null && input.Contains("www." + d, StringComparison.OrdinalIgnoreCase) ? "www." + d : d;
    }

    /// <summary>
    /// Совпадает ли запись с адресом так же, как в sing-box: Domain — ровно этот адрес, Suffix — он и поддомены
    /// (с точкой в начале — только поддомены), Keyword — адрес содержит слово, Regex — поиск выражения в адресе,
    /// Cidr — IP-адрес в сети.
    /// </summary>
    public static bool Matches(GeoEntry entry, string? host, IPAddress? ip, Func<string, System.Text.RegularExpressions.Regex?>? regex = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        switch (entry.Kind)
        {
            case GeoEntryKind.Cidr:
                return ip is not null && InNetwork(entry.Value, ip);
            case GeoEntryKind.Domain:
                return host is not null && string.Equals(host, entry.Value, StringComparison.OrdinalIgnoreCase);
            case GeoEntryKind.Suffix:
                if (host is null)
                    return false;
                if (entry.Value.StartsWith('.'))
                    return host.EndsWith(entry.Value, StringComparison.OrdinalIgnoreCase);
                return string.Equals(host, entry.Value, StringComparison.OrdinalIgnoreCase)
                    || host.EndsWith("." + entry.Value, StringComparison.OrdinalIgnoreCase);
            case GeoEntryKind.Keyword:
                return host is not null && host.Contains(entry.Value, StringComparison.OrdinalIgnoreCase);
            case GeoEntryKind.Regex:
                if (host is null)
                    return false;
                var r = regex is not null ? regex(entry.Value) : CompileForMatch(entry.Value);
                try
                {
                    return r?.IsMatch(host) == true;
                }
                catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
                {
                    return false;
                }

            default:
                return false;
        }
    }

    /// <summary>Выражение из базы для проверки адреса; null — .NET его не понимает (тогда считаем, что не совпало).</summary>
    public static System.Text.RegularExpressions.Regex? CompileForMatch(string pattern)
    {
        try
        {
            return new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool InNetwork(string cidr, IPAddress ip)
    {
        var slash = cidr.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0 || !IPAddress.TryParse(cidr[..slash], out var net)
            || !int.TryParse(cidr[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            return false;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (net.AddressFamily != ip.AddressFamily)
            return false;
        var a = net.GetAddressBytes();
        var b = ip.GetAddressBytes();
        for (var i = 0; i < a.Length && prefix > 0; i++, prefix -= 8)
        {
            var mask = (byte)(0xFF << (8 - Math.Min(prefix, 8)));
            if ((a[i] & mask) != (b[i] & mask))
                return false;
        }

        return true;
    }

    /// <summary>«1.2.3.4» → «1.2.3.4/32»; «10.1.2.3/8» → «10.0.0.0/8». null — не адрес и не сеть.</summary>
    public static string? Cidr(string? input)
    {
        var s = input?.Trim() ?? "";
        var slash = s.IndexOf('/', StringComparison.Ordinal);
        if (!IPAddress.TryParse(slash < 0 ? s : s[..slash], out var address))
            return null;
        var bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        var prefix = bits;
        if (slash >= 0 && (!int.TryParse(s[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) || prefix > bits))
            return null;
        if (address.IsIPv4MappedToIPv6)
            return null;

        var bytes = address.GetAddressBytes();
        for (var i = 0; i < bytes.Length; i++)
        {
            var keep = Math.Clamp(prefix - (i * 8), 0, 8);
            bytes[i] &= (byte)(0xFF << (8 - keep));
        }

        return new IPAddress(bytes).ToString() + "/" + prefix.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Домен: Suffix — как правило «Сайты» (без www), Domain — ровно этот адрес.</summary>
    public static string? Domain(string? input, GeoEntryKind kind)
    {
        // IP-адрес как «сайт» никогда не совпадёт — ему место в IP-списке.
        if (Cidr(input) is not null)
            return null;
        if (kind == GeoEntryKind.Suffix)
            return RuleInput.NormalizeDomain(input);
        if (kind != GeoEntryKind.Domain || string.IsNullOrWhiteSpace(input))
            return null;
        // NormalizeDomain убирает «www.», а точному адресу он нужен.
        var d = RuleInput.NormalizeDomain(input);
        return d is not null && input.Contains("www." + d, StringComparison.OrdinalIgnoreCase) ? "www." + d : d;
    }

    /// <summary>
    /// Правки из недоверенного источника (резервная копия, повреждённый файл): только известные наборы,
    /// только проверенные значения, не больше <see cref="MaxEntries"/> в каждом списке.
    /// </summary>
    public static List<GeoListEdit> Sanitize(IReadOnlyList<GeoListEdit>? edits)
    {
        var result = new List<GeoListEdit>();
        foreach (var e in edits ?? [])
        {
            if (e?.Tag is not { } tag || Find(tag) is not { } info || result.Any(r => r.Tag == tag))
                continue;
            var added = Clean(e.Added, info.IsIp, removed: false);
            var removed = Clean(e.Removed, info.IsIp, removed: true);
            if (added.Count + removed.Count > 0)
                result.Add(new GeoListEdit { Tag = tag, Added = added, Removed = removed });
        }

        return result;
    }

    private static List<GeoEntry> Clean(IReadOnlyList<GeoEntry>? entries, bool ip, bool removed)
    {
        var seen = new HashSet<GeoEntry>();
        var list = new List<GeoEntry>();
        foreach (var e in entries ?? [])
        {
            if (e?.Value is null || list.Count >= MaxEntries)
                continue;
            var value = e.Kind switch
            {
                GeoEntryKind.Cidr when ip => Cidr(e.Value),
                // Убирают записи из самой базы — их значения не обязаны проходить нашу проверку, только безопасность.
                // Выражение должно хотя бы разбираться: иначе ядро не запустится.
                GeoEntryKind.Regex when !ip && removed => Opaque(e.Value) is { } v && CompileForMatch(v) is not null ? v : null,
                GeoEntryKind.Domain or GeoEntryKind.Suffix or GeoEntryKind.Keyword or GeoEntryKind.Regex when !ip =>
                    removed ? Opaque(e.Value) : Validate(e.Value, e.Kind),
                _ => null,
            };
            if (value is not null && seen.Add(e with { Value = value }))
                list.Add(e with { Value = value });
        }

        return list;
    }

    /// <summary>Значение из самой базы: не проверяем как домен, только длину и отсутствие управляющих символов.</summary>
    private static string? Opaque(string value) =>
        value.Length is > 0 and <= MaxValueLength && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) ? value : null;

    /// <summary>Содержимое набора с правками: исходные записи без убранных, затем добавленные.</summary>
    public static List<GeoEntry> Apply(IReadOnlyList<GeoEntry> original, GeoListEdit? edit)
    {
        if (edit is null || edit.IsEmpty)
            return [.. original];
        var removed = edit.Removed.ToHashSet();
        var result = original.Where(e => !removed.Contains(e)).ToList();
        var present = result.ToHashSet();
        result.AddRange(edit.Added.Where(present.Add));
        return result;
    }
}
