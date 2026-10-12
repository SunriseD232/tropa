using System.Collections.Frozen;

namespace Tropa.Core.Diagnostics;

/// <summary>
/// Страна, из которой сайты видят подключение. Нужна, потому что сервер может сам переключать
/// выход внутри туннеля (балансировщик у провайдера), и имя сервера в Тропе тогда не совпадает с выходом.
/// </summary>
public sealed record ExitRegion(string CountryCode, DateTimeOffset CheckedAt)
{
    /// <summary>Адрес проверки: отвечает текстом «ключ=значение», в нём loc — страна по IP выхода.</summary>
    public const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";

    private static readonly FrozenDictionary<string, string> Names = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RU"] = "Россия", ["NL"] = "Нидерланды", ["DE"] = "Германия", ["FI"] = "Финляндия", ["SE"] = "Швеция",
        ["EE"] = "Эстония", ["LV"] = "Латвия", ["LT"] = "Литва", ["PL"] = "Польша", ["FR"] = "Франция",
        ["GB"] = "Великобритания", ["US"] = "США", ["CA"] = "Канада", ["TR"] = "Турция", ["KZ"] = "Казахстан",
        ["AM"] = "Армения", ["GE"] = "Грузия", ["AZ"] = "Азербайджан", ["BY"] = "Беларусь", ["UA"] = "Украина",
        ["MD"] = "Молдова", ["RO"] = "Румыния", ["BG"] = "Болгария", ["CZ"] = "Чехия", ["AT"] = "Австрия",
        ["CH"] = "Швейцария", ["IT"] = "Италия", ["ES"] = "Испания", ["PT"] = "Португалия", ["NO"] = "Норвегия",
        ["DK"] = "Дания", ["IE"] = "Ирландия", ["BE"] = "Бельгия", ["LU"] = "Люксембург", ["HU"] = "Венгрия",
        ["RS"] = "Сербия", ["CY"] = "Кипр", ["GR"] = "Греция", ["IL"] = "Израиль", ["AE"] = "ОАЭ",
        ["JP"] = "Япония", ["KR"] = "Южная Корея", ["SG"] = "Сингапур", ["HK"] = "Гонконг", ["IN"] = "Индия",
        ["AU"] = "Австралия", ["BR"] = "Бразилия", ["IS"] = "Исландия", ["KG"] = "Киргизия", ["UZ"] = "Узбекистан",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>«Нидерланды», если страна знакома, иначе двухбуквенный код.</summary>
    public string Name => Names.TryGetValue(CountryCode, out var n) ? n : CountryCode;

    /// <summary>Разбор ответа cdn-cgi/trace. null — страны в ответе нет или она неизвестна (XX, T1 — Tor).</summary>
    public static ExitRegion? Parse(string? trace, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(trace))
            return null;
        foreach (var raw in trace.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("loc=", StringComparison.Ordinal))
                continue;
            var code = line[4..].Trim().ToUpperInvariant();
            return code.Length == 2 && code.All(char.IsAsciiLetterUpper) && code != "XX" ? new ExitRegion(code, now) : null;
        }

        return null;
    }
}
