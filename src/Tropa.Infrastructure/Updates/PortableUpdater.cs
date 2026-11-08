using System.IO.Compression;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;

namespace Tropa.Infrastructure.Updates;

/// <summary>
/// Обновление портативной Тропы (ADR-063). Запущенный exe себя не перезапишет, поэтому:
/// 1) Тропа скачивает zip (хэш из подписанного манифеста) и распаковывает его в <c>data\local\update-portable</c>;
/// 2) запускает Tropa.exe новой версии из распакованной папки с <c>--apply-portable-update</c> и закрывается;
/// 3) новая версия ждёт, пока старая (и её служба) завершится, копирует файлы поверх папки Тропы — кроме
///    <c>data</c>, — и запускает обновлённую Тропу.
/// </summary>
public static class PortableUpdater
{
    public const string ApplyArgument = "--apply-portable-update";

    public const string StagingFolder = "update-portable";

    private const long MaxUnpackedBytes = 600L * 1024 * 1024;

    /// <summary>Распаковывает архив в пустой каталог и возвращает папку с Tropa.exe и меткой портативной версии.</summary>
    public static string Stage(string zipPath, string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
        Directory.CreateDirectory(stagingDirectory);
        var root = Path.GetFullPath(stagingDirectory) + Path.DirectorySeparatorChar;
        long total = 0;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                // Защита от путей вида «..\..\x» и абсолютных: всё строго внутри каталога распаковки.
                var target = Path.GetFullPath(Path.Combine(stagingDirectory, entry.FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new IntegrityException("В архиве Тропы файл вне папки. Обновление отменено.");
                total += entry.Length;
                if (total > MaxUnpackedBytes)
                    throw new IntegrityException("Архив Тропы слишком большой. Обновление отменено.");
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(target);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: false);
            }
        }

        return FindProgramRoot(stagingDirectory)
            ?? throw new IntegrityException("В архиве нет портативной Тропы. Обновление отменено.");
    }

    /// <summary>Папка с Tropa.exe и меткой: сам каталог или единственная папка в нём (zip с папкой «Tropa»).</summary>
    public static string? FindProgramRoot(string directory)
    {
        if (IsProgramRoot(directory))
            return directory;
        var subdirs = Directory.GetDirectories(directory);
        return subdirs.Length == 1 && IsProgramRoot(subdirs[0]) ? subdirs[0] : null;
    }

    private static bool IsProgramRoot(string dir) =>
        File.Exists(Path.Combine(dir, "Tropa.exe")) && File.Exists(Path.Combine(dir, PortableMode.MarkerFile));

    /// <summary>
    /// Копирует программу поверх папки портативной Тропы. Папку <c>data</c> (настройки, подписки, журнал)
    /// не трогает; файлы, которых нет в новой версии, не удаляет. Пишем только в папку, где лежит метка.
    /// </summary>
    public static void Apply(string source, string target, TimeSpan lockedFileWait)
    {
        if (!IsProgramRoot(source))
            throw new IntegrityException("Новая версия Тропы распакована не полностью.");
        if (!File.Exists(Path.Combine(target, PortableMode.MarkerFile)))
            throw new InvalidOperationException("Папка назначения — не портативная Тропа.");
        var sourceRoot = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetFullPath(file)[sourceRoot.Length..];
            if (relative.StartsWith(PortableMode.DataFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            CopyWithRetry(file, destination, lockedFileWait);
        }

        RemoveLegacyFiles(source, target, LegacyFiles(), lockedFileWait);
    }

    /// <summary>
    /// Файлы старой многофайловой раскладки (0.1.28), которых нет в новой версии, удаляются — только по явному
    /// списку, только в корне папки с меткой и только если новая версия их не содержит (docs/02-security.md §3.7).
    /// </summary>
    internal static void RemoveLegacyFiles(string source, string target, IEnumerable<string> legacy, TimeSpan lockedFileWait = default)
    {
        var pending = legacy
            .Where(name => name.Length > 0 && name.IndexOfAny(['/', '\\', ':']) < 0 && !name.Contains("..", StringComparison.Ordinal)
                && !name.Equals(PortableMode.MarkerFile, StringComparison.OrdinalIgnoreCase)
                && !File.Exists(Path.Combine(source, name)))
            .Select(name => Path.Combine(target, name))
            .Where(File.Exists)
            .ToList();
        // Антивирус и старая служба держат файлы недолго — повторяем до lockedFileWait; что не удалилось, не мешает работе.
        var until = DateTime.UtcNow + lockedFileWait;
        while (true)
        {
            pending.RemoveAll(TryDelete);
            if (pending.Count == 0 || DateTime.UtcNow >= until)
                return;
            Thread.Sleep(500);
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> LegacyFiles()
    {
        using var stream = typeof(PortableUpdater).Assembly.GetManifestResourceStream("Tropa.portable-legacy-files.txt");
        if (stream is null)
            return [];
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))];
    }

    /// <summary>Служба старой версии закрывается чуть позже интерфейса: занятый файл пробуем ещё раз.</summary>
    private static void CopyWithRetry(string source, string destination, TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;
        var temp = destination + ".new";
        while (true)
        {
            try
            {
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, destination, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && DateTime.UtcNow < until)
            {
                Thread.Sleep(500);
            }
        }
    }

    /// <summary>Остатки прошлого обновления (распакованная папка и архив) — после запуска новой версии они не нужны.</summary>
    public static void CleanUp(string localDirectory)
    {
        foreach (var path in new[] { Path.Combine(localDirectory, StagingFolder), Path.Combine(localDirectory, "update-download") })
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Программа из распакованной папки ещё закрывается — уберём в следующий раз.
            }
        }
    }
}
