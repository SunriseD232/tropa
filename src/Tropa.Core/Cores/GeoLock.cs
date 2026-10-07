using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tropa.Core.Cores;

/// <summary>
/// Закреплённые наборы правил (tools/geo.lock.json, ADR-015): ссылка на конкретный коммит и SHA-256.
/// </summary>
public sealed partial record GeoLock(int Schema, IReadOnlyList<GeoFile> Files)
{
    [GeneratedRegex(@"^https://raw\.githubusercontent\.com/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/[0-9a-f]{40}/(?:[A-Za-z0-9._/-]|%2[1-9A-F]|%40)+\.srs$", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedUrl();

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeName();

    public static GeoLock Parse(string json)
    {
        GeoLock? lockFile;
        try
        {
            lockFile = JsonSerializer.Deserialize(json, GeoLockJsonContext.Default.GeoLock);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Lock-файл наборов правил повреждён.", ex);
        }

        if (lockFile is null || lockFile.Schema != 1 || lockFile.Files is null || lockFile.Files.Count == 0)
            throw new InvalidDataException("Lock-файл наборов правил пуст или неизвестной версии.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in lockFile.Files)
        {
            if (f.Name is null || !SafeName().IsMatch(f.Name) || !names.Add(f.Name))
                throw new InvalidDataException($"Недопустимое или повторяющееся имя набора правил «{f.Name}».");
            if (f.Url is null || !PinnedUrl().IsMatch(f.Url))
                throw new InvalidDataException($"Набор «{f.Name}»: ссылка должна указывать на конкретный коммит.");
            HashFormat.Require(f.Sha256, $"Набор «{f.Name}»");
        }

        return lockFile;
    }
}

public sealed record GeoFile(string Name, string Url, string Sha256, long Size, string? License)
{
    public string FileName => Name + ".srs";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GeoLock))]
internal sealed partial class GeoLockJsonContext : JsonSerializerContext;
