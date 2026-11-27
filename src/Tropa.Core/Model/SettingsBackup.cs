using System.Text.Json;
using System.Text.Json.Serialization;
using Tropa.Core.Routing;
using Tropa.Core.Security;

namespace Tropa.Core.Model;

/// <summary>Файл резервной копии настроек и правил (info.ru.json: backup, ruleExport).</summary>
public sealed record SettingsBackupFile
{
    public string Kind { get; init; } = SettingsBackup.Kind;
    public int Schema { get; init; } = AppSettings.CurrentSchema;
    public DateTimeOffset Exported { get; init; }
    public AppSettings? Settings { get; init; }
}

/// <summary>
/// Экспорт и импорт настроек и правил. Серверы, подписки и ключи в файл не попадают: настройки можно
/// спокойно переслать другу. Импортируемый файл — недоверенные данные: каждое значение проверяется
/// так же, как при вводе руками, неверное заменяется текущим с предупреждением.
/// </summary>
public static class SettingsBackup
{
    public const string Kind = "tropa-settings";
    private const int MaxRules = 1000;
    private const int MaxBytes = 2 * 1024 * 1024;

    public static string Export(AppSettings settings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(settings);
        // Служебное и привязанное к этому компьютеру не экспортируем.
        var clean = settings with
        {
            Connection = settings.Connection with { UwpLoopback = [] },
            Cores = settings.Cores with { LastManifestSequence = 0, LastUpdateCheck = null },
            Telemetry = new(),
            Routing = settings.Routing with
            {
                Rules = PortableRules(settings.Routing.Rules),
                CustomLists = PortableLists(settings.Routing.CustomLists),
                Routes = settings.Routing.Routes.Select(r => r with { Rules = PortableRules(r.Rules), CustomLists = PortableLists(r.CustomLists) }).ToList(),
            },
        };
        return JsonSerializer.Serialize(new SettingsBackupFile { Exported = now, Settings = clean }, BackupJsonContext.Default.SettingsBackupFile);
    }

    // «Через конкретный сервер» в чужой копии Тропы смысла не имеет: такого сервера там нет.
    private static RuleAction Portable(RuleAction a) => a.Kind == RuleActionKind.Server ? RuleAction.Proxy : a;

    private static List<Rule> PortableRules(IReadOnlyList<Rule> rules) => rules.Select(r => r with { Tcp = Portable(r.Tcp), Udp = Portable(r.Udp) }).ToList();

    private static List<CustomGeoList> PortableLists(IReadOnlyList<CustomGeoList> lists) => lists.Select(l => l with { ServerId = null }).ToList();

    public static (AppSettings Settings, IReadOnlyList<string> Warnings) Import(string json, AppSettings current, IReadOnlySet<Guid> knownProfiles)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(knownProfiles);
        if (json is null || json.Length > MaxBytes)
            throw new InvalidDataException("Файл слишком большой для настроек Тропы.");
        SettingsBackupFile? file;
        try
        {
            file = JsonSerializer.Deserialize(json, BackupJsonContext.Default.SettingsBackupFile);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Это не файл настроек Тропы или он повреждён.", ex);
        }

        if (file is null || file.Kind != Kind || file.Settings is null)
            throw new InvalidDataException("Это не файл настроек Тропы.");
        if (file.Schema > AppSettings.CurrentSchema)
            throw new InvalidDataException("Файл сделан более новой версией Тропы. Обновите Тропу.");

        var s = file.Settings;
        var warnings = new List<string>();
        T Check<T>(T value, T fallback, string? error, string what)
        {
            if (error is null)
                return value;
            warnings.Add($"{what}: {error} Оставлено прежнее значение.");
            return fallback;
        }

        var c = s.Connection;
        var cc = current.Connection;
        var connection = c with
        {
            Mtu = Check(c.Mtu, cc.Mtu, SettingsValidation.Mtu(c.Mtu), "MTU"),
            SocksPort = Check(c.SocksPort, cc.SocksPort, SettingsValidation.Port(c.SocksPort), "Порт прокси"),
            LanPort = Check(c.LanPort, cc.LanPort, SettingsValidation.Port(c.LanPort, c.SocksPort), "Порт для локальной сети"),
            SysBypass = Check(c.SysBypass ?? "", cc.SysBypass, SettingsValidation.SysBypass(c.SysBypass ?? ""), "Исключения прокси"),
            TestUrl = Check(c.TestUrl ?? "", cc.TestUrl, SettingsValidation.HttpUrl(c.TestUrl ?? "", httpsOnly: false), "Адрес проверки"),
            SpeedUrl = Check(c.SpeedUrl ?? "", cc.SpeedUrl, SettingsValidation.HttpUrl(c.SpeedUrl ?? "", httpsOnly: true), "Адрес теста скорости"),
            StunServer = Check(c.StunServer ?? "", cc.StunServer, SettingsValidation.HostPort(c.StunServer ?? ""), "STUN-сервер"),
            SpeedSizeMb = c.SpeedSizeMb is 1 or 10 or 25 ? c.SpeedSizeMb : cc.SpeedSizeMb,
            Parallel = c.Parallel is 2 or 5 or 10 ? c.Parallel : cc.Parallel,
            UwpLoopback = cc.UwpLoopback,
        };
        var d = s.Dns;
        var dns = d with
        {
            RemoteDns = Check(d.RemoteDns ?? "", current.Dns.RemoteDns, SettingsValidation.Dns(d.RemoteDns ?? ""), "Удалённый DNS"),
            LocalDns = Check(d.LocalDns ?? "", current.Dns.LocalDns, SettingsValidation.Dns(d.LocalDns ?? ""), "Локальный DNS"),
            Hosts = Check(d.Hosts ?? "", current.Dns.Hosts, SettingsValidation.Hosts(d.Hosts ?? ""), "Свои адреса (hosts)"),
        };
        var p = s.Dpi;
        var dpi = p with
        {
            Utls = Parsing.Validation.Fingerprint(p.Utls, []) ?? current.Dpi.Utls,
            MuxConc = Math.Clamp(p.MuxConc, 1, 128),
        };
        var g = s.General;
        var general = g with
        {
            Hotkey = Check(g.Hotkey ?? "", current.General.Hotkey, SettingsValidation.Hotkey(g.Hotkey ?? ""), "Горячая клавиша"),
        };
        var subs = s.Subscriptions with
        {
            SubUACustom = string.IsNullOrEmpty(s.Subscriptions.SubUACustom) ? ""
                : Check(s.Subscriptions.SubUACustom, current.Subscriptions.SubUACustom, SettingsValidation.UserAgent(s.Subscriptions.SubUACustom), "Свой User-Agent"),
            UpdateHours = s.Subscriptions.UpdateHours is 0 or 6 or 12 or 24 ? s.Subscriptions.UpdateHours : current.Subscriptions.UpdateHours,
            UpdateMinutes = s.Subscriptions.UpdateMinutes is 0 or (>= SubscriptionDefaults.MinCustomMinutes and <= SubscriptionDefaults.MaxCustomMinutes) ? s.Subscriptions.UpdateMinutes : current.Subscriptions.UpdateMinutes,
        };

        List<Rule> CleanRules(IReadOnlyList<Rule>? source, string where)
        {
            var rules = new List<Rule>();
            foreach (var r in (source ?? []).Take(MaxRules))
            {
                if (r?.Match is null)
                    continue;
                var match = RouteBook.KnownSetsOnly(r.Match with
                {
                    Processes = (r.Match.Processes ?? []).Select(RuleInput.NormalizeProcess).OfType<string>().ToList(),
                    DomainSuffixes = (r.Match.DomainSuffixes ?? []).Select(RuleInput.NormalizeDomain).OfType<string>().ToList(),
                    Domains = (r.Match.Domains ?? []).Select(RuleInput.NormalizeDomain).OfType<string>().ToList(),
                });
                if (match.IsEmpty)
                    continue;
                rules.Add(r with
                {
                    Id = Guid.NewGuid(),
                    Label = r.Label is null ? null : NameSanitizer.Sanitize(r.Label, ""),
                    Match = match,
                    Tcp = Known(r.Tcp, knownProfiles),
                    Udp = Known(r.Udp, knownProfiles),
                });
            }

            if ((source?.Count ?? 0) > rules.Count)
                warnings.Add($"{where}: пропущено пунктов: {source!.Count - rules.Count} (пустые или неверные).");
            return rules;
        }

        List<CustomGeoList> CleanLists(IReadOnlyList<CustomGeoList>? source) =>
            GeoEdits.SanitizeCustom(source).Select(l => l.ServerId is { } id && !knownProfiles.Contains(id) ? l with { ServerId = null } : l).ToList();

        var rules = CleanRules(s.Routing.Rules, "Встроенные маршруты");
        var routes = new List<CustomRoute>();
        foreach (var route in (s.Routing.Routes ?? []).Take(RouteBook.MaxRoutes))
        {
            if (route is null || route.Id == Guid.Empty || routes.Any(x => x.Id == route.Id))
                continue;
            var name = NameSanitizer.Sanitize(route.Name, "Свой маршрут");
            routes.Add(route with
            {
                Name = name,
                Base = Enum.IsDefined(route.Base) ? route.Base : RoutePreset.ItemsOnly,
                Rules = CleanRules(route.Rules, "Маршрут «" + name + "»"),
                CustomLists = CleanLists(route.CustomLists),
            });
        }

        var result = s with
        {
            SchemaVersion = AppSettings.CurrentSchema,
            General = general,
            Connection = connection,
            Dns = dns,
            Dpi = dpi,
            Subscriptions = subs,
            Admin = s.Admin with
            {
                RefreshMinutes = s.Admin.RefreshMinutes is 5 or 15 or 60 ? s.Admin.RefreshMinutes : current.Admin.RefreshMinutes,
                Title = (s.Admin.Title ?? "").Length <= 60 ? NameSanitizer.Sanitize(s.Admin.Title ?? "", "") : current.Admin.Title,
            },
            // Защита от отката обновлений не должна зависеть от импортированного файла.
            Cores = s.Cores with { LastManifestSequence = current.Cores.LastManifestSequence, LastUpdateCheck = current.Cores.LastUpdateCheck },
            // Имя и согласие на журнал — личное, из чужого файла не берём.
            Telemetry = current.Telemetry,
            Routing = s.Routing with
            {
                Preset = s.Routing.Preset is RoutePreset.ExceptRu or RoutePreset.BlockedOnly or RoutePreset.All ? s.Routing.Preset : current.Routing.Preset,
                Rules = rules,
                GeoEdits = GeoEdits.Sanitize(s.Routing.GeoEdits),
                CustomLists = CleanLists(s.Routing.CustomLists),
                Routes = routes,
                ActiveRouteId = routes.Any(r => r.Id == s.Routing.ActiveRouteId) ? s.Routing.ActiveRouteId : null,
            },
        };
        return (result, warnings);
    }

    private static RuleAction Known(RuleAction? a, IReadOnlySet<Guid> profiles) =>
        a is null ? RuleAction.Proxy
        : a.Kind == RuleActionKind.Server && (a.ServerId is not { } id || !profiles.Contains(id)) ? RuleAction.Proxy
        : a;
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SettingsBackupFile))]
internal sealed partial class BackupJsonContext : JsonSerializerContext;
