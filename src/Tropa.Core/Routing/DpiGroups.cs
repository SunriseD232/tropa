namespace Tropa.Core.Routing;

/// <summary>Группа сервисов для режима «сначала обход DPI»: свои наборы правил и адрес проверки.</summary>
public sealed record DpiGroup(string Key, string Title, IReadOnlyList<string> RuleSets, Uri ProbeUrl)
{
    /// <summary>Тег переключателя sing-box: «напрямую с обходом» или «через сервер».</summary>
    public string SelectorTag => "dpi-" + Key;
}

/// <summary>
/// Группы режима «сначала обход DPI» (info.ru.json: dpiFirst). Порядок важен: конкретные сервисы
/// раньше общего списка блокировок, чтобы у каждого был свой переключатель и своя проверка.
/// </summary>
public static class DpiGroups
{
    /// <summary>Выход «напрямую» для обхода: адреса узнаёт через удалённый DNS, а не у провайдера.</summary>
    public const string DirectTag = "dpi-direct";

    /// <summary>Вход, через который Тропа проверяет, открывает ли обход сервис.</summary>
    public const string ProbeInboundTag = "probe-dpi";

    // Порядок: конкретные сервисы раньше общего списка блокировок. У каждого свой переключатель и своя
    // проверка — Facebook и Instagram разделены (их домены и CDN не пересекаются), поэтому Facebook
    // идёт напрямую с обходом, даже если Instagram у провайдера так не открывается. «Остальное
    // заблокированное» проверяется по rutracker (закрыт по IP) — обычно уходит на сервер.
    public static IReadOnlyList<DpiGroup> All { get; } =
    [
        new("youtube", "YouTube", ["geosite-youtube"], new Uri("https://www.youtube.com/generate_204")),
        new("discord", "Discord", ["geosite-discord"], new Uri("https://discord.com/api/v10/gateway")),
        new("telegram", "Telegram", ["geosite-telegram", "geoip-telegram"], new Uri("https://telegram.org/")),
        new("facebook", "Facebook", ["geosite-facebook"], new Uri("https://www.facebook.com/")),
        new("instagram", "Instagram", ["geosite-instagram"], new Uri("https://www.instagram.com/")),
        new("twitter", "X (Twitter)", ["geosite-x"], new Uri("https://x.com/")),
        new("blocked", "Остальное заблокированное", RuleSets.Blocked, new Uri("https://rutracker.org/forum/index.php")),
    ];
}
