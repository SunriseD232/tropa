using System.Diagnostics;
using System.Text.Json;

namespace Tropa.Integration.Tests;

/// <summary>
/// Каждый эталонный конфиг из Tropa.Core.Tests/Generation/Golden проходит «sing-box check»
/// закреплённой версии ядра. Так схема генератора сверяется с настоящим ядром, а не с памятью.
/// </summary>
public sealed class SingBoxCheckTests
{
    private const string GoldenGeoDir = @"C:\tropa-test-geo";

    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        foreach (var f in Directory.EnumerateFiles(GoldenDir(), "*.singbox.json").Order(StringComparer.Ordinal))
            data.Add(Path.GetFileName(f));
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public async Task Golden_config_passes_sing_box_check(string file)
    {
        var ct = TestContext.Current.CancellationToken;
        var exe = Path.Combine(RepoRoot(), "cores", "sing-box.exe");
        if (!File.Exists(exe))
            Assert.Skip("sing-box.exe не найден: запустите tools/fetch-cores.ps1");

        var work = Directory.CreateTempSubdirectory("tropa-check-");
        try
        {
            var json = await File.ReadAllTextAsync(Path.Combine(GoldenDir(), file), ct);
            var geoDir = Directory.CreateDirectory(Path.Combine(work.FullName, "geo")).FullName;

            // Компилируем пустые наборы правил для всех тегов, на которые ссылается конфиг.
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.GetProperty("route").TryGetProperty("rule_set", out var sets))
                {
                    foreach (var set in sets.EnumerateArray())
                    {
                        if (set.GetProperty("type").GetString() != "local")
                            continue;
                        var tag = set.GetProperty("tag").GetString()!;
                        await CompileEmptyRuleSet(exe, work.FullName, geoDir, tag, ct);
                    }
                }
            }

            var escapedGolden = JsonSerializer.Serialize(GoldenGeoDir)[1..^1];
            var escapedTemp = JsonSerializer.Serialize(geoDir)[1..^1];
            var configPath = Path.Combine(work.FullName, "config.json");
            await File.WriteAllTextAsync(configPath, json.Replace(escapedGolden, escapedTemp, StringComparison.Ordinal), ct);

            var (code, output) = await Run(exe, ["check", "-c", configPath], ct);
            Assert.True(code == 0, $"sing-box check не прошёл для {file}:\n{output}");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    private static async Task CompileEmptyRuleSet(string exe, string work, string geoDir, string tag, CancellationToken ct)
    {
        var source = Path.Combine(work, tag + ".json");
        await File.WriteAllTextAsync(source, """{ "version": 3, "rules": [ { "domain_suffix": ["example.invalid"] } ] }""", ct);
        var (code, output) = await Run(exe, ["rule-set", "compile", "--output", Path.Combine(geoDir, tag + ".srs"), source], ct);
        Assert.True(code == 0, $"Не удалось скомпилировать набор правил {tag}:\n{output}");
    }

    private static async Task<(int Code, string Output)> Run(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static string GoldenDir() => Path.Combine(RepoRoot(), "tests", "Tropa.Core.Tests", "Generation", "Golden");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Не найден корень репозитория (Tropa.slnx).");
    }
}
