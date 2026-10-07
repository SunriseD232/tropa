using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Tropa.Core.Parsing;

/// <summary>Проверки отдельных значений. Всё, что пришло из подписки, — недоверенный ввод.</summary>
internal static partial class Validation
{
    public const int MaxLinkLength = 8 * 1024;
    private const int MaxFieldLength = 512;

    public static readonly FrozenSet<string> Fingerprints =
        new[] { "chrome", "firefox", "safari", "ios", "android", "edge", "360", "qq", "random", "randomized" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> AlpnTokens =
        new[] { "h2", "http/1.1", "h3" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IdnMapping Idn = new();

    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[A-Za-z0-9-]{1,63}(?<!-)(\.(?!-)[A-Za-z0-9-]{1,63}(?<!-))*\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex Hostname();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex RealityPublicKey();

    [GeneratedRegex("^([0-9a-fA-F]{2}){0,8}$", RegexOptions.CultureInvariant)]
    private static partial Regex RealityShortId();

    /// <summary>Домен (в т.ч. IDN → punycode) или IP. Возвращает нормализованный адрес.</summary>
    public static string Address(string? raw)
    {
        var value = Text(raw, "адрес сервера");
        if (value.StartsWith('[') && value.EndsWith(']'))
            value = value[1..^1];

        if (IPAddress.TryParse(value, out var ip))
        {
            // «1.2» тоже парсится как IP — принимаем только канонические формы.
            if (ip.ToString().Equals(value, StringComparison.OrdinalIgnoreCase) || value.Contains(':', StringComparison.Ordinal))
                return ip.ToString();
            throw new LinkFormatException($"Некорректный IP-адрес «{value}».");
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(value);
        }
        catch (ArgumentException)
        {
            throw new LinkFormatException($"Некорректный адрес сервера «{value}».");
        }

        if (!Hostname().IsMatch(ascii))
            throw new LinkFormatException($"Некорректный адрес сервера «{value}».");
        return ascii.TrimEnd('.').ToLowerInvariant();
    }

    public static int Port(string? raw)
    {
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new LinkFormatException($"Некорректный порт «{raw}».");
        return port;
    }

    public static string Uuid(string? raw)
    {
        var value = Text(raw, "UUID");
        if (!Guid.TryParseExact(value, "D", out var guid))
            throw new LinkFormatException("UUID имеет неверный формат.");
        return guid.ToString("D");
    }

    public static string Password(string? raw)
    {
        var value = Text(raw, "пароль");
        return value;
    }

    /// <summary>Методы Shadowsocks: 2022 и AEAD. Потоковые шифры и «none» небезопасны — не принимаем.</summary>
    public static readonly FrozenSet<string> SsMethods = new[]
    {
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
        "aes-128-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static string SsMethod(string? raw)
    {
        var value = Text(raw, "метод шифрования").ToLowerInvariant();
        if (value is "chacha20-poly1305")
            value = "chacha20-ietf-poly1305";
        if (!SsMethods.Contains(value))
            throw new LinkFormatException($"Метод Shadowsocks «{value}» не поддерживается: он устарел или небезопасен.");
        return value;
    }

    /// <summary>Для методов 2022 пароль — ключ base64 нужной длины (или «ключ сервера:ключ пользователя»).</summary>
    public static string SsPassword(string method, string? raw)
    {
        var value = Text(raw, "пароль");
        if (!method.StartsWith("2022-", StringComparison.Ordinal))
            return value;
        var keyBytes = method.Contains("128", StringComparison.Ordinal) ? 16 : 32;
        foreach (var part in value.Split(':'))
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(part);
            }
            catch (FormatException)
            {
                throw new LinkFormatException("Для Shadowsocks-2022 пароль должен быть ключом base64.");
            }

            if (key.Length != keyBytes)
                throw new LinkFormatException($"Для метода {method} ключ должен быть {keyBytes} байт.");
        }

        return value;
    }

    public static string? Sni(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        try
        {
            return Address(raw);
        }
        catch (LinkFormatException)
        {
            throw new LinkFormatException($"Некорректное имя сервера (SNI) «{raw}».");
        }
    }

    public static string? Alpn(string? raw, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var tokens = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var known = tokens.Where(AlpnTokens.Contains).Distinct(StringComparer.Ordinal).ToList();
        if (known.Count != tokens.Length)
            warnings.Add($"Неизвестные значения ALPN проигнорированы: {string.Join(", ", tokens.Except(known))}.");
        return known.Count == 0 ? null : string.Join(',', known);
    }

    public static string? Fingerprint(string? raw, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var value = raw.Trim().ToLowerInvariant();
        if (Fingerprints.Contains(value))
            return value;
        warnings.Add($"Неизвестный отпечаток «{raw}», будет использован chrome.");
        return "chrome";
    }

    public static string RealityKey(string? raw)
    {
        if (raw is null || !RealityPublicKey().IsMatch(raw))
            throw new LinkFormatException("Публичный ключ Reality (pbk) имеет неверный формат.");
        return raw;
    }

    public static string RealitySid(string? raw)
    {
        var value = raw ?? "";
        if (!RealityShortId().IsMatch(value))
            throw new LinkFormatException("Short ID Reality (sid) должен состоять из 0–16 шестнадцатеричных символов чётной длины.");
        return value.ToLowerInvariant();
    }

    /// <summary>Путь или хост транспорта: без управляющих символов и разумной длины.</summary>
    public static string? Optional(string? raw, string what)
    {
        if (string.IsNullOrEmpty(raw))
            return null;
        return Text(raw, what);
    }

    public static string Text(string? raw, string what)
    {
        if (string.IsNullOrEmpty(raw))
            throw new LinkFormatException($"Не указан {what}.");
        if (raw.Length > MaxFieldLength)
            throw new LinkFormatException($"Слишком длинное значение: {what}.");
        foreach (var ch in raw)
        {
            if (char.IsControl(ch))
                throw new LinkFormatException($"Недопустимые символы: {what}.");
        }

        return raw;
    }

    public static bool Flag(string? raw) =>
        raw is not null && (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase));
}
