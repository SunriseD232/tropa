using Tropa.Core.Model;
using static Tropa.Core.Tests.Parsing.ShareLinkTests;

namespace Tropa.Core.Tests.Model;

public sealed class SubscriptionMergerTests
{
    private static readonly Guid Sub = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static Profile P(string host, string name = "x") =>
        Ok($"vless://{Uuid}@{host}:443?security=none#{name}") with { SubscriptionId = Sub };

    [Fact]
    public void Keeps_ids_favorites_and_chains_of_known_servers()
    {
        var old = P("a.example.com", "old name");
        var relay = Guid.NewGuid();
        var existing = new[] { new StoredProfile { Profile = old with { ChainVia = relay }, Favorite = true } };

        var fresh = P("a.example.com", "new name");
        var r = SubscriptionMerger.Merge(Sub, existing, [fresh], Now);

        var merged = Assert.Single(r.Profiles);
        Assert.Equal(old.Id, merged.Profile.Id);         // выбранный сервер остаётся выбранным
        Assert.Equal("new name", merged.Profile.Name);   // данные провайдера обновились
        Assert.True(merged.Favorite);
        Assert.Equal(relay, merged.Profile.ChainVia);     // цепочка — настройка пользователя
        Assert.Equal((0, 1, 0), (r.Added, r.Updated, r.Removed));
    }

    [Fact]
    public void New_servers_are_added_and_duplicates_dropped()
    {
        var r = SubscriptionMerger.Merge(Sub, [], [P("a.example.com"), P("a.example.com"), P("b.example.com")], Now);
        Assert.Equal(2, r.Profiles.Count);
        Assert.Equal(2, r.Added);
        Assert.All(r.Profiles, p => Assert.Equal(Sub, p.Profile.SubscriptionId));
    }

    [Fact]
    public void Missing_servers_are_kept_for_grace_period_then_dropped()
    {
        var existing = new[] { new StoredProfile { Profile = P("gone.example.com") } };

        var first = SubscriptionMerger.Merge(Sub, existing, [], Now);
        var marked = Assert.Single(first.Profiles);
        Assert.Equal(Now, marked.RemovedByProvider);
        Assert.Equal(1, first.Removed);

        var later = SubscriptionMerger.Merge(Sub, first.Profiles, [], Now + TimeSpan.FromDays(3));
        Assert.Equal(Now, Assert.Single(later.Profiles).RemovedByProvider);
        Assert.Equal(0, later.Removed);

        var expired = SubscriptionMerger.Merge(Sub, later.Profiles, [], Now + SubscriptionMerger.RemovalGrace);
        Assert.Empty(expired.Profiles);
    }

    [Fact]
    public void Returning_server_is_unmarked()
    {
        var existing = new[] { new StoredProfile { Profile = P("a.example.com"), RemovedByProvider = Now } };
        var r = SubscriptionMerger.Merge(Sub, existing, [P("a.example.com")], Now + TimeSpan.FromDays(1));
        Assert.Null(Assert.Single(r.Profiles).RemovedByProvider);
    }

    [Fact]
    public void Other_subscriptions_and_manual_servers_are_untouched()
    {
        var manual = new StoredProfile { Profile = P("manual.example.com") with { SubscriptionId = null } };
        var other = new StoredProfile { Profile = P("other.example.com") with { SubscriptionId = Guid.NewGuid() } };
        var r = SubscriptionMerger.Merge(Sub, [manual, other], [], Now);
        Assert.Contains(manual, r.Profiles);
        Assert.Contains(other, r.Profiles);
    }

    [Fact]
    public void Credential_change_is_a_different_server()
    {
        var existing = new[] { new StoredProfile { Profile = P("a.example.com") } };
        var rotated = Ok($"vless://{Guid.NewGuid()}@a.example.com:443?security=none#x") with { SubscriptionId = Sub };
        var r = SubscriptionMerger.Merge(Sub, existing, [rotated], Now);
        Assert.Equal(1, r.Added);
        Assert.Equal(1, r.Removed);
    }
}
