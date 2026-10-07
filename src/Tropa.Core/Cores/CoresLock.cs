using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tropa.Core.Cores;

/// <summary>
/// Закреплённые версии ядер и их SHA-256 (tools/cores.lock.json).
/// Служба сверяет хэш исполняемого файла с этим списком перед каждым запуском ядра.
/// </summary>
public sealed record CoresLock(int Schema, IReadOnlyList<CoreEntry> Cores)
{
    public const int SupportedSchema = 1;

    public CoreEntry Get(string name) =>
        Cores.FirstOrDefault(c => c.Name == name)
        ?? throw new KeyNotFoundException($"Ядро «{name}» не закреплено в lock-файле.");

    /// <summary>Разбирает и строго проверяет lock-файл. Любое отклонение — исключение.</summary>
    public static CoresLock Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        CoresLock? lockFile;
        try
        {
            lockFile = JsonSerializer.Deserialize(json, CoresLockJsonContext.Default.CoresLock);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Lock-файл ядер повреждён.", ex);
        }

        if (lockFile is null)
            throw new InvalidDataException("Lock-файл ядер пуст.");
        if (lockFile.Schema != SupportedSchema)
            throw new InvalidDataException($"Неподдерживаемая версия lock-файла: {lockFile.Schema}.");
        if (lockFile.Cores is null || lockFile.Cores.Count == 0)
            throw new InvalidDataException("В lock-файле нет ядер.");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var core in lockFile.Cores)
        {
            core.Validate();
            if (!names.Add(core.Name))
                throw new InvalidDataException($"Ядро «{core.Name}» указано дважды.");
            foreach (var file in core.Files)
            {
                if (!targets.Add(file.Target))
                    throw new InvalidDataException($"Файл «{file.Target}» указан дважды.");
            }
        }

        return lockFile;
    }
}

public sealed record CoreEntry(
    string Name,
    string Version,
    string Source,
    string License,
    string Url,
    string ArchiveSha256,
    IReadOnlyList<CoreFile> Files)
{
    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Version))
            throw new InvalidDataException("У ядра не указано имя или версия.");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Ядро «{Name}»: разрешены только https-ссылки на github.com.");
        HashFormat.Require(ArchiveSha256, $"Ядро «{Name}», архив");
        if (Files is null || Files.Count == 0)
            throw new InvalidDataException($"Ядро «{Name}»: не указаны файлы.");
        foreach (var file in Files)
            file.Validate(Name);
    }
}

public sealed record CoreFile(string Entry, string Target, string Sha256)
{
    internal void Validate(string coreName)
    {
        if (string.IsNullOrWhiteSpace(Entry))
            throw new InvalidDataException($"Ядро «{coreName}»: пустой путь в архиве.");
        if (!SafeFileName.IsValid(Target))
            throw new InvalidDataException($"Ядро «{coreName}»: недопустимое имя файла «{Target}».");
        HashFormat.Require(Sha256, $"Ядро «{coreName}», файл {Target}");
    }
}

internal static partial class HashFormat
{
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    public static void Require(string? value, string what)
    {
        if (value is null || !Sha256Hex().IsMatch(value))
            throw new InvalidDataException($"{what}: SHA-256 должен быть 64 шестнадцатеричными символами в нижнем регистре.");
    }
}

/// <summary>Только имя файла: без каталогов, без «..», без зарезервированных имён Windows.</summary>
internal static partial class SafeFileName
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex Allowed();

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static bool IsValid(string? name) =>
        name is not null
        && Allowed().IsMatch(name)
        && !name.Contains("..", StringComparison.Ordinal)
        && !name.EndsWith('.')
        && !Reserved.Contains(name.Split('.')[0]);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(CoresLock))]
internal sealed partial class CoresLockJsonContext : JsonSerializerContext;
