namespace Tropa.Core.Routing;

/// <summary>
/// Теги наборов правил. Файлы <c>{тег}.srs</c> лежат в каталоге гео-баз и проверяются по хэшу
/// перед использованием; удалённые (remote) наборы правил ядру не передаются никогда.
/// </summary>
public static class RuleSets
{
    public const string RuServices = "ru-services";
    public const string GeositeCategoryRu = "geosite-category-ru";
    public const string GeoipRu = "geoip-ru";
    public const string GeositeRuBlocked = "geosite-ru-blocked";
    public const string GeoipRuBlocked = "geoip-ru-blocked";
    public const string GeoipRuBlockedCommunity = "geoip-ru-blocked-community";
    public const string GeositeGovRu = "geosite-category-gov-ru";
    public const string GeositeBankRu = "geosite-category-bank-ru";
    public const string GeositeRuOnlyInside = "geosite-ru-available-only-inside";
    /// <summary>ИИ-сервисы, закрытые для России с их стороны (OpenAI, Anthropic, Gemini…): только через сервер.</summary>
    public const string GeositeAiNonCn = "geosite-category-ai-noncn";

    /// <summary>Всегда напрямую: госуслуги, банки, сайты, доступные только из России.</summary>
    public static IReadOnlyList<string> AlwaysDirect { get; } = [RuServices, GeositeGovRu, GeositeBankRu, GeositeRuOnlyInside];

    /// <summary>Заблокированное в России (runetfreedom/russia-v2ray-rules-dat).</summary>
    public static IReadOnlyList<string> Blocked { get; } = [GeositeRuBlocked, GeoipRuBlocked, GeoipRuBlockedCommunity];

    /// <summary>
    /// Домены, которые при обходе DPI без сервера идут напрямую с фрагментацией: список блокировок
    /// и сервисы, которые чаще всего замедляют (YouTube, Discord, Telegram).
    /// </summary>
    public static IReadOnlyList<string> BypassDomains { get; } = [GeositeRuBlocked, "geosite-youtube", "geosite-discord", "geosite-telegram"];

    /// <summary>Сайты, к которым по умолчанию применяется фрагментация (набор «Мягко»).</summary>
    public static IReadOnlyList<string> FragmentDefaults { get; } =
        ["youtube.com", "googlevideo.com", "ytimg.com", "ggpht.com", "youtu.be", "youtube-nocookie.com", "yt.be"];

    /// <summary>Тег для geosite:X / geoip:X из пользовательских правил.</summary>
    public static string GeositeTag(string name) => "geosite-" + Normalize(name);

    public static string GeoipTag(string name) => "geoip-" + Normalize(name);

    private static string Normalize(string name)
    {
        var lower = name.Trim().ToLowerInvariant();
        if (lower.Length == 0 || lower.Length > 64 || !lower.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '!' or '@'))
            throw new ArgumentException($"Недопустимое имя гео-базы «{name}».", nameof(name));
        return lower.Replace('!', '-').Replace('@', '-');
    }
}
