using System.Globalization;
using Tropa.Core.Security;

namespace Tropa.Core.Parsing;

/// <summary>Служебные сведения, которые панель провайдера отдаёт в заголовках ответа.</summary>
public sealed record SubscriptionInfo
{
    public long? Upload { get; init; }
    public long? Download { get; init; }
    public long? Total { get; init; }
    public DateTimeOffset? Expire { get; init; }
    public int? UpdateIntervalHours { get; init; }
    public string? Title { get; init; }
    public Uri? SupportUrl { get; init; }
    public Uri? WebPageUrl { get; init; }
    public string? Announce { get; init; }

    public long? Remaining => Total is > 0 ? Math.Max(0, Total.Value - (Upload ?? 0) - (Download ?? 0)) : null;
}

/// <summary>
/// Разбор заголовков subscription-userinfo, profile-update-interval, profile-title, support-url,
/// profile-web-page-url, announce. Значения недоверенные: всё очищается и ограничивается.
/// </summary>
public static class SubscriptionHeaders
{
    private const int MaxAnnounceLength = 300;

    public static SubscriptionInfo Parse(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers)
            map.TryAdd(key, value);

        var info = new SubscriptionInfo();
        if (map.TryGetValue("subscription-userinfo", out var userInfo))
            info = ParseUserInfo(userInfo, info);

        if (map.TryGetValue("profile-update-interval", out var interval)
            && int.TryParse(interval.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            && hours is >= 1 and <= 24 * 30)
            info = info with { UpdateIntervalHours = hours };

        if (map.TryGetValue("profile-title", out var title))
        {
            var decoded = title.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)
                ? Base64Text.Decode(title[7..])
                : title;
            var clean = NameSanitizer.Sanitize(decoded, "");
            info = info with { Title = clean.Length == 0 ? null : clean };
        }

        info = info with
        {
            SupportUrl = SafeUrl(map.GetValueOrDefault("support-url")),
            WebPageUrl = SafeUrl(map.GetValueOrDefault("profile-web-page-url")),
        };

        if (map.TryGetValue("announce", out var announce))
        {
            var decoded = announce.StartsWith("base64:", StringComparison.OrdinalIgnoreCase) ? Base64Text.Decode(announce[7..]) : announce;
            var clean = AnnounceText(decoded);
            info = info with { Announce = clean.Length == 0 ? null : clean };
        }

        return info;
    }

    /// <summary>«upload=1; download=2; total=3; expire=1700000000». Порядок и пробелы произвольные.</summary>
    internal static SubscriptionInfo ParseUserInfo(string value, SubscriptionInfo info)
    {
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim().ToLowerInvariant();
            if (!long.TryParse(part[(eq + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                continue;

            info = key switch
            {
                "upload" => info with { Upload = number },
                "download" => info with { Download = number },
                "total" => info with { Total = number },
                // 0 у многих панелей означает «бессрочно».
                "expire" when number > 0 && number < 253402300800 => info with { Expire = DateTimeOffset.FromUnixTimeSeconds(number) },
                _ => info,
            };
        }

        return info;
    }

    // Разрешаем только https и tg: другие схемы (file:, javascript:, ms-*) открыть нельзя.
    private static Uri? SafeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512)
            return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri))
            return null;
        return uri.Scheme is "https" or "tg" ? uri : null;
    }

    private static string AnnounceText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";
        // Объявление может быть многострочным: сохраняем переносы, остальное чистим как имя.
        var lines = value.Replace("\r", "", StringComparison.Ordinal).Split('\n')
            .Select(l => NameSanitizer.Sanitize(l, ""))
            .Where(l => l.Length > 0);
        var text = string.Join('\n', lines);
        return text.Length <= MaxAnnounceLength ? text : text[..MaxAnnounceLength].TrimEnd() + "…";
    }
}
