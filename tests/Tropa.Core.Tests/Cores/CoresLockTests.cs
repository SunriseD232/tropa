using Tropa.Core.Cores;

namespace Tropa.Core.Tests.Cores;

public sealed class CoresLockTests
{
    private const string Sha = "7bbef1dea9189ee12799ae834ea4b4658355da25c47a21ad8804904c0ccd9410";

    private static string Lock(
        string url = "https://github.com/SagerNet/sing-box/releases/download/v1.14.2/a.zip",
        string target = "sing-box.exe",
        string fileSha = Sha,
        int schema = 1) => $$"""
        {
          "schema": {{schema}},
          "cores": [
            {
              "name": "sing-box", "version": "1.14.2",
              "source": "https://github.com/SagerNet/sing-box", "license": "GPL-3.0-or-later",
              "url": "{{url}}", "archiveSha256": "{{Sha}}",
              "files": [ { "entry": "x/sing-box.exe", "target": "{{target}}", "sha256": "{{fileSha}}" } ]
            }
          ]
        }
        """;

    [Fact]
    public void Parses_valid_lock()
    {
        var parsed = CoresLock.Parse(Lock());
        var core = parsed.Get("sing-box");
        Assert.Equal("1.14.2", core.Version);
        Assert.Equal("sing-box.exe", core.Files[0].Target);
    }

    [Fact]
    public void Repository_lock_file_is_valid()
    {
        var path = Path.Combine(RepoRoot(), "tools", "cores.lock.json");
        var parsed = CoresLock.Parse(File.ReadAllText(path));
        Assert.Contains(parsed.Cores, c => c.Name == "sing-box");
        Assert.Contains(parsed.Cores, c => c.Name == "xray");
    }

    [Theory]
    [InlineData("http://github.com/a.zip")]
    [InlineData("https://evil.example/a.zip")]
    [InlineData("https://github.com.evil.example/a.zip")]
    [InlineData("file:///C:/a.zip")]
    public void Rejects_non_github_https_urls(string url) =>
        Assert.Throws<InvalidDataException>(() => CoresLock.Parse(Lock(url: url)));

    [Theory]
    [InlineData("..\\evil.exe")]
    [InlineData("../evil.exe")]
    [InlineData("C:evil.exe")]
    [InlineData("sub/evil.exe")]
    [InlineData("CON.exe")]
    [InlineData(".hidden")]
    [InlineData("evil.exe.")]
    public void Rejects_unsafe_target_names(string target) =>
        Assert.Throws<InvalidDataException>(() => CoresLock.Parse(Lock(target: target)));

    [Theory]
    [InlineData("")]
    [InlineData("7BBEF1DEA9189EE12799AE834EA4B4658355DA25C47A21AD8804904C0CCD9410")]
    [InlineData("7bbef1dea9189ee12799ae834ea4b4658355da25c47a21ad8804904c0ccd941")]
    [InlineData("zzbef1dea9189ee12799ae834ea4b4658355da25c47a21ad8804904c0ccd9410")]
    public void Rejects_malformed_hashes(string sha) =>
        Assert.Throws<InvalidDataException>(() => CoresLock.Parse(Lock(fileSha: sha)));

    [Fact]
    public void Rejects_unknown_schema() =>
        Assert.Throws<InvalidDataException>(() => CoresLock.Parse(Lock(schema: 2)));

    [Fact]
    public void Rejects_broken_json() =>
        Assert.Throws<InvalidDataException>(() => CoresLock.Parse("{ not json"));

    [Fact]
    public void Unknown_core_is_reported() =>
        Assert.Throws<KeyNotFoundException>(() => CoresLock.Parse(Lock()).Get("xray"));

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tropa.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Не найден корень репозитория (Tropa.slnx).");
    }
}
