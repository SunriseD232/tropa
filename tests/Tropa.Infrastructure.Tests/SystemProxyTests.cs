using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.SystemIntegration;

namespace Tropa.Infrastructure.Tests;

/// <summary>Реестр в памяти: тесты не трогают настоящий прокси Windows.</summary>
internal sealed class FakeProxyStore : IProxySettingsStore
{
    public Dictionary<string, string?> Values { get; } = new()
    {
        ["ProxyEnable"] = "1",
        ["ProxyServer"] = "corp-proxy:3128",
        ["ProxyOverride"] = "*.corp",
        ["AutoConfigURL"] = "http://wpad/proxy.pac",
    };

    public Dictionary<string, string?> Read() => new(Values);

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (k, v) in values)
            Values[k] = v;
    }
}

public sealed class SystemProxyTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-journal-");
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    public void Dispose() => _dir.Delete(recursive: true);

    private string JournalPath => Path.Combine(_dir.FullName, "journal.json");

    [Fact]
    public void Apply_then_restore_returns_original_settings()
    {
        var store = new FakeProxyStore();
        var original = store.Read();
        var proxy = new SystemProxy(store, new ChangeJournal(JournalPath));

        proxy.Apply(10808, "localhost;127.*", Now);
        Assert.Equal("127.0.0.1:10808", store.Values["ProxyServer"]);
        Assert.Null(store.Values["AutoConfigURL"]);
        Assert.EndsWith("<local>", store.Values["ProxyOverride"], StringComparison.Ordinal);

        proxy.Restore();
        Assert.Equal(original, store.Values);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Second_apply_does_not_overwrite_original()
    {
        var store = new FakeProxyStore();
        var original = store.Read();
        var proxy = new SystemProxy(store, new ChangeJournal(JournalPath));
        proxy.Apply(10808, "", Now);
        proxy.Apply(20808, "", Now); // смена порта при переподключении
        proxy.Restore();
        Assert.Equal(original, store.Values);
    }

    [Fact]
    public void Crash_recovery_restores_from_journal_on_disk()
    {
        var store = new FakeProxyStore();
        var original = store.Read();
        new SystemProxy(store, new ChangeJournal(JournalPath)).Apply(10808, "", Now);
        // «Падение»: экземпляр потерян, остался только журнал на диске.
        var afterRestart = new SystemProxy(store, new ChangeJournal(JournalPath));
        Assert.True(afterRestart.IsAppliedByUs);
        afterRestart.Restore();
        Assert.Equal(original, store.Values);
    }

    [Fact]
    public void Restore_without_apply_changes_nothing()
    {
        var store = new FakeProxyStore();
        var original = store.Read();
        new SystemProxy(store, new ChangeJournal(JournalPath)).Restore();
        Assert.Equal(original, store.Values);
    }

    [Fact]
    public void Corrupted_journal_is_kept_for_diagnostics()
    {
        File.WriteAllText(JournalPath, "{ broken");
        Assert.Empty(new ChangeJournal(JournalPath).Pending());
        Assert.True(File.Exists(JournalPath + ".corrupt"));
    }
}

public sealed class PinnedFilesTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-pinned-");

    public void Dispose() => _dir.Delete(recursive: true);

    [Fact]
    public void Embedded_locks_are_valid()
    {
        Assert.Equal("1.14.2", PinnedFiles.Cores.Get("sing-box").Version);
        // Каждый набор правил, на который ссылается генератор, закреплён в lock-файле.
        var pinned = PinnedFiles.Geo.Files.Select(f => f.Name).ToHashSet();
        var used = Core.Routing.RuleSets.AlwaysDirect.Where(t => t != Core.Routing.RuleSets.RuServices)
            .Concat(Core.Routing.RuleSets.Blocked)
            .Concat([Core.Routing.RuleSets.GeositeCategoryRu, Core.Routing.RuleSets.GeoipRu, Core.Routing.RuleSets.GeositeAiNonCn]);
        Assert.All(used, tag => Assert.Contains(tag, pinned));
        // Все — из одного источника и закреплены по коммиту.
        Assert.All(PinnedFiles.Geo.Files, f => Assert.StartsWith("https://raw.githubusercontent.com/runetfreedom/russia-v2ray-rules-dat/", f.Url, StringComparison.Ordinal));
    }

    [Fact]
    public void Tampered_file_is_rejected_and_unlocked()
    {
        var path = Path.Combine(_dir.FullName, "x.exe");
        File.WriteAllText(path, "evil");
        Assert.Throws<IntegrityException>(() => PinnedFiles.OpenVerified(path, new string('0', 64)));
        File.Delete(path); // поток закрыт, файл не заблокирован
    }

    [Fact]
    public void Verified_file_cannot_be_replaced_while_open()
    {
        var path = Path.Combine(_dir.FullName, "x.exe");
        File.WriteAllText(path, "good");
        using var fs = File.OpenRead(path);
        var sha = PinnedFiles.Sha256(fs);
        fs.Dispose();

        using (PinnedFiles.OpenVerified(path, sha))
        {
            Assert.Throws<IOException>(() => File.WriteAllText(path, "evil"));
            Assert.Throws<IOException>(() => File.Move(path, path + ".old"));
        }
    }

    [Fact]
    public void Missing_file_gives_clear_message() =>
        Assert.Contains("не найден", Assert.Throws<IntegrityException>(() => PinnedFiles.OpenVerified(Path.Combine(_dir.FullName, "nope.exe"), new string('0', 64))).Message, StringComparison.Ordinal);
}
