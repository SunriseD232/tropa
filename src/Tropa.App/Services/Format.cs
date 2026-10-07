using System.Globalization;
using System.Text;
using Tropa.Core.Model;

namespace Tropa.App.Services;

internal static class Format
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Русское множественное число: Plural(5, "сервер", "сервера", "серверов") → «5 серверов».</summary>
    public static string Plural(int n, string one, string few, string many)
    {
        var mod100 = Math.Abs(n) % 100;
        var mod10 = mod100 % 10;
        var word = mod100 is >= 11 and <= 14 ? many : mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
        return $"{n} {word}";
    }

    public static string Speed(long bytesPerSecond) => Bytes(bytesPerSecond) + "/с";

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => (bytes / 1024.0).ToString("0", Ru) + " КБ",
        < 1024L * 1024 * 1024 => (bytes / 1024.0 / 1024).ToString("0.0", Ru) + " МБ",
        _ => (bytes / 1024.0 / 1024 / 1024).ToString("0.0", Ru) + " ГБ",
    };

    public static string Duration(TimeSpan t) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    /// <summary>Код страны из эмодзи-флага в начале имени («🇳🇱 Amsterdam» → «NL»).</summary>
    public static string CountryCode(string name)
    {
        var runes = name.EnumerateRunes().Take(2).ToArray();
        if (runes.Length == 2 && IsRegional(runes[0]) && IsRegional(runes[1]))
            return new string([(char)('A' + runes[0].Value - 0x1F1E6), (char)('A' + runes[1].Value - 0x1F1E6)]);
        return "··";
    }

    /// <summary>Имя без флага в начале: флаг уже показан плашкой с кодом страны.</summary>
    public static string NameWithoutFlag(string name)
    {
        var runes = name.EnumerateRunes().ToArray();
        if (runes.Length >= 2 && IsRegional(runes[0]) && IsRegional(runes[1]))
        {
            var sb = new StringBuilder();
            foreach (var r in runes.Skip(2))
                sb.Append(r.ToString());
            var rest = sb.ToString().Trim();
            return rest.Length == 0 ? name : rest;
        }

        return name;
    }

    private static bool IsRegional(Rune r) => r.Value is >= 0x1F1E6 and <= 0x1F1FF;

    public static string Protocol(Profile p) => p.Protocol switch
    {
        Core.Model.Protocol.Vless => "VLESS",
        Core.Model.Protocol.Vmess => "VMess",
        _ => "Trojan",
    };

    public static string Transport(Profile p)
    {
        var t = p.Transport.Type switch
        {
            TransportType.Tcp => "TCP",
            TransportType.Ws => "WS",
            TransportType.Grpc => "gRPC",
            TransportType.HttpUpgrade => "HTTPUpgrade",
            _ => "XHTTP",
        };
        var s = p.Security.Type switch
        {
            SecurityType.Reality => "Reality",
            SecurityType.Tls => "TLS",
            _ => "без шифрования",
        };
        return p.Flow == VlessFlow.XtlsRprxVision ? $"{t} · {s} · Vision" : $"{t} · {s}";
    }
}
