using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Core.Security;

namespace Tropa.Infrastructure.Storage;

/// <summary>Всё состояние пользователя: настройки, подписки, серверы, выбранный сервер.</summary>
public sealed record AppState
{
    public AppSettings Settings { get; init; } = new();
    public IReadOnlyList<Subscription> Subscriptions { get; init; } = [];
    public IReadOnlyList<StoredProfile> Profiles { get; init; } = [];
    public Guid? ActiveProfileId { get; init; }

    public Profile? ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Profile.Id == ActiveProfileId)?.Profile;
}

/// <summary>Результат загрузки: состояние, секреты и предупреждение, если пришлось восстанавливаться.</summary>
public sealed record LoadResult(AppState State, SecretStore Secrets, string? Warning);

/// <summary>
/// Хранилище состояния (docs/03-architecture.md, §4). Настройки и секреты — разные файлы:
/// settings.json можно показать при отладке, в нём нет ни одного ключа доступа.
/// Запись атомарная, хранится 5 резервных копий, при порче файла загружается последняя целая копия.
/// </summary>
public sealed class StateStore(string directory)
{
    public const int BackupsToKeep = 5;
    private static readonly TimeSpan BackupInterval = TimeSpan.FromHours(1);

    public string SettingsPath => Path.Combine(directory, "settings.json");
    public string SecretsPath => Path.Combine(directory, "secrets.bin");
    public string BackupDirectory => Path.Combine(directory, "backups");

    public LoadResult Load()
    {
        var secrets = SecretStore.Open(SecretsPath);
        if (!File.Exists(SettingsPath))
            return new LoadResult(new AppState(), secrets, null);

        var candidates = new List<string> { SettingsPath };
        if (Directory.Exists(BackupDirectory))
            candidates.AddRange(Directory.GetFiles(BackupDirectory, "settings-*.json").OrderDescending(StringComparer.Ordinal));

        foreach (var path in candidates)
        {
            StateDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(File.ReadAllText(path), StorageJsonContext.Default.StateDto);
            }
            catch (JsonException)
            {
                continue;
            }

            if (dto is null)
                continue;
            if (dto.Schema > AppSettings.CurrentSchema)
            {
                // Не перезаписываем данные более новой версии: это потеряло бы настройки пользователя.
                throw new InvalidDataException("Настройки созданы более новой версией Тропы. Обновите программу.");
            }

            var warning = path == SettingsPath ? null : "Файл настроек был повреждён, загружена резервная копия.";
            return new LoadResult(FromDto(dto, secrets), secrets, warning);
        }

        return new LoadResult(new AppState(), secrets, "Файл настроек повреждён, резервных копий нет. Загружены настройки по умолчанию.");
    }

    public void Save(AppState state, SecretStore secrets, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(secrets);

        var dto = ToDto(state, secrets);
        BackupIfDue(now);

        // Секреты удаляются, только если на них не ссылаются ни текущие настройки, ни резервные копии:
        // иначе восстановление из копии молча потеряло бы серверы.
        var keep = ReferencedSecretKeys(state);
        keep.UnionWith(BackupSecretKeys());
        secrets.RetainOnly(keep);
        secrets.Save();

        AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(dto, StorageJsonContext.Default.StateDto) + "\n");
    }

    private HashSet<string> BackupSecretKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(BackupDirectory))
            return keys;
        foreach (var path in Directory.GetFiles(BackupDirectory, "settings-*.json"))
        {
            try
            {
                var dto = JsonSerializer.Deserialize(File.ReadAllText(path), StorageJsonContext.Default.StateDto);
                if (dto is null)
                    continue;
                keys.UnionWith(dto.Profiles.Select(p => CredentialKey(p.Id)));
                keys.UnionWith(dto.Subscriptions.Select(s => SubscriptionUrlKey(s.Id)));
            }
            catch (JsonException)
            {
            }
        }

        return keys;
    }

    private void BackupIfDue(DateTimeOffset now)
    {
        if (!File.Exists(SettingsPath))
            return;
        Directory.CreateDirectory(BackupDirectory);
        var backups = Directory.GetFiles(BackupDirectory, "settings-*.json").Order(StringComparer.Ordinal).ToList();
        // Время копии — из её имени: File.Copy сохраняет время изменения исходного файла,
        // и по файловой системе копия выглядела бы старше или моложе, чем есть.
        var last = backups.Count > 0 && DateTime.TryParseExact(Path.GetFileNameWithoutExtension(backups[^1])["settings-".Length..],
            "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTime.MinValue;
        if (now.UtcDateTime - last >= BackupInterval)
        {
            var name = $"settings-{now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
            File.Copy(SettingsPath, Path.Combine(BackupDirectory, name), overwrite: true);
            backups.Add(Path.Combine(BackupDirectory, name));
        }

        foreach (var old in backups.Order(StringComparer.Ordinal).SkipLast(BackupsToKeep))
            File.Delete(old);
    }

    // ---------------- Ключи секретов ----------------

    public const string LocalUserKey = "local:user";
    public const string LocalPasswordKey = "local:pass";

    public static string CredentialKey(Guid profileId) => "cred:" + profileId.ToString("N");

    public static string SubscriptionUrlKey(Guid subscriptionId) => "sub:" + subscriptionId.ToString("N");

    private static HashSet<string> ReferencedSecretKeys(AppState state)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal) { LocalUserKey, LocalPasswordKey };
        foreach (var p in state.Profiles)
            keys.Add(CredentialKey(p.Profile.Id));
        foreach (var s in state.Subscriptions)
            keys.Add(SubscriptionUrlKey(s.Id));
        return keys;
    }

    // ---------------- DTO ↔ модель ----------------

    private static StateDto ToDto(AppState state, SecretStore secrets)
    {
        foreach (var p in state.Profiles)
            secrets.Set(CredentialKey(p.Profile.Id), p.Profile.Credential.Reveal());
        foreach (var s in state.Subscriptions)
            secrets.Set(SubscriptionUrlKey(s.Id), s.Url.Reveal());

        return new StateDto
        {
            Schema = AppSettings.CurrentSchema,
            Settings = state.Settings,
            ActiveProfileId = state.ActiveProfileId,
            Subscriptions = state.Subscriptions.Select(s => new SubscriptionDto
            {
                Id = s.Id,
                Name = s.Name,
                UpdateHours = s.UpdateHours,
                UserAgent = s.UserAgent,
                CustomUserAgent = s.CustomUserAgent,
                SendHwid = s.SendHwid,
                FetchViaProxy = s.FetchViaProxy,
                Info = s.Info,
                LastUpdated = s.LastUpdated,
                LastError = s.LastError,
            }).ToList(),
            Profiles = state.Profiles.Select(sp => new StoredProfileDto
            {
                Id = sp.Profile.Id,
                Name = sp.Profile.Name,
                SubscriptionId = sp.Profile.SubscriptionId,
                Protocol = sp.Profile.Protocol,
                Address = sp.Profile.Address,
                Port = sp.Profile.Port,
                Flow = sp.Profile.Flow,
                VmessCipher = sp.Profile.VmessCipher,
                Transport = sp.Profile.Transport,
                Security = sp.Profile.Security,
                ChainVia = sp.Profile.ChainVia,
                RemovedByProvider = sp.RemovedByProvider,
                Favorite = sp.Favorite,
                Order = sp.Order,
                LastTest = sp.LastTest,
            }).ToList(),
        };
    }

    private static AppState FromDto(StateDto dto, SecretStore secrets)
    {
        var subscriptions = new List<Subscription>();
        foreach (var s in dto.Subscriptions)
        {
            // Нет секрета — нет подписки: без URL её не обновить, а показывать «пустышку» бессмысленно.
            if (secrets.Get(SubscriptionUrlKey(s.Id)) is not { } url)
                continue;
            subscriptions.Add(new Subscription
            {
                Id = s.Id,
                Name = s.Name,
                Url = new Secret(url),
                UpdateHours = s.UpdateHours,
                UserAgent = s.UserAgent,
                CustomUserAgent = s.CustomUserAgent,
                SendHwid = s.SendHwid,
                FetchViaProxy = s.FetchViaProxy,
                Info = s.Info,
                LastUpdated = s.LastUpdated,
                LastError = s.LastError,
            });
        }

        var profiles = new List<StoredProfile>();
        foreach (var p in dto.Profiles)
        {
            if (secrets.Get(CredentialKey(p.Id)) is not { } credential)
                continue;
            profiles.Add(new StoredProfile
            {
                Profile = new Profile
                {
                    Id = p.Id,
                    Name = p.Name,
                    SubscriptionId = p.SubscriptionId,
                    Protocol = p.Protocol,
                    Address = p.Address,
                    Port = p.Port,
                    Credential = new Secret(credential),
                    Flow = p.Flow,
                    VmessCipher = p.VmessCipher,
                    Transport = p.Transport,
                    Security = p.Security,
                    ChainVia = p.ChainVia,
                },
                RemovedByProvider = p.RemovedByProvider,
                Favorite = p.Favorite,
                Order = p.Order,
                LastTest = p.LastTest,
            });
        }

        var active = profiles.Any(p => p.Profile.Id == dto.ActiveProfileId) ? dto.ActiveProfileId : null;
        return new AppState { Settings = dto.Settings, Subscriptions = subscriptions, Profiles = profiles, ActiveProfileId = active };
    }
}

internal sealed record StateDto
{
    public int Schema { get; init; }
    public AppSettings Settings { get; init; } = new();
    public Guid? ActiveProfileId { get; init; }
    public List<SubscriptionDto> Subscriptions { get; init; } = [];
    public List<StoredProfileDto> Profiles { get; init; } = [];
}

internal sealed record SubscriptionDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public int UpdateHours { get; init; } = 12;
    public UserAgentMode UserAgent { get; init; }
    public string? CustomUserAgent { get; init; }
    public bool SendHwid { get; init; }
    public bool FetchViaProxy { get; init; } = true;
    public SubscriptionInfo? Info { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public string? LastError { get; init; }
}

internal sealed record StoredProfileDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = "";
    public Guid? SubscriptionId { get; init; }
    public Protocol Protocol { get; init; }
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public VlessFlow Flow { get; init; }
    public string VmessCipher { get; init; } = "auto";
    public TransportSettings Transport { get; init; } = TransportSettings.Tcp;
    public SecuritySettings Security { get; init; } = SecuritySettings.None;
    public Guid? ChainVia { get; init; }
    public DateTimeOffset? RemovedByProvider { get; init; }
    public bool Favorite { get; init; }
    public int Order { get; init; }
    public Core.Testing.ServerTestResult? LastTest { get; init; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StateDto))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;
