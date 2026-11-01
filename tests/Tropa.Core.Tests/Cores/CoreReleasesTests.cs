using System.Text;
using System.Text.Json.Nodes;
using Tropa.Core.Cores;
using Tropa.Core.Updates;

namespace Tropa.Core.Tests.Cores;

public sealed class CoreReleasesTests
{
    private static string Hash(char c) => new(c, 64);

    private static JsonObject Release(string tag, bool prerelease, params (string Name, string? Digest)[] assets) => new()
    {
        ["tag_name"] = tag,
        ["prerelease"] = prerelease,
        ["draft"] = false,
        ["published_at"] = "2026-10-20T10:00:00Z",
        ["assets"] = new JsonArray([.. assets.Select(a => (JsonNode)new JsonObject
        {
            ["name"] = a.Name,
            ["digest"] = a.Digest,
            ["browser_download_url"] = $"https://github.com/XTLS/Xray-core/releases/download/{tag}/{a.Name}",
        })]),
    };

    [Theory]
    [InlineData("1.14.2", true)]
    [InlineData("1.15.0-beta.3", true)]
    [InlineData("26.9.30", true)]
    [InlineData("1.15.0-rc.1", true)]
    [InlineData("v1.14.2", false)]
    [InlineData("1.14", false)]
    [InlineData("1.14.2/../x", false)]
    [InlineData("1.14.2-beta 1", false)]
    [InlineData("", false)]
    public void Version_format(string version, bool valid) => Assert.Equal(valid, CoreReleases.IsValidVersion(version));

    [Fact]
    public void Sing_box_releases_keep_only_windows_archive_with_digest()
    {
        var repo = CoreReleases.For("sing-box");
        var json = new JsonArray(
            Release("v1.15.0-beta.3", true, ("sing-box-1.15.0-beta.3-windows-amd64.zip", "sha256:" + Hash('a'))),
            Release("v1.14.2", false, ("sing-box-1.14.2-windows-amd64.zip", "sha256:" + Hash('b')), ("sing-box-1.14.2-linux-amd64.tar.gz", "sha256:" + Hash('c'))),
            Release("v1.14.1", false, ("sing-box-1.14.1-linux-amd64.tar.gz", "sha256:" + Hash('d'))),
            Release("v1.14.0", false, ("sing-box-1.14.0-windows-amd64.zip", null)),
            Release("nightly", false, ("sing-box-nightly-windows-amd64.zip", "sha256:" + Hash('e'))));

        var list = CoreReleases.ParseReleases(repo, Encoding.UTF8.GetBytes(json.ToJsonString()));

        Assert.Equal(["1.15.0-beta.3", "1.14.2", "1.14.0"], list.Select(r => r.Version));
        Assert.True(list[0].Preview);
        Assert.False(list[1].Preview);
        Assert.Equal(Hash('b'), list[1].ArchiveSha256);
        Assert.Null(list[2].ArchiveSha256); // без digest — установить нельзя
    }

    [Fact]
    public void Xray_dgst_must_come_from_same_repository()
    {
        var repo = CoreReleases.For("xray");
        var json = Release("v26.10.5", false, ("Xray-windows-64.zip", "sha256:" + Hash('a')), ("Xray-windows-64.zip.dgst", null));
        var r = Assert.Single(CoreReleases.ParseReleases(repo, Encoding.UTF8.GetBytes(json.ToJsonString())));
        Assert.StartsWith("https://github.com/XTLS/Xray-core/releases/download/", r.DgstUrl, StringComparison.Ordinal);

        json["assets"]![1]!["browser_download_url"] = "https://evil.example/Xray-windows-64.zip.dgst";
        Assert.Null(Assert.Single(CoreReleases.ParseReleases(repo, Encoding.UTF8.GetBytes(json.ToJsonString()))).DgstUrl);
    }

    [Fact]
    public void Xray_prerelease_flag_alone_is_not_preview()
    {
        // Xray помечает pre-release почти все выпуски (и закреплённую 26.9.30) — это не beta.
        var json = Release("v26.10.31", true, ("Xray-windows-64.zip", "sha256:" + Hash('a')));
        Assert.False(Assert.Single(CoreReleases.ParseReleases(CoreReleases.For("xray"), Encoding.UTF8.GetBytes(json.ToJsonString()))).Preview);
    }

    [Fact]
    public void Dgst_line_is_parsed()
    {
        var text = "MD5= 0123\nSHA1= abcd\nSHA2-256= " + Hash('f') + "\nSHA2-512= 99\n";
        Assert.Equal(Hash('f'), CoreReleases.ParseDgst(text));
        Assert.Null(CoreReleases.ParseDgst("SHA2-256= zz"));
    }

    [Fact]
    public void Entry_points_to_official_repository()
    {
        var e = CoreReleases.Entry(CoreReleases.For("sing-box"), "1.15.0-beta.3", Hash('a'), Hash('b'));
        Assert.Equal("https://github.com/SagerNet/sing-box/releases/download/v1.15.0-beta.3/sing-box-1.15.0-beta.3-windows-amd64.zip", e.Url);
        Assert.Equal("sing-box-1.15.0-beta.3-windows-amd64/sing-box.exe", e.Files[0].Entry);
        Assert.Equal("sing-box.exe", e.Files[0].Target);
        Assert.Throws<InvalidDataException>(() => CoreReleases.For("evil"));
    }

    [Fact]
    public void Manifest_extra_cores_are_parsed_and_checked()
    {
        var o = Updates.UpdateManifestTests.Valid();
        var extra = (JsonObject)o["cores"]!["cores"]![0]!.DeepClone();
        extra["version"] = "1.15.0-beta.3";
        extra["channel"] = "preview";
        o["extraCores"] = new JsonArray(extra);

        var m = UpdateManifest.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()), ["geoip-ru"]);
        var x = Assert.Single(m.ExtraCores);
        Assert.Equal("sing-box", x.Entry.Name);
        Assert.True(x.Preview);

        // Другое имя exe, чем у ядра выпуска, — отказ: служба искала бы не тот файл.
        extra["files"]![0]!["target"] = "other.exe";
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()), ["geoip-ru"]));
        extra["files"]![0]!["target"] = "sing-box.exe";
        extra["url"] = "https://evil.example/x.zip";
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()), ["geoip-ru"]));
        extra["url"] = "https://github.com/x/y/releases/download/v1/sing-box.zip";
        extra["channel"] = "nightly";
        Assert.Throws<InvalidDataException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()), ["geoip-ru"]));
    }

    [Fact]
    public void Manifest_without_extra_cores_is_still_valid()
    {
        var m = UpdateManifest.Parse(Encoding.UTF8.GetBytes(Updates.UpdateManifestTests.Valid().ToJsonString()), ["geoip-ru"]);
        Assert.Empty(m.ExtraCores);
    }
}
