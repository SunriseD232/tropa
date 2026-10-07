using System.Net;
using System.Text.Json;
using Tropa.Core.Model;

namespace Tropa.Core.Routing;

/// <summary>Что удалось разобрать из чужого списка сайтов.</summary>
public sealed record SiteList(
    IReadOnlyList<string> DomainSuffixes,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Keywords,
    IReadOnlyList<string> IpCidrs,
    int Skipped,
    IReadOnlyList<string> Warnings)
{
    public int Count => DomainSuffixes.Count + Domains.Count + Keywords.Count + IpCidrs.Count;

    /// <summary>Одно правило со всеми адресами — ядру так проще, чем тысячи отдельных правил.</summary>
    public Rule ToRule(string label, RuleAction tcp, RuleAction udp) => new()
    {
        Label = label,
        Match = new RuleMatch { DomainSuffixes = DomainSuffixes, Domains = Domains, DomainKeywords = Keywords, IpCidrs = IpCidrs },
        Tcp = tcp,
        Udp = udp,
    };
}

/// <summary>
/// Массовый импорт сайтов из других программ (info.ru.json: siteImport). Понимает: простой список
/// доменов и адресов, файл hosts, правила v2rayN/Xray (JSON: domain/ip с префиксами domain:, full:,
/// keyword:), правила Clash (DOMAIN-SUFFIX,…; DOMAIN,…; DOMAIN-KEYWORD,…; IP-CIDR,…; «+.example.com»).
/// Текст — недоверенные данные: каждое значение проверяется, регулярные выражения и geosite не принимаются.
/// </summary>
public static class SiteListImport
{
    public const int MaxEntries = 50_000;
    private const int MaxChars = 10 * 1024 * 1024;

    public static SiteList Parse(string? text)
    {
        var suffixes = new HashSet<string>(StringComparer.Ordinal);
        var domains = new HashSet<string>(StringComparer.Ordinal);
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        var cidrs = new HashSet<string>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var skipped = 0;
        if (string.IsNullOrWhiteSpace(text))
            return new SiteList([], [], [], [], 0, ["Список пуст."]);
        if (text.Length > MaxChars)
            return new SiteList([], [], [], [], 0, ["Список больше 10 МБ — разделите его на части."]);

        var trimmed = text.TrimStart();
        IEnumerable<string> entries = trimmed.StartsWith('{') || trimmed.StartsWith('[') ? FromJson(trimmed, warnings) : text.Split('\n');
        var geosite = 0;
        var regex = 0;
        foreach (var raw in entries)
        {
            if (suffixes.Count + domains.Count + keywords.Count + cidrs.Count >= MaxEntries)
            {
                warnings.Add($"Взяты первые {MaxEntries} адресов.");
                break;
            }

            var line = StripComment(raw).Trim().Trim(',', '"', '\'').Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal))
                line = line[2..].Trim().Trim('"', '\'');
            if (line.Length == 0 || line.EndsWith(':') || line is "payload:" or "[" or "]" or "{" or "}")
                continue;

            switch (Classify(line))
            {
                case (Kind.Suffix, var v):
                    suffixes.Add(v);
                    break;
                case (Kind.Full, var v):
                    domains.Add(v);
                    break;
                case (Kind.Keyword, var v):
                    keywords.Add(v);
                    break;
                case (Kind.Cidr, var v):
                    cidrs.Add(v);
                    break;
                case (Kind.Geosite, _):
                    geosite++;
                    break;
                case (Kind.Regex, _):
                    regex++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        if (geosite > 0)
            warnings.Add($"Пропущено ссылок на geosite/geoip: {geosite} — добавьте их правилом вручную.");
        if (regex > 0)
            warnings.Add($"Пропущено регулярных выражений: {regex}.");
        // Полное имя, уже покрытое суффиксом, лишнее.
        domains.RemoveWhere(d => suffixes.Contains(d));
        return new SiteList([.. suffixes.Order(StringComparer.Ordinal)], [.. domains.Order(StringComparer.Ordinal)],
            [.. keywords.Order(StringComparer.Ordinal)], [.. cidrs.Order(StringComparer.Ordinal)], skipped, warnings);
    }

    private enum Kind { Bad, Suffix, Full, Keyword, Cidr, Geosite, Regex }

    /// <summary>Комментарии: «#» и «//» в начале строки или после пробела (в https:// перед «//» стоит «:»), «;» в начале.</summary>
    private static string StripComment(string line)
    {
        if (line.TrimStart().StartsWith(';'))
            return "";
        for (var i = 0; i < line.Length; i++)
        {
            var atStartOrSpace = i == 0 || char.IsWhiteSpace(line[i - 1]);
            if (line[i] == '#' && atStartOrSpace)
                return line[..i];
            if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/' && atStartOrSpace)
                return line[..i];
        }

        return line;
    }

    private static (Kind, string) Classify(string line)
    {
        // Clash: ТИП,значение[,действие]
        var comma = line.Split(',', StringSplitOptions.TrimEntries);
        if (comma.Length >= 2)
        {
            var value = comma[1];
            return comma[0].ToUpperInvariant() switch
            {
                "DOMAIN-SUFFIX" => Domain(value, Kind.Suffix),
                "DOMAIN" => Domain(value, Kind.Full),
                "DOMAIN-KEYWORD" => Keyword(value),
                "IP-CIDR" or "IP-CIDR6" => Cidr(value),
                "GEOSITE" or "GEOIP" => (Kind.Geosite, ""),
                "DOMAIN-REGEX" => (Kind.Regex, ""),
                _ => (Kind.Bad, ""),
            };
        }

        // v2rayN / Xray: префиксы
        var colon = line.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && !line.Contains("://", StringComparison.Ordinal) && !IPAddress.TryParse(line.Split('/')[0], out _))
        {
            var prefix = line[..colon].ToLowerInvariant();
            var value = line[(colon + 1)..];
            switch (prefix)
            {
                case "domain":
                    return Domain(value, Kind.Suffix);
                case "full":
                    return Domain(value, Kind.Full);
                case "keyword":
                    return Keyword(value);
                case "geosite" or "geoip" or "ext":
                    return (Kind.Geosite, "");
                case "regexp":
                    return (Kind.Regex, "");
            }
        }

        // hosts: «0.0.0.0 example.com» / «127.0.0.1 example.com»
        var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && IPAddress.TryParse(parts[0], out _))
            return Domain(parts[1], Kind.Full);
        if (parts.Length != 1)
            return (Kind.Bad, "");

        var item = parts[0];
        if (item.Contains('/', StringComparison.Ordinal) && !item.Contains("://", StringComparison.Ordinal) && Cidr(item) is (Kind.Cidr, _) cidr)
            return cidr;
        if (IPAddress.TryParse(item, out var ip))
            return (Kind.Cidr, ip.ToString() + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128"));
        // Clash «+.example.com», «*.example.com», «.example.com» — домен с поддоменами.
        if (item.StartsWith("+.", StringComparison.Ordinal) || item.StartsWith("*.", StringComparison.Ordinal))
            item = item[2..];
        return Domain(item, Kind.Suffix);
    }

    private static (Kind, string) Domain(string value, Kind kind)
    {
        var d = RuleInput.NormalizeDomain(value.TrimStart('.'));
        return d is null ? (Kind.Bad, "") : (kind, d);
    }

    private static (Kind, string) Keyword(string value)
    {
        var k = value.Trim().ToLowerInvariant();
        return k.Length is >= 2 and <= 64 && k.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.') ? (Kind.Keyword, k) : (Kind.Bad, "");
    }

    private static (Kind, string) Cidr(string value) =>
        IPNetwork.TryParse(value.Trim(), out var net) ? (Kind.Cidr, net.ToString()) : (Kind.Bad, "");

    /// <summary>JSON v2rayN/Xray: правила маршрутизации с полями domain и ip (на любом уровне вложенности).</summary>
    private static List<string> FromJson(string json, List<string> warnings)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            Walk(doc.RootElement);
        }
        catch (JsonException)
        {
            warnings.Add("Похоже на JSON, но он повреждён.");
        }

        return result;

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Name is "domain" or "domains" or "ip" or "domain_suffix" && p.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var v in p.Value.EnumerateArray())
                            {
                                if (v.ValueKind == JsonValueKind.String)
                                    result.Add(v.GetString()!);
                            }
                        }
                        else
                        {
                            Walk(p.Value);
                        }
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                    {
                        if (item.ValueKind == JsonValueKind.String)
                            result.Add(item.GetString()!);
                        else
                            Walk(item);
                    }

                    break;
            }
        }
    }
}
