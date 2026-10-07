using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tropa.Core.Cores;

namespace Tropa.Core.Updates;

/// <summary>Новая версия Тропы: установщик и его SHA-256.</summary>
public sealed record AppRelease(Version Version, Uri Url, string Sha256, string? Notes);

/// <summary>
/// Манифест обновлений (docs/02-security.md, §3.8; ADR-023). Подписан ключом владельца, публичный
/// ключ вшит в программу. Содержит версии и хэши ядер и наборов правил (в тех же форматах, что
/// lock-файлы) и, если есть, новую версию Тропы.
/// </summary>
public sealed partial record UpdateManifest(
    long Sequence,
    DateTimeOffset Issued,
    DateTimeOffset Expires,
    AppRelease? App,
    CoresLock Cores,
    GeoLock Geo)
{
    public const int SupportedSchema = 1;

    /// <summary>Ядра, без которых Тропа не работает.</summary>
    public static readonly IReadOnlyList<string> RequiredCores = ["sing-box", "xray"];

    [GeneratedRegex(@"^https://github\.com/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+/releases/download/[A-Za-z0-9._-]+/[A-Za-z0-9._-]+\.exe$", RegexOptions.CultureInvariant)]
    private static partial Regex InstallerUrl();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    /// <summary>
    /// Разбор манифеста. Вызывать только после проверки подписи: до неё это недоверенные данные.
    /// <paramref name="requiredGeo"/> — имена наборов правил, на которые ссылается генератор конфигов:
    /// манифест без них сломал бы подключение.
    /// </summary>
    public static UpdateManifest Parse(ReadOnlySpan<byte> json, IEnumerable<string> requiredGeo)
    {
        ArgumentNullException.ThrowIfNull(requiredGeo);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Манифест обновлений повреждён.", ex);
        }

        if (root is not JsonObject o || (int?)o["schema"] != SupportedSchema)
            throw new InvalidDataException("Манифест обновлений неизвестной версии.");

        var sequence = (long?)o["sequence"] ?? 0;
        if (sequence <= 0)
            throw new InvalidDataException("В манифесте нет номера выпуска.");
        if (!DateTimeOffset.TryParse((string?)o["issued"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var issued)
            || !DateTimeOffset.TryParse((string?)o["expires"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var expires)
            || expires <= issued)
            throw new InvalidDataException("В манифесте неверные даты.");

        var cores = CoresLock.Parse(o["cores"]?.ToJsonString() ?? throw new InvalidDataException("В манифесте нет ядер."));
        foreach (var name in RequiredCores)
            _ = cores.Get(name);
        var geo = GeoLock.Parse(o["geo"]?.ToJsonString() ?? throw new InvalidDataException("В манифесте нет наборов правил."));
        foreach (var name in requiredGeo)
        {
            if (geo.Files.All(f => f.Name != name))
                throw new InvalidDataException($"В манифесте нет набора правил «{name}».");
        }

        AppRelease? app = null;
        if (o["app"] is JsonObject a)
        {
            if (!Version.TryParse((string?)a["version"], out var version))
                throw new InvalidDataException("В манифесте неверная версия Тропы.");
            var url = (string?)a["url"];
            if (url is null || !InstallerUrl().IsMatch(url))
                throw new InvalidDataException("Установщик Тропы разрешено скачивать только из выпусков на github.com.");
            var sha = (string?)a["sha256"];
            if (sha is null || !Sha256Hex().IsMatch(sha))
                throw new InvalidDataException("У установщика Тропы неверный SHA-256.");
            var notes = (string?)a["notes"];
            app = new AppRelease(version, new Uri(url), sha, notes is { Length: > 2000 } ? notes[..2000] : notes);
        }

        return new UpdateManifest(sequence, issued, expires, app, cores, geo);
    }
}

/// <summary>
/// Подпись манифеста: ECDSA P-256 + SHA-256 (ADR-023), подпись в формате IEEE P1363 (64 байта).
/// Публичный ключ — SubjectPublicKeyInfo в base64.
/// </summary>
public static class ManifestSignature
{
    public static bool Verify(ReadOnlySpan<byte> manifest, ReadOnlySpan<byte> signature, string publicKeySpkiBase64)
    {
        if (string.IsNullOrWhiteSpace(publicKeySpkiBase64) || signature.Length != 64)
            return false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpkiBase64), out _);
            if (key.KeySize != 256)
                return false;
            return key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    public static byte[] Sign(ReadOnlySpan<byte> manifest, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);
        return privateKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
