using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Tropa.Core.Updates;

namespace Tropa.Core.Tests.Updates;

public sealed class UpdateManifestTests
{
    private static readonly string[] RequiredGeo = ["geoip-ru"];

    private static string Hash(char c) => new(c, 64);

    internal static JsonObject Valid() => new()
    {
        ["schema"] = 1,
        ["sequence"] = 5,
        ["issued"] = "2026-10-01T00:00:00Z",
        ["expires"] = "2027-01-01T00:00:00Z",
        ["cores"] = new JsonObject
        {
            ["schema"] = 1,
            ["cores"] = new JsonArray(Core("sing-box", 'a'), Core("xray", 'b')),
        },
        ["geo"] = new JsonObject
        {
            ["schema"] = 1,
            ["files"] = new JsonArray(new JsonObject
            {
                ["name"] = "geoip-ru",
                ["url"] = "https://raw.githubusercontent.com/SagerNet/sing-geoip/7fe82a879ad2666526730c195b55a6d8d9147908/geoip-ru.srs",
                ["sha256"] = Hash('c'),
                ["size"] = 1,
            }),
        },
    };

    private static JsonObject Core(string name, char h) => new()
    {
        ["name"] = name,
        ["version"] = "1.0.0",
        ["source"] = "https://github.com/x/y",
        ["license"] = "MIT",
        ["url"] = $"https://github.com/x/y/releases/download/v1/{name}.zip",
        ["archiveSha256"] = Hash(h),
        ["files"] = new JsonArray(new JsonObject { ["entry"] = name + ".exe", ["target"] = name + ".exe", ["sha256"] = Hash(h) }),
    };

    private static UpdateManifest Parse(JsonObject o) => UpdateManifest.Parse(Encoding.UTF8.GetBytes(o.ToJsonString()), RequiredGeo);

    [Fact]
    public void Valid_manifest_parses()
    {
        var o = Valid();
        o["app"] = new JsonObject
        {
            ["version"] = "0.2.0",
            ["url"] = "https://github.com/owner/tropa/releases/download/v0.2.0/Tropa-0.2.0-setup.exe",
            ["sha256"] = Hash('d'),
            ["notes"] = "Исправления",
        };
        var m = Parse(o);
        Assert.Equal(5, m.Sequence);
        Assert.Equal(new Version(0, 2, 0), m.App!.Version);
        Assert.Equal("1.0.0", m.Cores.Get("xray").Version);
    }

    [Fact]
    public void Missing_core_or_rule_set_is_rejected()
    {
        var o = Valid();
        ((JsonArray)o["cores"]!["cores"]!).RemoveAt(1);
        Assert.Throws<KeyNotFoundException>(() => Parse(o));

        var g = Valid();
        g["geo"]!["files"]![0]!["name"] = "geoip-other";
        Assert.Throws<InvalidDataException>(() => Parse(g));
    }

    [Theory]
    [InlineData("http://github.com/o/t/releases/download/v1/setup.exe")]
    [InlineData("https://evil.example/o/t/releases/download/v1/setup.exe")]
    [InlineData("https://github.com/o/t/releases/download/v1/setup.msi")]
    [InlineData("https://github.com/o/t/raw/main/setup.exe")]
    public void Installer_only_from_github_releases(string url)
    {
        var o = Valid();
        o["app"] = new JsonObject { ["version"] = "1.0.0", ["url"] = url, ["sha256"] = Hash('d') };
        Assert.Throws<InvalidDataException>(() => Parse(o));
    }

    [Fact]
    public void Bad_core_url_and_dates_are_rejected()
    {
        var o = Valid();
        o["cores"]!["cores"]![0]!["url"] = "https://evil.example/sing-box.zip";
        Assert.Throws<InvalidDataException>(() => Parse(o));

        var d = Valid();
        d["expires"] = "2026-01-01T00:00:00Z";
        Assert.Throws<InvalidDataException>(() => Parse(d));

        var s = Valid();
        s["sequence"] = 0;
        Assert.Throws<InvalidDataException>(() => Parse(s));
    }

    [Fact]
    public void Signature_round_trip_and_tampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var bytes = Encoding.UTF8.GetBytes(Valid().ToJsonString());
        var sig = ManifestSignature.Sign(bytes, key);
        Assert.Equal(64, sig.Length);
        Assert.True(ManifestSignature.Verify(bytes, sig, pub));

        var tampered = (byte[])bytes.Clone();
        tampered[^3] ^= 1;
        Assert.False(ManifestSignature.Verify(tampered, sig, pub));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(ManifestSignature.Verify(bytes, sig, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo())));
        Assert.False(ManifestSignature.Verify(bytes, sig.AsSpan(0, 63), pub));
        Assert.False(ManifestSignature.Verify(bytes, sig, ""));
        Assert.False(ManifestSignature.Verify(bytes, sig, "не base64"));

        // Ключ другой кривой не принимаем.
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(ManifestSignature.Verify(bytes, sig, Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo())));
    }
}
