using System.Text.Json;
using System.Text.Json.Serialization;
using Tropa.Core.Routing;

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
            Routing = settings.Routing with
            {
                Rules = settings.Routing.Rules.Select(r => r with { Tcp = Portable(r.Tcp), Udp = Portable(r.Udp) }).ToList(),
            },
        };
        return JsonSerializer.Serialize(new SettingsBackupFile { Exported = now, Settings = clean }, BackupJsonContext.Default.SettingsBackupFile);
    }

    // «Через конкретный сервер» в чужой копии Тропы смысла не имеет: такого сервера там нет.
    private static RuleAction Portable(RuleAction a) => a.Kind == RuleActionKind.Server ? RuleAction.Proxy : a;

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
            AutoIntervalMinutes = c.AutoIntervalMinutes is 3 or 10 or 30 ? c.AutoIntervalMinutes : cc.AutoIntervalMinutes,
            AutoTolMs = Math.Clamp(c.AutoTolMs, 0, 1000),
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
            FragPackets = Check(p.FragPackets ?? "", current.Dpi.FragPackets, SettingsValidation.FragPackets(p.FragPackets ?? ""), "Пакеты фрагментации"),
            FragLen = Check(p.FragLen ?? "", current.Dpi.FragLen, SettingsValidation.Range(p.FragLen ?? "", 1, 1000), "Длина фрагментов"),
            FragInt = Check(p.FragInt ?? "", current.Dpi.FragInt, SettingsValidation.Range(p.FragInt ?? "", 0, 1000), "Интервал фрагментов"),
            NoiseLen = Check(p.NoiseLen ?? "", current.Dpi.NoiseLen, SettingsValidation.Range(p.NoiseLen ?? "", 1, 2000), "Длина шума"),
            NoiseDelay = Check(p.NoiseDelay ?? "", current.Dpi.NoiseDelay, SettingsValidation.Range(p.NoiseDelay ?? "", 0, 1000), "Задержка шума"),
            Utls = Parsing.Validation.Fingerprint(p.Utls, []) ?? current.Dpi.Utls,
            NoiseType = p.NoiseType is "rand" or "str" or "base64" ? p.NoiseType : current.Dpi.NoiseType,
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
        };

        var rules = new List<Rule>();
        foreach (var r in (s.Routing.Rules ?? []).Take(MaxRules))
        {
            var match = r.Match with
            {
                Processes = (r.Match.Processes ?? []).Select(RuleInput.NormalizeProcess).OfType<string>().ToList(),
                DomainSuffixes = (r.Match.DomainSuffixes ?? []).Select(RuleInput.NormalizeDomain).OfType<string>().ToList(),
                Domains = (r.Match.Domains ?? []).Select(RuleInput.NormalizeDomain).OfType<string>().ToList(),
            };
            if (match.IsEmpty)
                continue;
            rules.Add(r with
            {
                Id = Guid.NewGuid(),
                Match = match,
                Tcp = Known(r.Tcp, knownProfiles),
                Udp = Known(r.Udp, knownProfiles),
            });
        }

        if ((s.Routing.Rules?.Count ?? 0) > rules.Count)
            warnings.Add($"Пропущено правил: {s.Routing.Rules!.Count - rules.Count} (пустые или неверные).");

        var result = s with
        {
            SchemaVersion = AppSettings.CurrentSchema,
            General = general,
            Connection = connection,
            Dns = dns,
            Dpi = dpi,
            Subscriptions = subs,
            // Защита от отката обновлений не должна зависеть от импортированного файла.
            Cores = s.Cores with { LastManifestSequence = current.Cores.LastManifestSequence, LastUpdateCheck = current.Cores.LastUpdateCheck },
            Routing = s.Routing with { Rules = rules },
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
