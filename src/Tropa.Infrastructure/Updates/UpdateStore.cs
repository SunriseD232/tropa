using System.Reflection;
using System.Text.Json.Nodes;
using Tropa.Core.Updates;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Storage;

namespace Tropa.Infrastructure.Updates;

/// <summary>
/// Настройки обновлений, вшитые в сборку (tools/update.config.json): адрес манифеста, публичный
/// ключ и номер выпуска, с которым собран установщик. Пустой адрес или ключ — обновления выключены.
/// </summary>
public sealed record UpdateConfig(string ManifestUrl, string PublicKey, long BundledSequence)
{
    public static UpdateConfig Embedded { get; } = Load();

    public bool Enabled => ManifestUrl.Length > 0 && PublicKey.Length > 0;

    private static UpdateConfig Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tropa.update.config.json")
            ?? throw new InvalidOperationException("В сборке нет настроек обновлений.");
        var o = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException("Настройки обновлений повреждены.");
        var url = (string?)o["manifestUrl"] ?? "";
        if (url.Length > 0 && (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
            throw new InvalidDataException("Адрес манифеста должен быть https.");
        return new UpdateConfig(url, (string?)o["publicKey"] ?? "", (long?)o["bundledSequence"] ?? 0);
    }

    /// <summary>Имена наборов правил, без которых генератор конфигов не работает.</summary>
    public static IEnumerable<string> RequiredGeo => PinnedFiles.Geo.Files.Select(f => f.Name);
}

/// <summary>Манифест вместе с байтами и подписью — именно эти байты подписаны.</summary>
public sealed record VerifiedManifest(byte[] Bytes, byte[] Signature, UpdateManifest Manifest)
{
    /// <summary>Проверяет подпись и только потом разбирает. Любая ошибка — исключение с понятным текстом.</summary>
    public static VerifiedManifest Verify(byte[] bytes, byte[] signature, UpdateConfig config)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(config);
        if (!ManifestSignature.Verify(bytes, signature, config.PublicKey))
            throw new IntegrityException("Подпись манифеста обновлений неверна. Обновление отменено.");
        return new VerifiedManifest(bytes, signature, UpdateManifest.Parse(bytes, UpdateConfig.RequiredGeo));
    }
}

/// <summary>
/// Установленные обновления ядер и наборов правил (ADR-023): каталог &lt;root&gt;\&lt;номер выпуска&gt;\
/// с подкаталогами cores и geo и копией подписанного манифеста. Служба держит его в
/// %ProgramData%\Tropa\updates (запись только SYSTEM и администраторам), интерфейс без службы —
/// в %LOCALAPPDATA%\Tropa\updates. Подпись и хэши проверяются при загрузке и перед каждым запуском.
/// </summary>
public sealed class UpdateStore(string root, UpdateConfig config)
{
    private const string ManifestFile = "manifest.json";
    private const string SignatureFile = "manifest.json.sig";

    /// <summary>Текущие файлы: последнее установленное обновление, если оно новее установщика, иначе файлы программы.</summary>
    public CoreLocations Load(CoreLocations bundled)
    {
        ArgumentNullException.ThrowIfNull(bundled);
        if (!config.Enabled || !Directory.Exists(root))
            return bundled;
        foreach (var dir in Directory.GetDirectories(root).Select(d => (Dir: d, Seq: long.TryParse(Path.GetFileName(d), out var n) ? n : -1))
                     .Where(x => x.Seq > config.BundledSequence).OrderByDescending(x => x.Seq))
        {
            try
            {
                var bytes = File.ReadAllBytes(Path.Combine(dir.Dir, ManifestFile));
                var sig = File.ReadAllBytes(Path.Combine(dir.Dir, SignatureFile));
                var m = VerifiedManifest.Verify(bytes, sig, config).Manifest;
                if (m.Sequence != dir.Seq)
                    continue;
                return new CoreLocations(Path.Combine(dir.Dir, "cores"), Path.Combine(dir.Dir, "geo"))
                {
                    Cores = m.Cores,
                    Geo = m.Geo,
                    Sequence = m.Sequence,
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IntegrityException or InvalidDataException)
            {
                // Неполный или испорченный каталог пропускаем: файлы программы останутся рабочими.
            }
        }

        return bundled;
    }

    /// <summary>
    /// Устанавливает обновление из каталога с уже скачанными файлами. Подпись проверяется заново (тому,
    /// кто прислал файлы, не доверяем), номер выпуска должен быть больше текущего — так нельзя вернуть
    /// старые уязвимые ядра. Каждый файл сверяется с манифестом; манифест пишется последним.
    /// </summary>
    public CoreLocations Install(byte[] manifestBytes, byte[] signature, string sourceDirectory, CoreLocations current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!config.Enabled)
            throw new InvalidOperationException("Обновления в этой сборке выключены.");
        var verified = VerifiedManifest.Verify(manifestBytes, signature, config);
        var m = verified.Manifest;
        if (m.Sequence <= Math.Max(config.BundledSequence, current.Sequence))
            throw new IntegrityException($"Манифест №{m.Sequence} не новее установленного (№{Math.Max(config.BundledSequence, current.Sequence)}). Откат на старые версии запрещён.");

        Directory.CreateDirectory(root);
        var target = Path.Combine(root, m.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        var cores = Directory.CreateDirectory(Path.Combine(target, "cores")).FullName;
        var geo = Directory.CreateDirectory(Path.Combine(target, "geo")).FullName;
        try
        {
            foreach (var core in m.Cores.Cores)
            {
                foreach (var file in core.Files)
                    CopyVerified(file.Target, file.Sha256, [sourceDirectory, current.CoresDirectory], cores);
            }

            foreach (var file in m.Geo.Files)
                CopyVerified(file.FileName, file.Sha256, [sourceDirectory, current.GeoSourceDirectory], geo);

            File.WriteAllBytes(Path.Combine(target, SignatureFile), signature);
            AtomicFile.WriteAllBytes(Path.Combine(target, ManifestFile), manifestBytes);
        }
        catch
        {
            Directory.Delete(target, recursive: true);
            throw;
        }

        // Старые выпуски больше не нужны: удаляем только свои пронумерованные каталоги.
        foreach (var old in Directory.GetDirectories(root))
        {
            if (long.TryParse(Path.GetFileName(old), out var n) && n != m.Sequence)
            {
                try
                {
                    Directory.Delete(old, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Файл ядра ещё используется — удалим при следующем обновлении.
                }
            }
        }

        var loaded = Load(current);
        return loaded.Sequence == m.Sequence
            ? loaded
            : throw new IntegrityException("Обновление установлено, но не читается. Остаются прежние версии.");
    }

    /// <summary>Берёт файл из первого каталога, где он есть с нужным хэшем. Ссылки и точки повторного разбора не принимаются.</summary>
    private static void CopyVerified(string name, string sha256, string[] sources, string targetDirectory)
    {
        foreach (var dir in sources)
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path) || new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (PinnedFiles.Sha256(source) != sha256)
                continue;
            source.Position = 0;
            using var output = new FileStream(Path.Combine(targetDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(output);
            return;
        }

        throw new IntegrityException($"Файл {name} не найден или не совпадает с манифестом. Обновление отменено.");
    }
}
