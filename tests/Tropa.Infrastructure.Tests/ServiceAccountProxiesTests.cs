using System.Buffers.Binary;
using System.Text;
using Tropa.Core.Diagnostics;
using Tropa.Infrastructure.SystemIntegration;

namespace Tropa.Infrastructure.Tests;

/// <summary>Кусты HKEY_USERS в памяти: тесты не трогают настоящий реестр.</summary>
internal sealed class FakeServiceAccountProxyStore : IServiceAccountProxyStore
{
    public Dictionary<string, AccountProxyValues> Accounts { get; } = new(StringComparer.Ordinal);
    public List<string> Writes { get; } = [];

    public AccountProxyValues? Read(string sid) => Accounts.GetValueOrDefault(sid);

    public void Write(string sid, AccountProxyValues values)
    {
        Accounts[sid] = values;
        Writes.Add(sid);
    }
}

public sealed class ServiceAccountProxiesTests
{
    private static byte[] Blob(int flags, string proxy)
    {
        var text = Encoding.ASCII.GetBytes(proxy);
        var blob = new byte[16 + text.Length + 40];
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0), 0x46);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(8), flags);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(12), text.Length);
        text.CopyTo(blob, 16);
        return blob;
    }

    /// <summary>Как было на компьютере владельца 2026-11-21: LocalSystem с прокси NekoBox на 2080.</summary>
    private static FakeServiceAccountProxyStore NekoBoxLeftovers() => new()
    {
        Accounts =
        {
            ["S-1-5-18"] = new AccountProxyValues(1, "127.0.0.1:2080", Blob(3, "127.0.0.1:2080"), Blob(3, "127.0.0.1:2080")),
            ["S-1-5-19"] = new AccountProxyValues(null, null, null, null),
        },
    };

    [Fact]
    public void Snapshot_names_accounts_and_skips_missing_hives()
    {
        var snapshot = new ServiceAccountProxies(NekoBoxLeftovers()).Snapshot();

        Assert.Equal(["LocalSystem", "LocalService"], snapshot.Select(p => p.Account));
        Assert.Equal(new ServiceAccountProxy("LocalSystem", true, "127.0.0.1:2080"), snapshot[0]);
        Assert.False(snapshot[1].Enabled);
    }

    [Fact]
    public void Proxy_enabled_only_in_binary_settings_is_still_seen()
    {
        var store = new FakeServiceAccountProxyStore
        {
            Accounts = { ["S-1-5-18"] = new AccountProxyValues(0, null, Blob(3, "127.0.0.1:2080"), null) },
        };

        Assert.Equal(new ServiceAccountProxy("LocalSystem", true, "127.0.0.1:2080"), new ServiceAccountProxies(store).Snapshot()[0]);
    }

    [Fact]
    public void Dead_proxy_is_disabled_everywhere_and_address_kept()
    {
        var store = NekoBoxLeftovers();
        var done = new ServiceAccountProxies(store).DisableDead(_ => false);

        Assert.Equal(["LocalSystem → 127.0.0.1:2080"], done);
        var after = store.Accounts["S-1-5-18"];
        Assert.Equal(0, after.ProxyEnable);
        Assert.Equal("127.0.0.1:2080", after.ProxyServer);
        Assert.Equal((false, "127.0.0.1:2080"), WinInetConnectionBlob.Parse(after.DefaultConnectionSettings));
        Assert.Equal((false, "127.0.0.1:2080"), WinInetConnectionBlob.Parse(after.SavedLegacySettings));
        Assert.False(new ServiceAccountProxies(store).Snapshot()[0].Enabled);
        Assert.Equal(["S-1-5-18"], store.Writes);
    }

    [Fact]
    public void Proxy_of_a_running_program_is_left_alone()
    {
        var store = NekoBoxLeftovers();

        Assert.Empty(new ServiceAccountProxies(store).DisableDead(port => port == 2080));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void External_proxy_is_left_alone()
    {
        var store = new FakeServiceAccountProxyStore
        {
            Accounts = { ["S-1-5-18"] = new AccountProxyValues(1, "corp-proxy:3128", Blob(3, "corp-proxy:3128"), null) },
        };

        Assert.Empty(new ServiceAccountProxies(store).DisableDead(_ => false));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public void Unreadable_hive_is_skipped()
    {
        var store = new ThrowingStore();
        Assert.Empty(new ServiceAccountProxies(store).Snapshot());
        Assert.Empty(new ServiceAccountProxies(store).DisableDead(_ => false));
    }

    private sealed class ThrowingStore : IServiceAccountProxyStore
    {
        public AccountProxyValues? Read(string sid) => throw new UnauthorizedAccessException();

        public void Write(string sid, AccountProxyValues values) => throw new InvalidOperationException();
    }
}
