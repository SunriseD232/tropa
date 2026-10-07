using System.Text.RegularExpressions;

namespace Tropa.Core.Security;

/// <summary>
/// Убирает секреты из текста перед записью в лог, отчёт или отправкой в интерфейс
/// (docs/02-security.md, §3.4). Два уровня защиты: точная замена известных секретов
/// (UUID, пароли, ключи, URL подписок текущего пользователя) и шаблоны для неизвестных.
/// </summary>
public sealed partial class SecretScrubber
{
    private const int MinKnownSecretLength = 6;
    private readonly string[] _known;

    public SecretScrubber(IEnumerable<string>? knownSecrets = null)
    {
        _known = (knownSecrets ?? [])
            .Where(s => !string.IsNullOrEmpty(s) && s.Length >= MinKnownSecretLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(s => s.Length) // сначала длинные: URL подписки содержит более короткие части
            .ToArray();
    }

    public static SecretScrubber PatternsOnly { get; } = new();

    [GeneratedRegex(@"\b(vless|vmess|trojan|ss|ssr|hysteria2|hy2|tuic|wireguard|socks5?|https?)://[^\s""'<>]*@[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkWithCredentials();

    [GeneratedRegex(@"\b(vless|vmess|trojan|ss|ssr|hysteria2|hy2|tuic)://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShareLink();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"(?<key>""?(?:password|passwd|pass|secret|token|uuid|id|private_key|public_key|publicKey|short_id|shortId|pbk|sid|username|user|auth)""?\s*[:=]\s*""?)(?<value>[^""\s,&}\]]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?<prefix>https?://[^/\s""'<>]+)(?<rest>/[^\s""'<>]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrl();

    [GeneratedRegex(@"[A-Za-z0-9_-]{16,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenLike();

    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{43}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex X25519Key();

    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var result = text;
        foreach (var secret in _known)
            result = result.Replace(secret, "***", StringComparison.Ordinal);

        result = ShareLink().Replace(result, m => m.Groups[1].Value + "://***");
        result = LinkWithCredentials().Replace(result, m => m.Groups[1].Value + "://***");
        result = KeyValue().Replace(result, m => m.Groups["key"].Value + "***");
        result = Uuid().Replace(result, "<uuid>");
        result = HttpUrl().Replace(result, m =>
            TokenLike().IsMatch(m.Groups["rest"].Value) ? m.Groups["prefix"].Value + "/***" : m.Value);
        result = X25519Key().Replace(result, "***");
        return result;
    }
}
