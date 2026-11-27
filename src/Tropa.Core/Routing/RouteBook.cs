using Tropa.Core.Model;

namespace Tropa.Core.Routing;

/// <summary>Маршрут в списке: встроенный (Preset) или свой (RouteId).</summary>
public sealed record RouteChoice(string Key, string Name, RoutePreset Base, Guid? RouteId)
{
    public bool IsBuiltIn => RouteId is null;
}

/// <summary>
/// Маршруты (ADR-067): три встроенных (основа + общие пункты Rules/CustomLists) и свои — с собственными
/// пунктами. Здесь же — какие настройки маршрута получает генератор и как правятся пункты маршрута.
/// </summary>
public static class RouteBook
{
    public const int MaxRoutes = 30;

    /// <summary>Встроенные маршруты в порядке, привычном по экрану «Правила» (индекс = (int)RoutePreset).</summary>
    public static IReadOnlyList<RouteChoice> BuiltIn { get; } =
    [
        new("preset:ExceptRu", "Всё, кроме российского", RoutePreset.ExceptRu, null),
        new("preset:BlockedOnly", "Только заблокированное", RoutePreset.BlockedOnly, null),
        new("preset:All", "Всё через сервер", RoutePreset.All, null),
    ];

    public static string BaseTitle(RoutePreset preset) => preset switch
    {
        RoutePreset.ExceptRu => "Всё, кроме российского",
        RoutePreset.BlockedOnly => "Только заблокированное",
        RoutePreset.All => "Всё через сервер",
        _ => "Только пункты маршрута",
    };

    public static string BaseDescription(RoutePreset preset) => preset switch
    {
        RoutePreset.ExceptRu => "Госуслуги, банки и российские сайты идут напрямую, остальное — через сервер. Заблокированное в РФ — всегда через сервер.",
        RoutePreset.BlockedOnly => "Через сервер идут только сайты из списка блокировок. Остальное — напрямую.",
        RoutePreset.All => "Через сервер идёт всё. Госуслуги и банки могут не пустить с зарубежного IP — добавьте их в маршрут напрямую.",
        _ => "Через сервер идут только сайты, приложения и списки этого маршрута. Всё остальное — напрямую.",
    };

    /// <summary>Все маршруты для выбора: в прежнем виде экрана — только встроенные.</summary>
    public static IReadOnlyList<RouteChoice> Choices(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.General.ClassicRoutes)
            return BuiltIn;
        return [.. BuiltIn, .. settings.Routing.Routes.Select(r => new RouteChoice(r.Id.ToString("N"), RouteName(r), r.Base, r.Id))];
    }

    public static string RouteName(CustomRoute r) => r.Name.Length > 0 ? r.Name : "Свой маршрут";

    /// <summary>Свой маршрут, который сейчас действует, или null (встроенный).</summary>
    public static CustomRoute? ActiveCustom(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.General.ClassicRoutes || settings.Routing.ActiveRouteId is not { } id)
            return null;
        return settings.Routing.Routes.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Ключ действующего маршрута.</summary>
    public static string ActiveKey(AppSettings settings) =>
        ActiveCustom(settings) is { } custom ? custom.Id.ToString("N") : BuiltIn[Math.Clamp((int)settings.Routing.Preset, 0, 2)].Key;

    public static RouteChoice Active(AppSettings settings)
    {
        var key = ActiveKey(settings);
        return Choices(settings).FirstOrDefault(c => c.Key == key) ?? BuiltIn[1];
    }

    /// <summary>
    /// Маршрутизация, которую получает генератор: у своего маршрута — его основа и пункты, у встроенного —
    /// как раньше. Основа ItemsOnly у встроенного не бывает (старые копии Тропы о ней не знают).
    /// </summary>
    public static RoutingSettings Effective(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var r = settings.Routing;
        if (ActiveCustom(settings) is { } route)
            return r with { Preset = route.Base, Rules = route.Rules, CustomLists = route.CustomLists };
        return r.Preset is RoutePreset.ExceptRu or RoutePreset.BlockedOnly or RoutePreset.All ? r : r with { Preset = RoutePreset.BlockedOnly };
    }

    /// <summary>Сделать маршрут действующим. Неизвестный ключ — без изменений.</summary>
    public static RoutingSettings Select(RoutingSettings r, string key)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (BuiltIn.FirstOrDefault(c => c.Key == key) is { } builtIn)
            return r with { Preset = builtIn.Base, ActiveRouteId = null };
        return r.Routes.FirstOrDefault(x => x.Id.ToString("N") == key) is { } route ? r with { ActiveRouteId = route.Id } : r;
    }

    /// <summary>Пункты маршрута: null — общие пункты встроенных маршрутов.</summary>
    public static (IReadOnlyList<Rule> Rules, IReadOnlyList<CustomGeoList> Lists) Items(RoutingSettings r, Guid? routeId)
    {
        ArgumentNullException.ThrowIfNull(r);
        if (routeId is { } id && r.Routes.FirstOrDefault(x => x.Id == id) is { } route)
            return (route.Rules, route.CustomLists);
        return (r.Rules, r.CustomLists);
    }

    /// <summary>Меняет пункты маршрута (null — общие пункты встроенных). Удалённый маршрут — без изменений.</summary>
    public static RoutingSettings WithRules(RoutingSettings r, Guid? routeId, Func<IReadOnlyList<Rule>, IReadOnlyList<Rule>> change)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(change);
        if (routeId is not { } id)
            return r with { Rules = change(r.Rules) };
        return r with { Routes = r.Routes.Select(x => x.Id == id ? x with { Rules = change(x.Rules) } : x).ToList() };
    }

    public static RoutingSettings WithLists(RoutingSettings r, Guid? routeId, Func<IReadOnlyList<CustomGeoList>, IReadOnlyList<CustomGeoList>> change)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(change);
        if (routeId is not { } id)
            return r with { CustomLists = change(r.CustomLists) };
        return r with { Routes = r.Routes.Select(x => x.Id == id ? x with { CustomLists = change(x.CustomLists) } : x).ToList() };
    }

    /// <summary>
    /// Копия маршрута (встроенного или своего) как нового своего: та же основа, копии пунктов с новыми id.
    /// </summary>
    public static CustomRoute Duplicate(RoutingSettings r, string key)
    {
        ArgumentNullException.ThrowIfNull(r);
        var source = r.Routes.FirstOrDefault(x => x.Id.ToString("N") == key);
        var builtIn = BuiltIn.FirstOrDefault(c => c.Key == key);
        var (rules, lists) = source is not null ? (source.Rules, source.CustomLists) : (r.Rules, r.CustomLists);
        var name = source is not null ? RouteName(source) : builtIn?.Name ?? "Маршрут";
        return new CustomRoute
        {
            Name = UniqueName(r, name + " (копия)"),
            Base = source?.Base ?? builtIn?.Base ?? RoutePreset.ItemsOnly,
            Rules = rules.Select(x => x with { Id = Guid.NewGuid() }).ToList(),
            CustomLists = lists.Select(x => x with { Id = Guid.NewGuid() }).ToList(),
        };
    }

    public static string UniqueName(RoutingSettings r, string name)
    {
        ArgumentNullException.ThrowIfNull(r);
        var taken = r.Routes.Select(RouteName).Concat(BuiltIn.Select(b => b.Name)).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        if (!taken.Contains(name))
            return name;
        for (var i = 2; ; i++)
        {
            var candidate = $"{name} {i}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    /// <summary>Удаляет свой маршрут; если он действовал — действует встроенный.</summary>
    public static RoutingSettings Remove(RoutingSettings r, Guid id)
    {
        ArgumentNullException.ThrowIfNull(r);
        return r with
        {
            Routes = r.Routes.Where(x => x.Id != id).ToList(),
            ActiveRouteId = r.ActiveRouteId == id ? null : r.ActiveRouteId,
        };
    }

    /// <summary>Наборы гео-баз, которые можно добавить в маршрут пунктом: только закреплённые файлы Тропы.</summary>
    public static IReadOnlyList<GeoListInfo> ImportableSets => GeoEdits.Catalog;

    /// <summary>Пункт маршрута «набор гео-базы»: правило с GeoIp или GeoSite.</summary>
    public static Rule GeoSetRule(string tag, RuleAction action)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var info = GeoEdits.Find(tag) ?? throw new ArgumentException($"Набора «{tag}» нет в Тропе.", nameof(tag));
        var match = RuleSets.IsIpSet(tag)
            ? new RuleMatch { GeoIp = [tag["geoip-".Length..]] }
            : new RuleMatch { GeoSite = [tag["geosite-".Length..]] };
        return new Rule { Label = info.Title, Match = match, Tcp = action, Udp = action };
    }

    /// <summary>Тег набора из пункта-набора, если правило — ровно один набор гео-базы.</summary>
    public static string? GeoSetTag(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var m = rule.Match;
        if (m.GeoIp.Count + m.GeoSite.Count != 1 || m.HasProcessCondition || m.Domains.Count + m.DomainSuffixes.Count + m.DomainKeywords.Count + m.DomainRegexes.Count + m.IpCidrs.Count > 0)
            return null;
        return m.GeoIp.Count == 1 ? "geoip-" + m.GeoIp[0] : "geosite-" + m.GeoSite[0];
    }

    /// <summary>
    /// Наборы гео-баз в правилах из недоверенного источника: только закреплённые в Тропе. Чужое имя
    /// дало бы ядру путь к несуществующему файлу (или исключение в генераторе).
    /// </summary>
    public static RuleMatch KnownSetsOnly(RuleMatch m)
    {
        ArgumentNullException.ThrowIfNull(m);
        return m with
        {
            GeoIp = (m.GeoIp ?? []).Where(n => n is not null && GeoEdits.Find("geoip-" + n) is not null).ToList(),
            GeoSite = (m.GeoSite ?? []).Where(n => n is not null && GeoEdits.Find("geosite-" + n) is not null).ToList(),
        };
    }

    /// <summary>Серверы, на которые ссылаются пункты действующего маршрута.</summary>
    public static IEnumerable<Guid> ServerIds(RoutingSettings effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        foreach (var rule in effective.Rules.Where(r => r.Enabled))
        {
            foreach (var action in new[] { rule.Tcp, rule.Udp })
            {
                if (action.Kind == RuleActionKind.Server && action.ServerId is { } id)
                    yield return id;
            }
        }

        foreach (var list in effective.CustomLists.Where(l => !l.Disabled && l.Action == RuleActionKind.Proxy))
        {
            if (list.ServerId is { } id)
                yield return id;
        }
    }
}
