using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Routing;
using Tropa.Core.Tests.Generation;

namespace Tropa.Core.Tests.Routing;

/// <summary>Маршруты (ADR-067): выбор, действующий маршрут, дубликат, резервная копия.</summary>
public sealed class RouteBookTests
{
    private static readonly Guid RouteId = new(30, 0, 0, new byte[8]);

    private static AppSettings WithRoute() => new()
    {
        Routing = new RoutingSettings
        {
            Preset = RoutePreset.ExceptRu,
            Rules = [new Rule { Match = new RuleMatch { DomainSuffixes = ["shared.example"] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Proxy }],
            Routes =
            [
                new CustomRoute
                {
                    Id = RouteId,
                    Name = "Только 2ip",
                    Base = RoutePreset.ItemsOnly,
                    Rules = [new Rule { Match = new RuleMatch { DomainSuffixes = ["2ip.io"] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Proxy }],
                },
            ],
        },
    };

    [Fact]
    public void Choices_list_builtin_then_custom_and_classic_hides_custom()
    {
        var s = WithRoute();
        var choices = RouteBook.Choices(s);
        Assert.Equal(4, choices.Count);
        Assert.Equal("Только 2ip", choices[3].Name);
        Assert.Equal(3, RouteBook.Choices(s with { General = s.General with { ClassicRoutes = true } }).Count);
    }

    [Fact]
    public void Selecting_custom_route_replaces_items_and_base()
    {
        var s = WithRoute();
        s = s with { Routing = RouteBook.Select(s.Routing, RouteId.ToString("N")) };
        var effective = RouteBook.Effective(s);
        Assert.Equal(RoutePreset.ItemsOnly, effective.Preset);
        Assert.Equal("2ip.io", Assert.Single(effective.Rules).Match.DomainSuffixes[0]);
        Assert.Equal(RouteId.ToString("N"), RouteBook.ActiveKey(s));

        // Прежний экран: свои маршруты не действуют, работает встроенный.
        var classic = s with { General = s.General with { ClassicRoutes = true } };
        Assert.Equal(RoutePreset.ExceptRu, RouteBook.Effective(classic).Preset);
        Assert.Equal("shared.example", Assert.Single(RouteBook.Effective(classic).Rules).Match.DomainSuffixes[0]);

        // Выбор встроенного снимает свой маршрут.
        s = s with { Routing = RouteBook.Select(s.Routing, "preset:All") };
        Assert.Null(s.Routing.ActiveRouteId);
        Assert.Equal(RoutePreset.All, RouteBook.Effective(s).Preset);
    }

    [Fact]
    public void Removing_active_route_falls_back_to_builtin()
    {
        var r = RouteBook.Select(WithRoute().Routing, RouteId.ToString("N"));
        r = RouteBook.Remove(r, RouteId);
        Assert.Empty(r.Routes);
        Assert.Null(r.ActiveRouteId);
    }

    [Fact]
    public void Duplicate_copies_items_with_new_ids()
    {
        var s = WithRoute();
        var copy = RouteBook.Duplicate(s.Routing, "preset:BlockedOnly");
        Assert.Equal(RoutePreset.BlockedOnly, copy.Base);
        Assert.Equal("Только заблокированное (копия)", copy.Name);
        Assert.NotEqual(s.Routing.Rules[0].Id, Assert.Single(copy.Rules).Id);

        var again = RouteBook.Duplicate(s.Routing, RouteId.ToString("N"));
        Assert.Equal("Только 2ip (копия)", again.Name);
        Assert.Equal(RoutePreset.ItemsOnly, again.Base);
    }

    [Fact]
    public void Items_only_route_sends_the_rest_direct_without_preset_sets()
    {
        var s = WithRoute();
        s = s with { Routing = RouteBook.Select(s.Routing, RouteId.ToString("N")) };
        s = s with { Routing = RouteBook.Effective(s) };
        var json = SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = s,
            Active = SingBoxGoldenTests.RealityProfile,
            RuleSetDirectory = "geo",
            LocalAuth = new LocalAuth("u", "p"),
        });
        Assert.Contains("\"final\": \"direct\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(RuleSets.GeositeAiNonCn, json, StringComparison.Ordinal);
        Assert.DoesNotContain(RuleSets.GeositeRuBlocked, json, StringComparison.Ordinal);
        Assert.Contains("2ip.io", json, StringComparison.Ordinal);
        Assert.DoesNotContain("shared.example", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Geo_set_rule_round_trips_and_unknown_sets_are_dropped()
    {
        var rule = RouteBook.GeoSetRule(RuleSets.GeoipRuBlocked, RuleAction.Proxy);
        Assert.Equal(RuleSets.GeoipRuBlocked, RouteBook.GeoSetTag(rule));
        var cleaned = RouteBook.KnownSetsOnly(new RuleMatch { GeoIp = ["ru-blocked", "../../evil"], GeoSite = ["nope"] });
        Assert.Equal(["ru-blocked"], cleaned.GeoIp);
        Assert.Empty(cleaned.GeoSite);
    }

    [Fact]
    public void Backup_keeps_routes_and_strips_servers()
    {
        var server = Guid.NewGuid();
        var s = WithRoute();
        var route0 = s.Routing.Routes[0];
        s = s with
        {
            Routing = s.Routing with
            {
                ActiveRouteId = RouteId,
                Routes =
                [
                    route0 with
                    {
                        Rules = [route0.Rules[0] with { Tcp = new RuleAction(RuleActionKind.Server, server) }],
                        CustomLists = [new CustomGeoList { Name = "L", Action = RuleActionKind.Proxy, ServerId = server, Entries = [new GeoEntry(GeoEntryKind.Suffix, "a.example")] }],
                    },
                ],
            },
        };
        var json = SettingsBackup.Export(s, DateTimeOffset.UnixEpoch);
        var (imported, warnings) = SettingsBackup.Import(json, new AppSettings(), new HashSet<Guid>());
        Assert.Empty(warnings);
        var route = Assert.Single(imported.Routing.Routes);
        Assert.Equal("Только 2ip", route.Name);
        Assert.Equal(RouteId, imported.Routing.ActiveRouteId);
        Assert.Equal(RuleActionKind.Proxy, route.Rules[0].Tcp.Kind);
        Assert.Null(Assert.Single(route.CustomLists).ServerId);
    }

    [Fact]
    public void Missing_routes_read_as_empty()
    {
        var r = new RoutingSettings { Routes = null! };
        Assert.Empty(r.Routes);
        Assert.Null(r.ActiveRouteId);
    }
}
