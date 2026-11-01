using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Tropa.Core.Cores;

namespace Tropa.Infrastructure.Cores;

/// <summary>
/// Выпуски ядер прямо с GitHub (ADR-058): список версий, хэш архива из ответа API (и .dgst у Xray),
/// скачивание и извлечение exe. Ходит только на адреса из <see cref="CoreReleases.Repos"/>.
/// Этим же кодом служба сама перепроверяет архив, который скачал интерфейс.
/// </summary>
public sealed class CoreReleaseClient(HttpClient http)
{
    private const int MaxApiBytes = 2 * 1024 * 1024;
    private const int MaxDgstBytes = 16 * 1024;
    private const long MaxArchiveBytes = 150L * 1024 * 1024;

    /// <summary>Последние выпуски ядра, у которых есть архив для Windows с хэшем.</summary>
    public async Task<IReadOnlyList<CoreReleases.Release>> ListAsync(string core, CancellationToken ct)
    {
        var repo = CoreReleases.For(core);
        var json = await GetLimitedAsync(new Uri(repo.ApiReleases), MaxApiBytes, api: true, ct).ConfigureAwait(false);
        return [.. CoreReleases.ParseReleases(repo, json).Where(r => r.ArchiveSha256 is not null)];
    }

    /// <summary>
    /// Хэш архива выпуска по данным GitHub. У Xray он должен совпасть ещё и с файлом .dgst из того же выпуска.
    /// </summary>
    public async Task<CoreReleases.Release> GetReleaseAsync(string core, string version, CancellationToken ct)
    {
        var repo = CoreReleases.For(core);
        var json = await GetLimitedAsync(new Uri(repo.ApiTag(CoreReleases.RequireVersion(version))), MaxApiBytes, api: true, ct).ConfigureAwait(false);
        var release = CoreReleases.ParseReleases(repo, json).FirstOrDefault(r => r.Version == version)
            ?? throw new IntegrityException($"В выпуске {core} {version} нет архива для Windows.");
        if (release.ArchiveSha256 is null)
            throw new IntegrityException($"GitHub не сообщил хэш архива {core} {version}. Такую версию установить нельзя.");
        if (repo.HasDgst)
        {
            if (release.DgstUrl is null)
                throw new IntegrityException($"В выпуске {core} {version} нет файла с хэшем (.dgst). Установка отменена.");
            var dgst = await GetLimitedAsync(new Uri(release.DgstUrl), MaxDgstBytes, api: false, ct).ConfigureAwait(false);
            if (CoreReleases.ParseDgst(System.Text.Encoding.UTF8.GetString(dgst)) != release.ArchiveSha256)
                throw new IntegrityException($"Хэш архива {core} {version} в .dgst не совпадает с GitHub. Установка отменена.");
        }

        return release;
    }

    /// <summary>Скачивает архив выпуска в <paramref name="directory"/> под его настоящим именем и проверяет хэш.</summary>
    public async Task<string> DownloadArchiveAsync(string core, string version, string archiveSha256, string directory, CancellationToken ct)
    {
        var repo = CoreReleases.For(core);
        Directory.CreateDirectory(directory);
        var data = await GetLimitedAsync(new Uri(repo.DownloadUrl(CoreReleases.RequireVersion(version))), MaxArchiveBytes, api: false, ct).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(data)) != archiveSha256)
            throw new IntegrityException($"Архив {core} {version} не совпадает с хэшем. Установка отменена.");
        var path = Path.Combine(directory, repo.AssetName(version));
        await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>
    /// Извлекает exe ядра из проверенного архива в <paramref name="targetDirectory"/>. Хэш архива сверяется
    /// ещё раз (файл мог подменить кто-то между проверками); берётся одна запись с заранее известным путём.
    /// Возвращает запись lock-файла с хэшем exe.
    /// </summary>
    public static CoreEntry ExtractExe(string core, string version, string archivePath, string archiveSha256, string targetDirectory)
    {
        var repo = CoreReleases.For(core);
        CoreReleases.RequireVersion(version);
        if (new FileInfo(archivePath).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IntegrityException("Архив ядра — ссылка на другой файл. Установка отменена.");
        using var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (archive.Length > MaxArchiveBytes || PinnedFiles.Sha256(archive) != archiveSha256)
            throw new IntegrityException($"Архив {core} {version} не совпадает с хэшем. Установка отменена.");
        archive.Position = 0;
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        var entry = zip.GetEntry(repo.ExeEntry(version)) ?? throw new IntegrityException($"В архиве {core} {version} нет {repo.ExeEntry(version)}.");
        if (entry.Length > MaxArchiveBytes)
            throw new IntegrityException($"Файл {repo.ExeTarget} в архиве слишком большой.");
        Directory.CreateDirectory(targetDirectory);
        var target = Path.Combine(targetDirectory, repo.ExeTarget);
        using (var input = entry.Open())
        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            input.CopyTo(output);
        using var check = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
        return CoreReleases.Entry(repo, version, archiveSha256, PinnedFiles.Sha256(check));
    }

    private async Task<byte[]> GetLimitedAsync(Uri url, long limit, bool api, CancellationToken ct)
    {
        if (url.Scheme != Uri.UriSchemeHttps || url.Host is not ("api.github.com" or "github.com"))
            throw new IntegrityException("Ядра с GitHub скачиваются только с github.com по https.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (api)
        {
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new HttpRequestException("На GitHub нет такого выпуска.");
        if (response.StatusCode == HttpStatusCode.Forbidden && api)
            throw new HttpRequestException("GitHub временно ограничил запросы с этого адреса. Попробуйте через час.");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException($"GitHub ответил {(int)response.StatusCode}.");
        if (response.Content.Headers.ContentLength > limit)
            throw new IntegrityException("Ответ GitHub слишком большой.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit)
                throw new IntegrityException("Ответ GitHub слишком большой.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
