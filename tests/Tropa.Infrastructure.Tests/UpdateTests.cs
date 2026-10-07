using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tropa.Core.Updates;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Infrastructure.Updates;

namespace Tropa.Infrastructure.Tests;

/// <summary>
/// Обновления без сети: свой ключ подписи, свои «ядра» и наборы правил во временных каталогах,
/// подставной HTTP-обработчик вместо GitHub.
/// </summary>
public sealed class UpdateTests : IDisposable
{
    private const string ManifestUrl = "https://updates.example/manifest.json";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-upd-");
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly UpdateConfig _config;
    private readonly CoreLocations _bundled;

    public UpdateTests()
    {
        _config = new UpdateConfig(ManifestUrl, Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo()), 0);
        var cores = Directory.CreateDirectory(Path.Combine(_dir.FullName, "bundled", "cores")).FullName;
        var geo = Directory.CreateDirectory(Path.Combine(_dir.FullName, "bundled", "geo")).FullName;
        foreach (var name in new[] { "sing-box", "xray" })
            File.WriteAllText(Path.Combine(cores, name + ".exe"), name + " v1");
        foreach (var g in PinnedFiles.Geo.Files)
            File.WriteAllText(Path.Combine(geo, g.FileName), g.Name + " v1");
        var m = Manifest(1, "v1", "v1");
        _bundled = new CoreLocations(cores, geo) { Cores = Parse(m).Cores, Geo = Parse(m).Geo };
    }

    private static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static byte[] Zip(string entry, string content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(content);
        }

        return ms.ToArray();
    }

    /// <summary>Манифест: версия ядер <paramref name="coreVersion"/> и наборов правил <paramref name="geoVersion"/>.</summary>
    private static byte[] Manifest(long sequence, string coreVersion, string geoVersion, JsonObject? app = null, int expiresDays = 90)
    {
        var cores = new JsonArray();
        foreach (var name in new[] { "sing-box", "xray" })
        {
            var content = name + " " + coreVersion;
            cores.Add(new JsonObject
            {
                ["name"] = name,
                ["version"] = coreVersion,
                ["source"] = "https://github.com/x/" + name,
                ["license"] = "MIT",
                ["url"] = $"https://github.com/x/{name}/releases/download/{coreVersion}/{name}.zip",
                ["archiveSha256"] = Convert.ToHexStringLower(SHA256.HashData(Zip(name + ".exe", content))),
                ["files"] = new JsonArray(new JsonObject { ["entry"] = name + ".exe", ["target"] = name + ".exe", ["sha256"] = Sha(content) }),
            });
        }

        var geo = new JsonArray();
        foreach (var g in PinnedFiles.Geo.Files)
        {
            geo.Add(new JsonObject
            {
                ["name"] = g.Name,
                ["url"] = $"https://raw.githubusercontent.com/x/y/{new string('a', 40)}/{g.Name}.srs",
                ["sha256"] = Sha(g.Name + " " + geoVersion),
                ["size"] = 1,
            });
        }

        var now = DateTimeOffset.UtcNow;
        var o = new JsonObject
        {
            ["schema"] = 1,
            ["sequence"] = sequence,
            ["issued"] = now.AddDays(-1).ToString("o"),
            ["expires"] = now.AddDays(expiresDays).ToString("o"),
            ["cores"] = new JsonObject { ["schema"] = 1, ["cores"] = cores },
            ["geo"] = new JsonObject { ["schema"] = 1, ["files"] = geo },
        };
        if (app is not null)
            o["app"] = app;
        return Encoding.UTF8.GetBytes(o.ToJsonString());
    }

    private static UpdateManifest Parse(byte[] m) => UpdateManifest.Parse(m, UpdateConfig.RequiredGeo);

    private byte[] Sign(byte[] m) => ManifestSignature.Sign(m, _key);

    /// <summary>Подставной «интернет»: манифест, подпись, архивы ядер и наборы правил версии v2.</summary>
    private sealed class FakeNet(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public Dictionary<string, byte[]> Files { get; } = files;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var key = Files.Keys.FirstOrDefault(k => url.EndsWith(k, StringComparison.Ordinal));
            return Task.FromResult(key is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Files[key]) });
        }
    }

    private FakeNet Net(byte[] manifest, byte[]? sig = null)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["manifest.json"] = manifest,
            ["manifest.json.sig"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(sig ?? Sign(manifest))),
            ["sing-box.zip"] = Zip("sing-box.exe", "sing-box v2"),
            ["xray.zip"] = Zip("xray.exe", "xray v2"),
        };
        foreach (var g in PinnedFiles.Geo.Files)
            files[g.Name + ".srs"] = Encoding.UTF8.GetBytes(g.Name + " v2");
        return new FakeNet(files);
    }

    [Fact]
    public async Task Check_download_install_and_load()
    {
        var ct = TestContext.Current.CancellationToken;
        var manifest = Manifest(2, "v2", "v2");
        var net = Net(manifest);
        using var http = new HttpClient(net);
        var client = new UpdateClient(http, _config);

        var check = await client.CheckAsync(_bundled, new Version(0, 1, 0), 0, DateTimeOffset.UtcNow, ct);
        Assert.True(check.NewCores);
        Assert.True(check.NewGeo);
        Assert.Null(check.NewApp);

        var work = Path.Combine(_dir.FullName, "work");
        await client.DownloadFilesAsync(check.Manifest.Manifest, _bundled, work, null, ct);
        var store = new UpdateStore(Path.Combine(_dir.FullName, "updates"), _config);
        var installed = store.Install(check.Manifest.Bytes, check.Manifest.Signature, work, _bundled);
        Assert.Equal(2, installed.Sequence);
        Assert.Equal("sing-box v2", File.ReadAllText(Path.Combine(installed.CoresDirectory, "sing-box.exe")));

        // После перезапуска программы загружается то же самое.
        var loaded = store.Load(_bundled);
        Assert.Equal(2, loaded.Sequence);
        Assert.Equal("v2", loaded.Cores.Get("xray").Version);
        using (PinnedFiles.OpenVerified(CoreProcess.ExecutablePath(loaded, "xray"), loaded.Cores.Get("xray").Files[0].Sha256))
        {
        }

        // Тот же манифест второй раз не ставится, старый — тем более.
        Assert.Throws<IntegrityException>(() => store.Install(check.Manifest.Bytes, check.Manifest.Signature, work, loaded));
    }

    [Fact]
    public void Only_changed_files_need_downloading()
    {
        // Ядра те же, что в программе, — их служба берёт из своего каталога, качать не нужно.
        var manifest = Manifest(3, "v1", "v1");
        var store = new UpdateStore(Path.Combine(_dir.FullName, "updates"), _config);
        var empty = Directory.CreateDirectory(Path.Combine(_dir.FullName, "empty")).FullName;
        var installed = store.Install(manifest, Sign(manifest), empty, _bundled);
        Assert.Equal(3, installed.Sequence);
    }

    [Fact]
    public void Forged_or_mismatched_files_are_refused()
    {
        var store = new UpdateStore(Path.Combine(_dir.FullName, "updates"), _config);
        var manifest = Manifest(2, "v2", "v1");
        var work = Directory.CreateDirectory(Path.Combine(_dir.FullName, "work")).FullName;
        File.WriteAllText(Path.Combine(work, "sing-box.exe"), "sing-box v2");
        File.WriteAllText(Path.Combine(work, "xray.exe"), "xray EVIL");
        var ex = Assert.Throws<IntegrityException>(() => store.Install(manifest, Sign(manifest), work, _bundled));
        Assert.Contains("xray.exe", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, store.Load(_bundled).Sequence);

        // Чужая подпись.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(work, "xray.exe"), "xray v2");
        Assert.Throws<IntegrityException>(() => store.Install(manifest, ManifestSignature.Sign(manifest, other), work, _bundled));
    }

    [Fact]
    public void Tampered_update_directory_is_ignored_on_load()
    {
        var manifest = Manifest(4, "v1", "v1");
        var store = new UpdateStore(Path.Combine(_dir.FullName, "updates"), _config);
        var installed = store.Install(manifest, Sign(manifest), _dir.FullName, _bundled);
        var manifestPath = Path.Combine(_dir.FullName, "updates", "4", "manifest.json");
        File.WriteAllText(manifestPath, File.ReadAllText(manifestPath).Replace("\"v1\"", "\"v9\"", StringComparison.Ordinal));
        Assert.Equal(0, store.Load(_bundled).Sequence);
        Assert.Equal(4, installed.Sequence);
    }

    [Fact]
    public void Installer_bundled_sequence_wins_over_older_updates()
    {
        var manifest = Manifest(2, "v1", "v1");
        new UpdateStore(Path.Combine(_dir.FullName, "updates"), _config).Install(manifest, Sign(manifest), _dir.FullName, _bundled);
        // Новый установщик собран с манифестом №5: старое обновление №2 больше не используется.
        var newer = new UpdateStore(Path.Combine(_dir.FullName, "updates"), _config with { BundledSequence = 5 });
        Assert.Same(_bundled, newer.Load(_bundled));
    }

    [Fact]
    public async Task Replayed_expired_and_unsigned_manifests_are_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var old = Manifest(2, "v2", "v2");
        using (var http = new HttpClient(Net(old)))
        {
            // Уже видели №7 — старый, хоть и подписанный, манифест не принимаем.
            await Assert.ThrowsAsync<IntegrityException>(() => new UpdateClient(http, _config).CheckAsync(_bundled, new Version(0, 1), 7, DateTimeOffset.UtcNow, ct));
        }

        using (var http = new HttpClient(Net(old)))
        {
            await Assert.ThrowsAsync<IntegrityException>(() => new UpdateClient(http, _config).CheckAsync(_bundled, new Version(0, 1), 0, DateTimeOffset.UtcNow.AddYears(1), ct));
        }

        using (var http = new HttpClient(Net(old, sig: new byte[64])))
        {
            await Assert.ThrowsAsync<IntegrityException>(() => new UpdateClient(http, _config).CheckAsync(_bundled, new Version(0, 1), 0, DateTimeOffset.UtcNow, ct));
        }
    }

    [Fact]
    public async Task New_app_version_is_offered_and_verified()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = Encoding.UTF8.GetBytes("installer bytes");
        var manifest = Manifest(2, "v1", "v1", new JsonObject
        {
            ["version"] = "0.2.0",
            ["url"] = "https://github.com/owner/tropa/releases/download/v0.2.0/Tropa-0.2.0-setup.exe",
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(setup)),
        });
        var net = Net(manifest);
        var files = net.Files;
        files["Tropa-0.2.0-setup.exe"] = setup;
        using var http = new HttpClient(net);
        var client = new UpdateClient(http, _config);
        var check = await client.CheckAsync(_bundled, new Version(0, 1, 0), 0, DateTimeOffset.UtcNow, ct);
        Assert.Equal(new Version(0, 2, 0), check.NewApp!.Version);
        Assert.False(check.NewCores);

        var path = await client.DownloadInstallerAsync(check.NewApp, Path.Combine(_dir.FullName, "dl"), ct);
        Assert.Equal(setup, await File.ReadAllBytesAsync(path, ct));

        files["Tropa-0.2.0-setup.exe"] = Encoding.UTF8.GetBytes("evil installer");
        await Assert.ThrowsAsync<IntegrityException>(() => client.DownloadInstallerAsync(check.NewApp, Path.Combine(_dir.FullName, "dl2"), ct));

        // Та же версия, что установлена, — не предлагается.
        Assert.Null((await client.CheckAsync(_bundled, new Version(0, 2, 0), 0, DateTimeOffset.UtcNow, ct)).NewApp);
    }

    public void Dispose()
    {
        _key.Dispose();
        _dir.Delete(recursive: true);
    }
}

public sealed class AutostartTests
{
    private sealed class FakeRunKey : IRunKey
    {
        public Dictionary<string, string> Values { get; } = [];

        public string? Read(string name) => Values.GetValueOrDefault(name);

        public void Write(string name, string? value)
        {
            if (value is null)
                Values.Remove(name);
            else
                Values[name] = value;
        }
    }

    private const string Exe = @"C:\Program Files\Tropa\Tropa.exe";

    [Fact]
    public void Sync_follows_setting()
    {
        var key = new FakeRunKey();
        var a = new Autostart(key);
        Assert.True(a.Sync(true, Exe));
        Assert.Equal($"\"{Exe}\" --autostart", key.Values["Tropa"]);
        Assert.False(a.Sync(true, Exe));
        Assert.True(a.Sync(false, Exe));
        Assert.Empty(key.Values);
    }

    [Fact]
    public void Foreign_value_with_same_name_is_left_alone()
    {
        var key = new FakeRunKey();
        key.Values["Tropa"] = @"C:\Other\tropa-clone.exe";
        Assert.False(new Autostart(key).Sync(false, Exe));
        Assert.Equal(@"C:\Other\tropa-clone.exe", key.Values["Tropa"]);
    }

    [Fact]
    public void Only_program_files_copy_counts_as_installed()
    {
        Assert.True(Autostart.IsInstalledCopy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tropa", "Tropa.exe")));
        Assert.False(Autostart.IsInstalledCopy(@"C:\Users\me\Desktop\tropa\src\Tropa.App\bin\Debug\Tropa.exe"));
    }
}
