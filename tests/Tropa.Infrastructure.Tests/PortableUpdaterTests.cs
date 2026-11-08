using System.IO.Compression;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.Updates;

namespace Tropa.Infrastructure.Tests;

/// <summary>Портативная Тропа (ADR-063): распаковка архива обновления и замена файлов без папки data.</summary>
public sealed class PortableUpdaterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tropa-portable-" + Guid.NewGuid().ToString("N"));

    public PortableUpdaterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Zip(params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write(text);
        }

        return path;
    }

    [Fact]
    public void Archive_with_tropa_folder_is_staged()
    {
        var zip = Zip(("Tropa/Tropa.exe", "new"), ("Tropa/" + PortableMode.MarkerFile, "m"), ("Tropa/cores/sing-box.exe", "sb"));
        var root = PortableUpdater.Stage(zip, Path.Combine(_dir, "stage"));
        Assert.Equal("Tropa", Path.GetFileName(root));
        Assert.True(File.Exists(Path.Combine(root, "cores", "sing-box.exe")));
    }

    [Theory]
    [InlineData("../evil.exe")]
    [InlineData("Tropa/../../evil.exe")]
    public void Entries_outside_the_folder_are_rejected(string name)
    {
        var zip = Zip(("Tropa/Tropa.exe", "new"), ("Tropa/" + PortableMode.MarkerFile, "m"), (name, "x"));
        Assert.Throws<IntegrityException>(() => PortableUpdater.Stage(zip, Path.Combine(_dir, "stage")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.exe")));
    }

    [Fact]
    public void Archive_without_portable_marker_is_rejected()
    {
        var zip = Zip(("Tropa/Tropa.exe", "new"));
        Assert.Throws<IntegrityException>(() => PortableUpdater.Stage(zip, Path.Combine(_dir, "stage")));
    }

    [Fact]
    public void Apply_overwrites_program_but_keeps_data()
    {
        var target = Path.Combine(_dir, "Tropa");
        Directory.CreateDirectory(Path.Combine(target, "data"));
        File.WriteAllText(Path.Combine(target, PortableMode.MarkerFile), "m");
        File.WriteAllText(Path.Combine(target, "Tropa.exe"), "old");
        File.WriteAllText(Path.Combine(target, "data", "settings.json"), "mine");
        File.WriteAllText(Path.Combine(target, "old-only.dll"), "keep");

        var source = Path.Combine(_dir, "new");
        Directory.CreateDirectory(Path.Combine(source, "data"));
        File.WriteAllText(Path.Combine(source, PortableMode.MarkerFile), "m");
        File.WriteAllText(Path.Combine(source, "Tropa.exe"), "new");
        File.WriteAllText(Path.Combine(source, "data", "settings.json"), "theirs");

        PortableUpdater.Apply(source, target, TimeSpan.FromSeconds(1));

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Tropa.exe")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "data", "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "old-only.dll")));
    }

    [Fact]
    public void Apply_refuses_folder_that_is_not_portable_tropa()
    {
        var target = Path.Combine(_dir, "Other");
        Directory.CreateDirectory(target);
        var source = Path.Combine(_dir, "new");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, PortableMode.MarkerFile), "m");
        File.WriteAllText(Path.Combine(source, "Tropa.exe"), "new");
        Assert.Throws<InvalidOperationException>(() => PortableUpdater.Apply(source, target, TimeSpan.FromSeconds(1)));
        Assert.False(File.Exists(Path.Combine(target, "Tropa.exe")));
    }

    [Fact]
    public void Portable_paths_live_next_to_the_program()
    {
        var paths = PortableMode.Paths(_dir);
        Assert.Equal(Path.Combine(_dir, "data"), paths.Roaming);
        Assert.StartsWith(Path.Combine(_dir, "data"), paths.Local, StringComparison.Ordinal);
        Assert.Null(PortableMode.Detect(_dir));
        File.WriteAllText(Path.Combine(_dir, PortableMode.MarkerFile), "m");
        Assert.NotNull(PortableMode.Detect(_dir));
    }
}
