using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Tropa.Core.Cores;
using Tropa.Core.Updates;
using Tropa.Infrastructure.Cores;

namespace Tropa.Infrastructure.Updates;

/// <summary>Что нашлось в свежем манифесте.</summary>
public sealed record UpdateCheck(VerifiedManifest Manifest, AppRelease? NewApp, bool NewCores, bool NewGeo)
{
    public bool Any => NewApp is not null || NewCores || NewGeo;
}

/// <summary>
/// Загрузка манифеста и файлов обновления (docs/02-security.md, §3.8). Всё скачанное сверяется
/// с подписанным манифестом до использования. Скачивает интерфейс с правами пользователя;
/// служба получает только готовые файлы и проверяет их сама (ADR-023).
/// </summary>
public sealed class UpdateClient(HttpClient http, UpdateConfig config)
{
    private const int MaxManifestBytes = 256 * 1024;
    private const long MaxArchiveBytes = 150L * 1024 * 1024;
    private const long MaxGeoBytes = 20L * 1024 * 1024;

    /// <summary>Скачивает и проверяет манифест. <paramref name="lastSeen"/> — самый новый номер, который уже видели.</summary>
    public async Task<UpdateCheck> CheckAsync(CoreLocations current, Version appVersion, long lastSeen, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!config.Enabled)
            throw new InvalidOperationException("В этой сборке Тропы обновления не настроены.");
        var bytes = await GetLimitedAsync(new Uri(config.ManifestUrl), MaxManifestBytes, ct).ConfigureAwait(false);
        var sigText = System.Text.Encoding.ASCII.GetString(await GetLimitedAsync(new Uri(config.ManifestUrl + ".sig"), 1024, ct).ConfigureAwait(false));
        byte[] sig;
        try
        {
            sig = Convert.FromBase64String(sigText.Trim());
        }
        catch (FormatException)
        {
            throw new IntegrityException("Подпись манифеста обновлений повреждена.");
        }

        var verified = VerifiedManifest.Verify(bytes, sig, config);
        var m = verified.Manifest;
        // Защита от подсовывания старого (но честно подписанного) манифеста и от «заморозки».
        if (m.Sequence < Math.Max(lastSeen, Math.Max(config.BundledSequence, current.Sequence)))
            throw new IntegrityException("Сервер обновлений вернул устаревший манифест. Возможно, кто-то подменяет ответ.");
        if (m.Expires < now)
            throw new IntegrityException("Манифест обновлений просрочен. Возможно, кто-то подменяет ответ или выпуск давно не обновлялся.");

        var newCores = m.Sequence > current.Sequence && m.Cores.Cores.Any(c => !SameCore(c, current.Cores));
        var newGeo = m.Sequence > current.Sequence && m.Geo.Files.Any(f => current.Geo.Files.All(g => g.Sha256 != f.Sha256 || g.Name != f.Name));
        var newApp = m.App is { } app && app.Version > appVersion ? app : null;
        return new UpdateCheck(verified, newApp, newCores, newGeo);
    }

    private static bool SameCore(CoreEntry c, CoresLock current)
    {
        var old = current.Cores.FirstOrDefault(x => x.Name == c.Name);
        return old is not null && old.Files.Count == c.Files.Count && old.Files.Zip(c.Files).All(p => p.First.Sha256 == p.Second.Sha256 && p.First.Target == p.Second.Target);
    }

    /// <summary>
    /// Скачивает в <paramref name="workDirectory"/> только то, чего нет в текущих файлах: архивы ядер
    /// (проверка хэша архива, затем извлечённого файла) и наборы правил.
    /// </summary>
    public async Task DownloadFilesAsync(UpdateManifest m, CoreLocations current, string workDirectory, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(current);
        Directory.CreateDirectory(workDirectory);
        foreach (var core in m.Cores.Cores)
        {
            if (core.Files.All(f => HasFile(current.CoresDirectory, f.Target, f.Sha256)))
                continue;
            progress?.Report($"Скачиваю {core.Name} {core.Version}…");
            var archive = await GetLimitedAsync(new Uri(core.Url), MaxArchiveBytes, ct).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(archive)) != core.ArchiveSha256)
                throw new IntegrityException($"Архив {core.Name} {core.Version} не совпадает с манифестом. Обновление отменено.");
            using var zip = new ZipArchive(new MemoryStream(archive), ZipArchiveMode.Read);
            foreach (var file in core.Files)
            {
                var entry = zip.GetEntry(file.Entry) ?? throw new IntegrityException($"В архиве {core.Name} нет файла {file.Entry}.");
                var target = Path.Combine(workDirectory, file.Target);
                await using (var input = await entry.OpenAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                await using var check = File.OpenRead(target);
                if (PinnedFiles.Sha256(check) != file.Sha256)
                    throw new IntegrityException($"Файл {file.Target} из архива не совпадает с манифестом. Обновление отменено.");
            }
        }

        foreach (var file in m.Geo.Files)
        {
            if (HasFile(current.GeoSourceDirectory, file.FileName, file.Sha256))
                continue;
            progress?.Report($"Скачиваю набор правил {file.Name}…");
            var data = await GetLimitedAsync(new Uri(file.Url), MaxGeoBytes, ct).ConfigureAwait(false);
            if (Convert.ToHexStringLower(SHA256.HashData(data)) != file.Sha256)
                throw new IntegrityException($"Набор правил {file.Name} не совпадает с манифестом. Обновление отменено.");
            await File.WriteAllBytesAsync(Path.Combine(workDirectory, file.FileName), data, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Скачивает установщик новой версии и проверяет его SHA-256. Возвращает путь к файлу.</summary>
    public async Task<string> DownloadInstallerAsync(AppRelease app, string directory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        Directory.CreateDirectory(directory);
        var data = await GetLimitedAsync(app.Url, MaxArchiveBytes, ct).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(data)) != app.Sha256)
            throw new IntegrityException("Установщик не совпадает с манифестом. Обновление отменено.");
        var path = Path.Combine(directory, $"Tropa-{app.Version}-setup.exe");
        await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
        return path;
    }

    private static bool HasFile(string directory, string name, string sha256)
    {
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
            return false;
        using var s = File.OpenRead(path);
        return PinnedFiles.Sha256(s) == sha256;
    }

    private async Task<byte[]> GetLimitedAsync(Uri url, long limit, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new IntegrityException("Обновления скачиваются только по https.");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"Сервер обновлений ответил {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > limit)
            throw new IntegrityException("Файл обновления слишком большой.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new IntegrityException("Файл обновления слишком большой.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
