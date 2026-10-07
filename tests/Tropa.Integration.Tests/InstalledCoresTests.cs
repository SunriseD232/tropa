using System.Diagnostics;
using System.Security.Cryptography;
using Tropa.Core.Cores;

namespace Tropa.Integration.Tests;

/// <summary>
/// Проверяет ядра, скачанные tools/fetch-cores.ps1: хэш совпадает с lock-файлом и ядро запускается.
/// Если ядра не скачаны, тест пропускается.
/// </summary>
public sealed class InstalledCoresTests
{
    public static TheoryData<string, string> Cores => new()
    {
        { "sing-box", "version" },
        { "xray", "version" },
    };

    [Theory]
    [MemberData(nameof(Cores))]
    public async Task Core_matches_lock_and_starts(string name, string versionArg)
    {
        var root = RepoRoot();
        var coreLock = CoresLock.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tools", "cores.lock.json"), TestContext.Current.CancellationToken));
        var entry = coreLock.Get(name);
        var exe = Path.Combine(root, "cores", entry.Files[0].Target);
        if (!File.Exists(exe))
            Assert.Skip($"{exe} не найден: запустите tools/fetch-cores.ps1");

        await using (var stream = File.OpenRead(exe))
        {
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, TestContext.Current.CancellationToken));
            Assert.Equal(entry.Files[0].Sha256, hash);
        }

        var psi = new ProcessStartInfo(exe, versionArg) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, process.ExitCode);
        Assert.Contains(entry.Version, output, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Не найден корень репозитория (Tropa.slnx).");
    }
}
