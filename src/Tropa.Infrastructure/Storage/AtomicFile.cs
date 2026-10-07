using System.Text;

namespace Tropa.Infrastructure.Storage;

/// <summary>
/// Атомарная запись (docs/02-security.md, §3.7): данные пишутся во временный файл рядом,
/// сбрасываются на диск и только потом заменяют целевой. При сбое питания остаётся либо
/// старая, либо новая версия, но не обрывок.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content));

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? throw new ArgumentException("Нет каталога.", nameof(path));
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }

            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
        }
    }
}
