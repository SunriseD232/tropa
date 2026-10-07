using System.Reflection;
using System.Security.Cryptography;
using Tropa.Core.Cores;

namespace Tropa.Infrastructure.Cores;

/// <summary>Файл не прошёл проверку хэша: его нельзя запускать или передавать ядру.</summary>
public sealed class IntegrityException(string message) : Exception(message);

/// <summary>
/// Закреплённые хэши ядер и наборов правил. Берутся только из ресурсов сборки (ADR-015):
/// lock-файл рядом с программой ни на что не влияет.
/// </summary>
public static class PinnedFiles
{
    public static CoresLock Cores { get; } = CoresLock.Parse(ReadResource("Tropa.cores.lock.json"));

    public static GeoLock Geo { get; } = GeoLock.Parse(ReadResource("Tropa.geo.lock.json"));

    private static string ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"В сборке нет ресурса {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string Sha256(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Открывает файл так, что его нельзя изменить, удалить или переименовать, пока поток открыт,
    /// и проверяет хэш. Вызывающий держит поток открытым до запуска процесса — так закрыто окно
    /// между проверкой и использованием (подмена файла после проверки).
    /// </summary>
    public static FileStream OpenVerified(string path, string expectedSha256)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (FileNotFoundException)
        {
            throw new IntegrityException($"Файл {Path.GetFileName(path)} не найден. Переустановите Тропу.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new IntegrityException($"Файл {Path.GetFileName(path)} не найден. Переустановите Тропу.");
        }

        var actual = Sha256(stream);
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            stream.Dispose();
            throw new IntegrityException(
                $"Файл {Path.GetFileName(path)} изменён: его хэш не совпадает с закреплённым. Запуск отменён. Переустановите Тропу и проверьте компьютер антивирусом.");
        }

        stream.Position = 0;
        return stream;
    }

    /// <summary>
    /// Копирует наборы правил из каталога программы в рабочий каталог, проверяя хэш каждого,
    /// и проверяет уже лежащие там файлы. Неверный файл перезаписывается правильным.
    /// </summary>
    public static void EnsureGeoInstalled(GeoLock geo, string sourceDirectory, string targetDirectory)
    {
        ArgumentNullException.ThrowIfNull(geo);
        Directory.CreateDirectory(targetDirectory);
        foreach (var file in geo.Files)
        {
            var target = Path.Combine(targetDirectory, file.FileName);
            if (File.Exists(target))
            {
                using var existing = File.OpenRead(target);
                if (Sha256(existing) == file.Sha256)
                    continue;
            }

            using (var source = OpenVerified(Path.Combine(sourceDirectory, file.FileName), file.Sha256))
            {
                var tmp = target + ".tmp";
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    source.CopyTo(output);
                File.Move(tmp, target, overwrite: true);
            }
        }
    }

    /// <summary>Проверка всех наборов правил в рабочем каталоге перед запуском ядра.</summary>
    public static void VerifyGeo(GeoLock geo, string directory)
    {
        ArgumentNullException.ThrowIfNull(geo);
        foreach (var file in geo.Files)
        {
            using var _ = OpenVerified(Path.Combine(directory, file.FileName), file.Sha256);
        }
    }
}
