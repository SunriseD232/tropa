using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tropa.Core.Cores;

/// <summary>
/// Официальные выпуски ядер на GitHub (ADR-058): откуда их можно брать в обход манифеста владельца.
/// Репозитории, имена архивов и путь к exe внутри архива зашиты здесь — ни интерфейс, ни сервер
/// не могут подсунуть другой источник. Хэш архива берётся из поля digest выпуска GitHub
/// (для Xray — ещё и из файла .dgst проекта).
/// </summary>
public static partial class CoreReleases
{
    /// <param name="PrereleaseFlagMeansPreview">
    /// Считать ли флаг pre-release на GitHub признаком preview. Xray ставит его почти всем выпускам
    /// (в том числе закреплённой у нас 26.9.30), поэтому у него preview — только версия с суффиксом.
    /// </param>
    public sealed record Repo(string Core, string Owner, string Name, string License, bool HasDgst, bool PrereleaseFlagMeansPreview)
    {
        public string Source => $"https://github.com/{Owner}/{Name}";

        public string ApiReleases => $"https://api.github.com/repos/{Owner}/{Name}/releases?per_page=30";

        public string ApiTag(string version) => $"https://api.github.com/repos/{Owner}/{Name}/releases/tags/v{version}";

        public string AssetName(string version) => Core == "sing-box" ? $"sing-box-{version}-windows-amd64.zip" : "Xray-windows-64.zip";

        public string ExeEntry(string version) => Core == "sing-box" ? $"sing-box-{version}-windows-amd64/sing-box.exe" : "xray.exe";

        public string ExeTarget => Core == "sing-box" ? "sing-box.exe" : "xray.exe";

        public string DownloadUrl(string version) => $"{Source}/releases/download/v{version}/{AssetName(version)}";
    }

    public static readonly IReadOnlyList<Repo> Repos =
    [
        new("sing-box", "SagerNet", "sing-box", "GPL-3.0-or-later", HasDgst: false, PrereleaseFlagMeansPreview: true),
        new("xray", "XTLS", "Xray-core", "MPL-2.0", HasDgst: true, PrereleaseFlagMeansPreview: false),
    ];

    public static Repo For(string core) =>
        Repos.FirstOrDefault(r => r.Core == core) ?? throw new InvalidDataException($"Ядро «{core}» не поддерживается.");

    /// <summary>Номер версии без «v»: 1.14.2, 1.15.0-beta.3, 26.9.30. Ничего, что могло бы изменить путь или адрес.</summary>
    [GeneratedRegex(@"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,6}(-[0-9A-Za-z]{1,20}(\.[0-9A-Za-z]{1,20}){0,3})?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsValidVersion(string? version) => version is not null && VersionPattern().IsMatch(version);

    public static string RequireVersion(string? version) =>
        IsValidVersion(version) ? version! : throw new InvalidDataException("Неверный номер версии ядра.");

    /// <summary>Выпуск на GitHub: версия, preview ли он и хэш архива для Windows (null — архива нет или у него нет digest).</summary>
    public sealed record Release(string Version, bool Preview, DateTimeOffset? Published, string? ArchiveSha256, string? DgstUrl);

    /// <summary>Ответ /releases (массив) или /releases/tags/… (объект). Выпуски без архива для Windows и черновики пропускаются.</summary>
    public static IReadOnlyList<Release> ParseReleases(Repo repo, ReadOnlySpan<byte> json)
    {
        ArgumentNullException.ThrowIfNull(repo);
        using var doc = Parse(json);
        var items = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
        var result = new List<Release>();
        foreach (var r in items)
        {
            if (r.ValueKind != JsonValueKind.Object || Bool(r, "draft"))
                continue;
            var tag = Str(r, "tag_name");
            if (tag is null || !tag.StartsWith('v') || !IsValidVersion(tag[1..]))
                continue;
            var version = tag[1..];
            string? sha = null, dgst = null;
            var found = false;
            if (r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = Str(a, "name");
                    if (name == repo.AssetName(version))
                    {
                        found = true;
                        var digest = Str(a, "digest");
                        sha = digest is { Length: 71 } && digest.StartsWith("sha256:", StringComparison.Ordinal) && IsSha256(digest[7..]) ? digest[7..] : null;
                    }
                    else if (name == repo.AssetName(version) + ".dgst")
                    {
                        dgst = Str(a, "browser_download_url");
                    }
                }
            }

            if (!found)
                continue;
            if (dgst is not null && !dgst.StartsWith(repo.Source + "/releases/download/", StringComparison.Ordinal))
                dgst = null;
            result.Add(new Release(version, (repo.PrereleaseFlagMeansPreview && Bool(r, "prerelease")) || version.Contains('-', StringComparison.Ordinal),
                DateTimeOffset.TryParse(Str(r, "published_at"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) ? at : null,
                sha, dgst));
        }

        return result;
    }

    /// <summary>Строка «SHA2-256= …» из файла .dgst Xray. null — строки нет.</summary>
    public static string? ParseDgst(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var m = DgstLine().Match(text);
        return m.Success && IsSha256(m.Groups[1].Value.ToLowerInvariant()) ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"(?im)^SHA2-256=\s*([0-9a-f]{64})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DgstLine();

    /// <summary>Запись lock-файла для выпуска с GitHub: те же поля, что у закреплённых ядер.</summary>
    public static CoreEntry Entry(Repo repo, string version, string archiveSha256, string exeSha256)
    {
        ArgumentNullException.ThrowIfNull(repo);
        var entry = new CoreEntry(repo.Core, RequireVersion(version), repo.Source, repo.License, repo.DownloadUrl(version), archiveSha256,
            [new CoreFile(repo.ExeEntry(version), repo.ExeTarget, exeSha256)]);
        entry.Validate();
        return entry;
    }

    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonDocument Parse(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("GitHub прислал неверный ответ о выпусках.", ex);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
