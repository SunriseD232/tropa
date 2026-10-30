using Tropa.Core.Compatibility;
using Tropa.Core.Model;
using Tropa.Core.Testing;
using static Tropa.Core.Tests.Parsing.ShareLinkTests;

namespace Tropa.Core.Tests.Model;

public sealed class ServerListActionsTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private static StoredProfile Stored(string link, int order, ServerTestResult? test = null, bool hidden = false) =>
        new() { Profile = Ok(link), Order = order, LastTest = test, Hidden = hidden };

    private static string Vless(string host, string name, string sni = "a.example.com") =>
        $"vless://{Uuid}@{host}:443?security=tls&sni={sni}#{name}";

    [Fact]
    public void Duplicates_differ_only_by_name_and_keep_active_or_top()
    {
        var a = Stored(Vless("nl.example.com", "Первый"), 0);
        var b = Stored(Vless("nl.example.com", "Второй"), 1);
        var c = Stored(Vless("nl.example.com", "Третий"), 2);
        var other = Stored(Vless("nl.example.com", "Другой SNI", sni: "b.example.com"), 3);

        // Без выбранного остаётся верхний.
        Assert.Equal(new[] { b.Profile.Id, c.Profile.Id }.Order(), ServerListActions.Duplicates([a, b, c, other], null).Order());
        // Выбранный остаётся всегда, даже если он ниже.
        Assert.Equal(new[] { a.Profile.Id, b.Profile.Id }.Order(), ServerListActions.Duplicates([a, b, c, other], c.Profile.Id).Order());
    }

    [Fact]
    public void Hidden_and_chained_servers_are_not_duplicates()
    {
        var a = Stored(Vless("nl.example.com", "A"), 0);
        var hidden = Stored(Vless("nl.example.com", "B"), 1, hidden: true);
        var chained = Stored(Vless("nl.example.com", "C"), 2) with { };
        chained = chained with { Profile = chained.Profile with { ChainVia = Guid.NewGuid() } };
        Assert.Empty(ServerListActions.Duplicates([a, hidden, chained], null));
    }

    [Fact]
    public void Dead_are_failed_on_last_test_except_active()
    {
        var ok = Stored(Vless("a.example.com", "ok"), 0, new ServerTestResult { At = At, TcpMs = 50, DelayMs = 120, SpeedMbps = 40 });
        var silent = Stored(Vless("b.example.com", "silent"), 1, new ServerTestResult { At = At, Error = "timeout" });
        var handshake = Stored(Vless("c.example.com", "hs"), 2, new ServerTestResult { At = At, TcpMs = 40, Error = "tls" });
        var frozen = Stored(Vless("d.example.com", "frozen"), 3, new ServerTestResult { At = At, TcpMs = 40, DelayMs = 100, Frozen = true });
        var untested = Stored(Vless("e.example.com", "new"), 4);
        var activeDead = Stored(Vless("f.example.com", "active"), 5, new ServerTestResult { At = At, Error = "timeout" });

        var dead = ServerListActions.Dead([ok, silent, handshake, frozen, untested, activeDead], activeDead.Profile.Id);
        Assert.Equal(new[] { silent, handshake, frozen }.Select(p => p.Profile.Id).Order(), dead.Order());
    }

    [Fact]
    public void Clone_goes_to_my_servers_with_new_id_and_free_name()
    {
        var source = Ok(Vless("nl.example.com", "NL")) with { SubscriptionId = Guid.NewGuid() };
        var copy = ServerListActions.Clone(source, ["NL", "NL (копия)"]);
        Assert.NotEqual(source.Id, copy.Id);
        Assert.Null(copy.SubscriptionId);
        Assert.Equal("NL (копия 2)", copy.Name);
        Assert.Equal(source.Credential.Reveal(), copy.Credential.Reveal());
        Assert.Equal(source.Address, copy.Address);
    }

    [Theory]
    [InlineData(15, 12, false)]
    [InlineData(0, 6, false)]
    [InlineData(0, 0, false)]
    [InlineData(30, 12, true)]
    [InlineData(0, 3, true)]
    public void Custom_interval_is_anything_outside_the_list(int minutes, int hours, bool custom)
    {
        var s = new SubscriptionDefaults { UpdateMinutes = minutes, UpdateHours = hours };
        Assert.Equal(custom, s.CustomInterval);
        var compat = CompatRules.Evaluate(new AppSettings { Subscriptions = s });
        Assert.Equal(!custom, compat.Disabled.ContainsKey("subInterval"));
    }

    [Fact]
    public void Custom_interval_survives_backup_and_bad_values_do_not()
    {
        var current = new AppSettings();
        var mine = current with { Subscriptions = current.Subscriptions with { UpdateMinutes = 30 } };
        var (loaded, _) = SettingsBackup.Import(SettingsBackup.Export(mine, At), current, new HashSet<Guid>());
        Assert.Equal(TimeSpan.FromMinutes(30), loaded.Subscriptions.Interval);

        var tooOften = current with { Subscriptions = current.Subscriptions with { UpdateMinutes = 1 } };
        (loaded, _) = SettingsBackup.Import(SettingsBackup.Export(tooOften, At), current, new HashSet<Guid>());
        Assert.Equal(current.Subscriptions.UpdateMinutes, loaded.Subscriptions.UpdateMinutes);
    }

    [Fact]
    public void Server_fragment_is_unavailable_for_hysteria2_and_plain_servers()
    {
        var on = new AppSettings { Dpi = new DpiSettings { ServerFragment = true } };
        Assert.False(CompatRules.Evaluate(on, Ok(VlessReality)).Disabled.ContainsKey("serverFragment"));
        Assert.True(CompatRules.Evaluate(on, Ok("hysteria2://pw@pl.example.com:443?sni=pl.example.com#PL")).Disabled.ContainsKey("serverFragment"));
        Assert.True(CompatRules.Evaluate(on, Ok($"vless://{Uuid}@plain.example.com:80#plain")).Disabled.ContainsKey("serverFragment"));
    }
}
