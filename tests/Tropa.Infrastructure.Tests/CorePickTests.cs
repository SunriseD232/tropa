using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Tropa.Core.Cores;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;

namespace Tropa.Infrastructure.Tests;

public sealed class CorePickTests : IDisposable
{
    private static readonly string[] FourVersions = ["1.15.0", "1.15.1", "1.15.2", "1.15.3"];

    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-picks-");

    public void Dispose() => _work.Delete(recursive: true);

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>«exe» версии: файл с текстом и запись lock-файла к нему.</summary>
    private (CoreEntry Entry, string Path) Exe(string core, string version)
    {
        var data = Encoding.UTF8.GetBytes($"{core} {version}");
        var dir = Directory.CreateDirectory(Path.Combine(_work.FullName, "src", core, version)).FullName;
        var repo = CoreReleases.For(core);
        var path = Path.Combine(dir, repo.ExeTarget);
        File.WriteAllBytes(path, data);
        return (CoreReleases.Entry(repo, version, new string('a', 64), Sha(data)), path);
    }

    private CoreLocations Release() => new(Path.Combine(_work.FullName, "release"), Path.Combine(_work.FullName, "geo"));

    [Fact]
    public void Install_select_rollback_and_release()
    {
        var store = new CorePickStore(Path.Combine(_work.FullName, "picks"));
        var now = DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var (a, aPath) = Exe("sing-box", "1.15.0-beta.3");
        var (b, bPath) = Exe("sing-box", "1.15.0");

        store.Install(a, CorePickSource.GitHub, preview: true, aPath, now);
        var loc = store.Apply(Release());
        Assert.Equal("1.15.0-beta.3", loc.Cores.Get("sing-box").Version);
        Assert.Equal(PinnedFiles.Cores.Get("xray").Version, loc.Cores.Get("xray").Version); // xray не тронут
        Assert.Equal(PinnedFiles.Cores.Get("sing-box").Version, loc.ReleaseCores.Get("sing-box").Version);
        Assert.StartsWith(store.Root, CoreProcess.ExecutablePath(loc, "sing-box"), StringComparison.Ordinal);
        Assert.Equal(Path.Combine(Release().CoresDirectory, "xray.exe"), CoreProcess.ExecutablePath(loc, "xray"));

        var state = store.Install(b, CorePickSource.Manifest, preview: false, bPath, now.AddMinutes(1));
        Assert.Equal("1.15.0", state.Selected["sing-box"]);
        Assert.Equal("1.15.0-beta.3", state.Previous["sing-box"]);

        // «Вернуть прежнюю» без скачивания.
        state = store.Select("sing-box", "1.15.0-beta.3");
        Assert.Equal("1.15.0-beta.3", store.Apply(Release()).Cores.Get("sing-box").Version);
        Assert.Equal("1.15.0", state.Previous["sing-box"]);

        // «Как в выпуске».
        store.Select("sing-box", null);
        var back = store.Apply(loc); // применяется и к уже заменённым путям
        Assert.Equal(PinnedFiles.Cores.Get("sing-box").Version, back.Cores.Get("sing-box").Version);
        Assert.Empty(back.PickedDirectories);
        Assert.Throws<InvalidOperationException>(() => store.Select("sing-box", "9.9.9"));
    }

    [Fact]
    public void Wrong_hash_is_refused_and_tampered_exe_is_caught_at_start()
    {
        var store = new CorePickStore(Path.Combine(_work.FullName, "picks"));
        var (a, aPath) = Exe("xray", "26.10.5");
        File.WriteAllText(aPath, "подмена");
        Assert.Throws<IntegrityException>(() => store.Install(a, CorePickSource.GitHub, false, aPath, DateTimeOffset.Now));
        Assert.Empty(store.Load().Installed);

        var (b, bPath) = Exe("xray", "26.10.6");
        store.Install(b, CorePickSource.GitHub, false, bPath, DateTimeOffset.Now);
        var exe = CoreProcess.ExecutablePath(store.Apply(Release()), "xray");
        File.WriteAllText(exe, "подмена после установки");
        // Хэш сверяется перед каждым запуском — подменённый файл ядро не запустит.
        Assert.Throws<IntegrityException>(() => PinnedFiles.OpenVerified(exe, b.Files[0].Sha256).Dispose());
    }

    [Fact]
    public void Keeps_three_versions_per_core()
    {
        var store = new CorePickStore(Path.Combine(_work.FullName, "picks"));
        var t = DateTimeOffset.Parse("2026-11-01T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (v, i) in FourVersions.Select((v, i) => (v, i)))
        {
            var (e, p) = Exe("sing-box", v);
            store.Install(e, CorePickSource.GitHub, false, p, t.AddMinutes(i));
        }

        var state = store.Load();
        Assert.Equal(CorePickStore.KeepPerCore, state.Installed.Count);
        Assert.DoesNotContain(state.Installed, x => x.Version == "1.15.0");
        Assert.False(Directory.Exists(Path.Combine(store.Root, "sing-box", "1.15.0")));
        Assert.Equal("1.15.3", state.Selected["sing-box"]);
    }

    [Fact]
    public void Broken_state_file_falls_back_to_release()
    {
        var root = Directory.CreateDirectory(Path.Combine(_work.FullName, "picks")).FullName;
        File.WriteAllText(Path.Combine(root, "picks.json"), "{ не json");
        var loc = new CorePickStore(root).Apply(Release());
        Assert.Empty(loc.PickedDirectories);
    }

    [Fact]
    public void Exe_is_extracted_from_verified_archive_only()
    {
        var repo = CoreReleases.For("sing-box");
        const string version = "1.15.0-beta.3";
        var zipPath = Path.Combine(_work.FullName, repo.AssetName(version));
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry(repo.ExeEntry(version)).Open();
            s.Write(Encoding.UTF8.GetBytes("exe"));
        }

        var sha = Sha(File.ReadAllBytes(zipPath));
        var entry = CoreReleaseClient.ExtractExe("sing-box", version, zipPath, sha, Path.Combine(_work.FullName, "out"));
        Assert.Equal(Sha(Encoding.UTF8.GetBytes("exe")), entry.Files[0].Sha256);
        Assert.Throws<IntegrityException>(() => CoreReleaseClient.ExtractExe("sing-box", version, zipPath, new string('0', 64), Path.Combine(_work.FullName, "out2")));
    }
}
