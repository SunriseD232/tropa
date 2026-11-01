using System.Globalization;
using System.Text.Json.Nodes;
using Tropa.Core.Cores;
using Tropa.Infrastructure.Storage;

namespace Tropa.Infrastructure.Cores;

/// <summary>Откуда взята выбранная версия ядра (ADR-058).</summary>
public enum CorePickSource { Manifest, GitHub }

/// <summary>Установленная по выбору пользователя версия ядра.</summary>
public sealed record CorePick(CoreEntry Entry, CorePickSource Source, bool Preview, DateTimeOffset Installed)
{
    public string Core => Entry.Name;

    public string Version => Entry.Version;
}

/// <summary>Что установлено и что выбрано: для каждого ядра — выбранная версия или null («как в выпуске»).</summary>
public sealed record CorePicksState(IReadOnlyList<CorePick> Installed, IReadOnlyDictionary<string, string> Selected, IReadOnlyDictionary<string, string> Previous)
{
    public static CorePicksState Empty { get; } = new([], new Dictionary<string, string>(), new Dictionary<string, string>());

    public CorePick? SelectedPick(string core) =>
        Selected.TryGetValue(core, out var v) ? Installed.FirstOrDefault(p => p.Core == core && p.Version == v) : null;
}

/// <summary>
/// Версии ядер, выбранные пользователем вместо версий выпуска (ADR-058): &lt;root&gt;\&lt;ядро&gt;\&lt;версия&gt;\&lt;exe&gt;
/// и picks.json со списком и выбором. Служба держит каталог в %ProgramData%\Tropa\cores-picked
/// (запись только SYSTEM и администраторам), интерфейс — свою копию в %LOCALAPPDATA% для проверок без службы.
/// Хэш exe сверяется при установке и перед каждым запуском (CoreProcess). Хранится до
/// <see cref="KeepPerCore"/> версий каждого ядра — их можно выбрать снова без скачивания.
/// </summary>
public sealed class CorePickStore(string root)
{
    public const int KeepPerCore = 3;
    private const string StateFile = "picks.json";

    public string Root { get; } = root;

    public CorePicksState Load()
    {
        var path = Path.Combine(Root, StateFile);
        if (!File.Exists(path))
            return CorePicksState.Empty;
        try
        {
            if (JsonNode.Parse(File.ReadAllBytes(path)) is not JsonObject o)
                return CorePicksState.Empty;
            var installed = new List<CorePick>();
            foreach (var n in o["installed"] as JsonArray ?? [])
            {
                if (n is not JsonObject x)
                    continue;
                var entry = CoresLock.Parse(new JsonObject { ["schema"] = CoresLock.SupportedSchema, ["cores"] = new JsonArray(x["entry"]?.DeepClone()) }.ToJsonString()).Cores[0];
                if (!CoreReleases.IsValidVersion(entry.Version) || CoreReleases.Repos.All(r => r.Core != entry.Name))
                    continue;
                installed.Add(new CorePick(entry, Enum.TryParse<CorePickSource>((string?)x["source"], out var src) ? src : CorePickSource.GitHub,
                    (bool?)x["preview"] ?? false, DateTimeOffset.TryParse((string?)x["installed"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : DateTimeOffset.MinValue));
            }

            return new CorePicksState(installed, Map(o["selected"]), Map(o["previous"]));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            // Испорченный файл выбора — работают версии выпуска.
            return CorePicksState.Empty;
        }
    }

    /// <summary>
    /// Применяет выбор к файлам выпуска. Выбранная версия, чей exe пропал или не совпадает по хэшу,
    /// пропускается — тогда работает версия выпуска.
    /// </summary>
    public CoreLocations Apply(CoreLocations release)
    {
        ArgumentNullException.ThrowIfNull(release);
        var state = Load();
        var picked = new List<(CoreEntry, string)>();
        foreach (var core in release.ReleaseCores.Cores)
        {
            if (state.SelectedPick(core.Name) is not { } pick)
                continue;
            var dir = DirectoryOf(pick.Core, pick.Version);
            var exe = Path.Combine(dir, pick.Entry.Files[0].Target);
            if (File.Exists(exe) && !new FileInfo(exe).Attributes.HasFlag(FileAttributes.ReparsePoint))
                picked.Add((pick.Entry, dir));
        }

        return release.WithPicked(picked);
    }

    /// <summary>
    /// Копирует exe (хэш сверяется с <paramref name="entry"/>) в каталог версии и делает её выбранной.
    /// Прежний выбор запоминается для «Вернуть прежнюю».
    /// </summary>
    public CorePicksState Install(CoreEntry entry, CorePickSource source, bool preview, string exeSourcePath, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var core = CoreReleases.For(entry.Name).Core;
        var version = CoreReleases.RequireVersion(entry.Version);
        var file = entry.Files[0];
        var dir = DirectoryOf(core, version);
        var target = Path.Combine(dir, file.Target);
        if (new FileInfo(exeSourcePath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IntegrityException("Файл ядра — ссылка на другой файл. Установка отменена.");

        var state = Load();
        if (!(File.Exists(target) && HashOf(target) == file.Sha256))
        {
            var temp = Path.Combine(Root, $"incoming-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temp);
            try
            {
                using (var input = new FileStream(exeSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (PinnedFiles.Sha256(input) != file.Sha256)
                        throw new IntegrityException($"Файл {file.Target} не совпадает с ожидаемым хэшем. Установка отменена.");
                    input.Position = 0;
                    using var output = new FileStream(Path.Combine(temp, file.Target), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                }

                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
                Directory.CreateDirectory(Path.GetDirectoryName(dir)!);
                Directory.Move(temp, dir);
            }
            finally
            {
                if (Directory.Exists(temp))
                    Directory.Delete(temp, recursive: true);
            }
        }

        var installed = state.Installed.Where(p => !(p.Core == core && p.Version == version)).Append(new CorePick(entry, source, preview, now)).ToList();
        var next = Select(state with { Installed = installed }, core, version);
        return Save(Prune(next));
    }

    /// <summary>Выбрать установленную версию; null — вернуться к версии выпуска.</summary>
    public CorePicksState Select(string core, string? version)
    {
        core = CoreReleases.For(core).Core;
        var state = Load();
        if (version is not null && state.Installed.All(p => !(p.Core == core && p.Version == version)))
            throw new InvalidOperationException($"Версия {core} {version} не установлена.");
        return Save(Select(state, core, version));
    }

    private static CorePicksState Select(CorePicksState state, string core, string? version)
    {
        var selected = new Dictionary<string, string>(state.Selected, StringComparer.Ordinal);
        var previous = new Dictionary<string, string>(state.Previous, StringComparer.Ordinal);
        var current = selected.GetValueOrDefault(core);
        if (current == version)
            return state;
        // «Прежняя» — то, что работало до этого выбора; пустая строка — версия выпуска.
        previous[core] = current ?? "";
        if (version is null)
            selected.Remove(core);
        else
            selected[core] = version;
        return state with { Selected = selected, Previous = previous };
    }

    /// <summary>Оставляет по <see cref="KeepPerCore"/> версий каждого ядра; выбранную и прежнюю не трогает.</summary>
    private CorePicksState Prune(CorePicksState state)
    {
        var keep = new List<CorePick>();
        foreach (var group in state.Installed.GroupBy(p => p.Core))
        {
            var protectedVersions = new[] { state.Selected.GetValueOrDefault(group.Key), state.Previous.GetValueOrDefault(group.Key) };
            var ordered = group.OrderByDescending(p => protectedVersions.Contains(p.Version)).ThenByDescending(p => p.Installed).ToList();
            keep.AddRange(ordered.Take(KeepPerCore));
            foreach (var old in ordered.Skip(KeepPerCore))
            {
                try
                {
                    Directory.Delete(DirectoryOf(old.Core, old.Version), recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Файл ещё запущен — каталог останется, в списке его уже нет.
                }
            }
        }

        return state with { Installed = keep };
    }

    private CorePicksState Save(CorePicksState state)
    {
        Directory.CreateDirectory(Root);
        var installed = new JsonArray();
        foreach (var p in state.Installed)
        {
            var e = p.Entry;
            installed.Add(new JsonObject
            {
                ["entry"] = new JsonObject
                {
                    ["name"] = e.Name, ["version"] = e.Version, ["source"] = e.Source, ["license"] = e.License, ["url"] = e.Url, ["archiveSha256"] = e.ArchiveSha256,
                    ["files"] = new JsonArray([.. e.Files.Select(f => (JsonNode)new JsonObject { ["entry"] = f.Entry, ["target"] = f.Target, ["sha256"] = f.Sha256 })]),
                },
                ["source"] = p.Source.ToString(),
                ["preview"] = p.Preview,
                ["installed"] = p.Installed.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        var o = new JsonObject
        {
            ["installed"] = installed,
            ["selected"] = ToJson(state.Selected),
            ["previous"] = ToJson(state.Previous),
        };
        AtomicFile.WriteAllBytes(Path.Combine(Root, StateFile), System.Text.Encoding.UTF8.GetBytes(o.ToJsonString()));
        return state;
    }

    private string DirectoryOf(string core, string version) => Path.Combine(Root, CoreReleases.For(core).Core, CoreReleases.RequireVersion(version));

    private static string HashOf(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return PinnedFiles.Sha256(s);
    }

    private static Dictionary<string, string> Map(JsonNode? node)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (node is JsonObject o)
        {
            foreach (var (k, v) in o)
            {
                if (CoreReleases.Repos.Any(r => r.Core == k) && (string?)v is { } s && (s.Length == 0 || CoreReleases.IsValidVersion(s)))
                    result[k] = s;
            }
        }

        return result;
    }

    private static JsonObject ToJson(IReadOnlyDictionary<string, string> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map)
            o[k] = v;
        return o;
    }
}
