using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Tropa.Core.Generation;

namespace Tropa.Core.Model;

/// <summary>
/// Проверка значений, которые пользователь вводит в настройках. null — значение верное,
/// иначе понятная причина. Неверное значение не сохраняется.
/// </summary>
public static partial class SettingsValidation
{
    [GeneratedRegex(@"^(\d{1,5})-(\d{1,5})$", RegexOptions.CultureInvariant)]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"^(?:(?:Ctrl|Alt|Shift|Win)\+){1,3}(?:[A-Z0-9]|F(?:[1-9]|1[0-2]))$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HotkeyPattern();

    [GeneratedRegex(@"^[A-Za-z0-9.*\-:\[\];<>]*$", RegexOptions.CultureInvariant)]
    private static partial Regex BypassPattern();

    public static string? Port(int port, params int[] taken)
    {
        if (port is < 1024 or > 65535)
            return "Порт должен быть от 1024 до 65535.";
        return taken?.Contains(port) == true ? "Этот порт уже занят другим пунктом настроек." : null;
    }

    public static string? Mtu(int mtu) => mtu is < 1280 or > 9000 ? "MTU должен быть от 1280 до 9000." : null;

    /// <summary>Диапазон вида «100-200».</summary>
    public static string? Range(string value, int min, int max)
    {
        var m = RangePattern().Match(value?.Trim() ?? "");
        if (!m.Success)
            return "Укажите диапазон в виде «от-до», например 100-200.";
        var a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (a > b)
            return "Начало диапазона больше конца.";
        return a < min || b > max ? $"Значения должны быть от {min} до {max}." : null;
    }

    public static string? HttpUrl(string value, bool httpsOnly)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && (httpsOnly || u.Scheme != Uri.UriSchemeHttp)))
            return httpsOnly ? "Нужен адрес https://…" : "Нужен адрес http:// или https://…";
        return null;
    }

    /// <summary>DNS-сервер: так же, как его поймёт генератор конфига.</summary>
    public static string? Dns(string value)
    {
        try
        {
            SingBoxConfigBuilder.DnsServer("check", value ?? "", null);
            return null;
        }
        catch (UnsupportedProfileException ex)
        {
            return ex.Message;
        }
    }

    public static string? HostPort(string value)
    {
        var v = value?.Trim() ?? "";
        var colon = v.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(v[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            return "Укажите адрес и порт, например stun.l.google.com:19302.";
        var host = v[..colon].Trim('[', ']');
        return IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns ? null : "Неверный адрес.";
    }

    public static string? Hotkey(string value) =>
        string.IsNullOrWhiteSpace(value) || HotkeyPattern().IsMatch(value.Replace(" ", "", StringComparison.Ordinal))
            ? null
            : "Например: Ctrl+Alt+P. Пусто — без горячей клавиши.";

    public static string? SysBypass(string value) =>
        value is not null && value.Length <= 2000 && BypassPattern().IsMatch(value) ? null : "Только адреса и маски через «;», например localhost;192.168.*";

    /// <summary>Строки «домен адрес»; пустые строки и комментарии (#) разрешены.</summary>
    public static string? Hosts(string value)
    {
        var lines = (value ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        if (lines.Count > 500)
            return "Не больше 500 строк.";
        var parsed = SingBoxConfigBuilder.ParseHosts(value ?? "");
        return parsed.Count == lines.Count ? null : "Каждая строка — «домен адрес», например router.lan 192.168.1.1.";
    }

    public static string? UserAgent(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && value.All(c => c >= ' ' && c < 127) ? null : "Латинские буквы, цифры и знаки, до 200 символов.";
}
