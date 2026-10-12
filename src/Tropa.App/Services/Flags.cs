using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Tropa.App.Services;

/// <summary>
/// Флаги стран картинками (Assets/Flags, набор flag-icons, MIT): Windows не рисует флаги-эмодзи,
/// вместо них показывает буквы. Картинки грузятся один раз и кэшируются.
/// </summary>
internal static class Flags
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    /// <summary>Флаг по коду ISO 3166 («NL»); null — кода нет или флага для него нет.</summary>
    public static Bitmap? Get(string? code)
    {
        if (code is not { Length: 2 } || !code.All(char.IsAsciiLetter))
            return null;
        var key = code.ToLowerInvariant();
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached))
                return cached;
            Bitmap? bitmap = null;
            var uri = new Uri($"avares://Tropa/Assets/Flags/{key}.png");
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                bitmap = new Bitmap(stream);
            }

            Cache[key] = bitmap;
            return bitmap;
        }
    }
}
