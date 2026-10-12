using System.Diagnostics;
using System.Text.Json;
using Tropa.Core.Model;
using Tropa.Core.Routing;

namespace Tropa.Infrastructure.Cores;

/// <summary>Содержимое набора правил: записи и сколько правил другого вида пропущено.</summary>
public sealed record GeoListContent(IReadOnlyList<GeoEntry> Entries, int Skipped);

/// <summary>
/// Читает закреплённый набор .srs для просмотра (ADR-039): закреплённый sing-box разворачивает его
/// в JSON командой «rule-set decompile». И ядро, и набор проверяются по хэшу и держатся открытыми
/// на время работы ядра. Ядру при этом не передаётся ни конфиг, ни сеть — только два пути.
/// </summary>
public static class GeoListReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public static async Task<GeoListContent> ReadAsync(CoreLocations locations, string tag, string runDirectory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var file = locations.Geo.Files.FirstOrDefault(f => f.Name == tag)
            ?? throw new InvalidOperationException($"Набора «{tag}» нет среди закреплённых.");
        var core = locations.Cores.Get("sing-box").Files[0];
        var exePath = Path.Combine(locations.CoresDirectory, core.Target);
        var srsPath = Path.Combine(locations.GeoSourceDirectory, file.FileName);

        Directory.CreateDirectory(runDirectory);
        var output = Path.Combine(runDirectory, $"geo-view-{Guid.NewGuid():N}.json");
        try
        {
            using (PinnedFiles.OpenVerified(exePath, core.Sha256))
            using (PinnedFiles.OpenVerified(srsPath, file.Sha256))
            {
                var psi = new ProcessStartInfo(exePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = runDirectory,
                };
                foreach (var arg in new[] { "rule-set", "decompile", srsPath, "-o", output })
                    psi.ArgumentList.Add(arg);

                using var process = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить sing-box.");
                var stderr = process.StandardError.ReadToEndAsync(ct);
                _ = process.StandardOutput.ReadToEndAsync(ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(Timeout);
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    throw;
                }

                if (process.ExitCode != 0)
                    throw new InvalidOperationException("sing-box не смог прочитать набор: " + (await stderr.ConfigureAwait(false)).Trim());
            }

            await using var stream = File.OpenRead(output);
            return await ParseAsync(stream, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Разбор JSON из «rule-set decompile»: правила с ip_cidr и полями доменов.</summary>
    public static async Task<GeoListContent> ParseAsync(Stream json, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(json, new JsonDocumentOptions { MaxDepth = 16 }, ct).ConfigureAwait(false);
        var entries = new List<GeoEntry>();
        var seen = new HashSet<GeoEntry>();
        var skipped = 0;
        if (!doc.RootElement.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return new GeoListContent([], 0);

        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.ValueKind != JsonValueKind.Object || rule.TryGetProperty("type", out _) || rule.TryGetProperty("invert", out _))
            {
                skipped++;
                continue;
            }

            foreach (var p in rule.EnumerateObject())
            {
                GeoEntryKind? kind = p.Name switch
                {
                    "ip_cidr" => GeoEntryKind.Cidr,
                    "domain" => GeoEntryKind.Domain,
                    "domain_suffix" => GeoEntryKind.Suffix,
                    "domain_keyword" => GeoEntryKind.Keyword,
                    "domain_regex" => GeoEntryKind.Regex,
                    _ => null,
                };
                if (kind is null)
                {
                    skipped++;
                    continue;
                }

                foreach (var v in p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().ToList() : [p.Value])
                {
                    if (v.ValueKind != JsonValueKind.String || v.GetString() is not { Length: > 0 } value)
                        continue;
                    // Сети приводим к тому же виду, в каком их хранят правки, — иначе «убрать» не найдёт запись.
                    if (kind == GeoEntryKind.Cidr)
                        value = GeoEdits.Cidr(value) ?? value;
                    var entry = new GeoEntry(kind.Value, value);
                    if (seen.Add(entry))
                        entries.Add(entry);
                }
            }
        }

        return new GeoListContent(entries, skipped);
    }
}
