using System.Net;
using System.Net.Sockets;
using Tropa.Core.Compatibility;
using Tropa.Core.Diagnostics;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Core.Security;
using Tropa.Core.Testing;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Diagnostics;
using Tropa.Infrastructure.Net;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.Storage;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Core.Telemetry;
using Tropa.Infrastructure.Telemetry;
using Tropa.Infrastructure.Testing;
using Tropa.Infrastructure.Updates;
using Tropa.Core.Updates;

namespace Tropa.Infrastructure;

public enum ConnectionState { Disconnected, Connecting, Connected, Disconnecting, Error }

public sealed record ConnectionStatus(ConnectionState State, string? Message = null, DateTimeOffset? Since = null);

/// <summary>Итог импорта для показа пользователю.</summary>
public sealed record ImportReport(int Added, int Updated, int Errors, int Skipped, IReadOnlyList<string> Messages);

/// <summary>Пути, с которыми работает движок. В тестах подменяются на временные.</summary>
public sealed record EnginePaths(string Roaming, string Local)
{
    public static EnginePaths Default() => new(AppPaths.Roaming, AppPaths.Local);

    public string GeoDirectory => Path.Combine(Local, "geo");
    public string RunDirectory => Path.Combine(Local, "run");
    public string JournalPath => Path.Combine(Local, "journal.json");
    public string LogDirectory => Path.Combine(Local, "logs");
    public string UpdatesDirectory => Path.Combine(Local, "updates");
}

/// <summary>
/// Движок Тропы для этапа 2: состояние, подписки, тесты, подключение в режиме «Только браузеры».
/// Интерфейс работает только через него. Все изменения системы идут через журнал и откатываются.
/// </summary>
public sealed partial class TropaEngine : IAsyncDisposable
{
    private readonly EnginePaths _paths;
    private readonly StateStore _store;
    private readonly SecretStore _secrets;
    private readonly SystemProxy _systemProxy;
    private CoreLocations _locations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;

    private readonly Func<CancellationToken, Task<ServiceClient?>> _connectService;
    private readonly Func<bool> _serviceInstalled;

    private CoreProcess? _core;
    private CoreProcess? _xray;
    private ServiceClient? _service;
    private bool _runningViaService;

    /// <summary>Переподключение через службу без команды Stop: старое ядро ещё может работать.</summary>
    private bool _servicePending;

    private readonly Queue<string> _recentLog = new();
    private CancellationTokenSource? _trafficCts;
    private int _mixedPort;
    private bool _systemProxyApplied;
    private bool _lastPlanNeedsXray;

    private TropaEngine(EnginePaths paths, StateStore store, LoadResult loaded, SystemProxy systemProxy, CoreLocations locations,
        Func<DateTimeOffset> now, Func<CancellationToken, Task<ServiceClient?>> connectService, Func<bool> serviceInstalled, TelemetryConfig telemetry)
    {
        _paths = paths;
        _store = store;
        _secrets = loaded.Secrets;
        _systemProxy = systemProxy;
        _locations = locations;
        _now = now;
        _connectService = connectService;
        _serviceInstalled = serviceInstalled;
        State = loaded.State;
        StartupWarning = loaded.Warning;
        Log += (_, line) => Remember(line);
        InitTelemetry(telemetry);
    }

    /// <summary>
    /// Подключение к службе по умолчанию: канал Tropa.Service.v1, а на другом конце должна быть
    /// Tropa.Service.exe из каталога программы.
    /// </summary>
    public static Func<CancellationToken, Task<ServiceClient?>> DefaultServiceConnector()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "Tropa.Service.exe");
#if DEBUG
        // Только для разработки: служба собирается в свой каталог.
        if (Environment.GetEnvironmentVariable("TROPA_DEV_SERVICE") is { Length: > 0 } dev)
            expected = dev;
#endif
        return ct => ServiceClient.TryConnectAsync(Ipc.IpcProtocol.PipeName, expected, TimeSpan.FromMilliseconds(500), ct);
    }

    /// <summary>
    /// Служба Тропы зарегистрирована в Windows. Раздел служб в реестре читается без прав администратора;
    /// так видно, что служба есть, но ещё не запустилась (сразу после входа в Windows).
    /// </summary>
    public static bool ServiceRegistered()
    {
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Ipc.IpcProtocol.ServiceName);
        return key is not null;
    }

    /// <summary>Почему режим «Весь компьютер» сейчас недоступен — для подсказок на Главной и после подключения.</summary>
    public string NoTunReason() => (ServiceAvailable, ServiceStarting) switch
    {
        (true, _) => "Служба Тропы работает без прав системы, поэтому режим «Весь компьютер» недоступен — переустановите Тропу.",
        (false, true) => "Служба Тропы установлена, но не отвечает (не запустилась или остановлена), поэтому режим «Весь компьютер» недоступен. Переподключитесь через минуту; если не поможет — перезагрузите компьютер или переустановите Тропу.",
        _ => "Служба Тропы не установлена, поэтому режим «Весь компьютер» недоступен — установите Тропу установщиком.",
    };

    /// <summary>Сколько ждать запуска установленной службы перед подключением.</summary>
    public static readonly TimeSpan ServiceStartWait = TimeSpan.FromSeconds(90);

    /// <summary>Служба установлена, но связи с ней нет — обычно она ещё запускается.</summary>
    public bool ServiceStarting => !ServiceAvailable && _serviceInstalled();

    /// <summary>
    /// Ждёт службу, если она установлена: после входа в Windows Тропа из автозагрузки стартует раньше
    /// службы, и без ожидания режим «Весь компьютер» молча заменился бы на «Только браузеры».
    /// </summary>
    public async Task<bool> WaitForServiceAsync(TimeSpan max, CancellationToken ct = default)
    {
        var until = _now() + max;
        while (true)
        {
            await AttachServiceAsync(ct).ConfigureAwait(false);
            if (ServiceAvailable || !_serviceInstalled() || _now() >= until)
                return ServiceAvailable;
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Служба Тропы подключена и может включить режим «Весь компьютер».</summary>
    public bool TunAvailable => _service is { IsConnected: true, TunSupported: true };

    /// <summary>Служба подключена (даже если без прав на TUN).</summary>
    public bool ServiceAvailable => _service is { IsConnected: true };

    public event EventHandler? ServiceChanged;

    /// <summary>Пытается подключиться к службе. Без неё Тропа работает в режиме «Только браузеры».</summary>
    public async Task AttachServiceAsync(CancellationToken ct = default)
    {
        if (ServiceAvailable)
            return;
        try
        {
            var client = await _connectService(ct).ConfigureAwait(false);
            if (client is null)
                return;
            client.Log += (_, line) => Log?.Invoke(this, line);
            client.Status += OnServiceStatus;
            client.Disconnected += OnServiceDisconnected;
            _service = client;
            Rec(Ev.ServiceAttached, ("tun", Flag(client.TunSupported)), ("cores", client.CoreVersions));
            // Узнаём, не держит ли служба блокировку после прошлого сеанса.
            try
            {
                await client.SendAsync(new Ipc.StatusRequest(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is ServiceException or TimeoutException)
            {
            }
        }
        catch (ServiceException ex)
        {
            StartupWarning = ex.Message;
        }

        ServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    public AppState State { get; private set; }

    /// <summary>Служба держит аварийную блокировку: ядро не работает, интернет мимо туннеля закрыт.</summary>
    public bool Blocking { get; private set; }

    /// <summary>Последние строки журнала (для отчёта диагностики), уже без секретов.</summary>
    public IReadOnlyList<string> RecentLog
    {
        get
        {
            lock (_recentLog)
                return [.. _recentLog];
        }
    }

    private void Remember(string line)
    {
        lock (_recentLog)
        {
            _recentLog.Enqueue($"{_now():HH:mm:ss} {Scrubber.Scrub(line)}");
            while (_recentLog.Count > 500)
                _recentLog.Dequeue();
        }
    }

    public ConnectionStatus Status { get; private set; } = new(ConnectionState.Disconnected);

    /// <summary>Предупреждение при загрузке (восстановление из резервной копии, откат после сбоя).</summary>
    public string? StartupWarning { get; private set; }

    public event EventHandler? StateChanged;

    public event EventHandler<ConnectionStatus>? StatusChanged;

    public event EventHandler<TrafficSample>? Traffic;

    /// <summary>Страна выхода по последней проверке; null — не подключено, проверка выключена или не удалась.</summary>
    public ExitRegion? ExitRegion { get; private set; }

    public event EventHandler? ExitRegionChanged;

    /// <summary>Как часто проверять страну выхода. Внутреннее — для тестов.</summary>
    internal TimeSpan ExitRegionInterval { get; set; } = TimeSpan.FromMinutes(1);

    private TaskCompletionSource _exitRegionNow = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Проверить страну выхода сейчас, не дожидаясь очередной минуты (правый клик по значку в трее).</summary>
    public void RefreshExitRegion() => _exitRegionNow.TrySetResult();

    public event EventHandler<string>? Log;

    public SecretScrubber Scrubber => new(_secrets.Values);

    public static TropaEngine Open(EnginePaths paths, IProxySettingsStore? proxyStore = null, CoreLocations? locations = null,
        Func<DateTimeOffset>? now = null, Func<CancellationToken, Task<ServiceClient?>>? connectService = null,
        Func<bool>? serviceInstalled = null, TelemetryConfig? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.Roaming);
        Directory.CreateDirectory(paths.Local);
        var store = new StateStore(paths.Roaming);
        var loaded = store.Load();
        var journal = new ChangeJournal(paths.JournalPath);
        var systemProxy = new SystemProxy(proxyStore ?? new RegistryProxySettingsStore(), journal);
        // Установленное обновление ядер (если есть и подпись верна) новее файлов программы.
        var effectiveLocations = locations ?? new UpdateStore(paths.UpdatesDirectory, UpdateConfig.Embedded).Load(CoreLocations.Default());
        var engine = new TropaEngine(paths, store, loaded, systemProxy, effectiveLocations, now ?? (() => DateTimeOffset.Now),
            connectService ?? (_ => Task.FromResult<ServiceClient?>(null)),
            serviceInstalled ?? (connectService is null ? () => false : ServiceRegistered), telemetry ?? TelemetryConfig.Embedded);

        // Откат после сбоя: если в журнале остались наши изменения, возвращаем систему в исходное состояние.
        if (systemProxy.IsAppliedByUs)
        {
            systemProxy.Restore();
            engine.StartupWarning = (engine.StartupWarning is null ? "" : engine.StartupWarning + " ")
                + "Тропа в прошлый раз завершилась аварийно — настройки прокси Windows восстановлены.";
        }

        engine.EnsureLocalAuth();
        return engine;
    }

    private void EnsureLocalAuth()
    {
        if (_secrets.Get(StateStore.LocalUserKey) is null || _secrets.Get(StateStore.LocalPasswordKey) is null)
        {
            _secrets.Set(StateStore.LocalUserKey, "tropa-" + SecretStore.NewRandomToken(4));
            _secrets.Set(StateStore.LocalPasswordKey, SecretStore.NewRandomToken());
            Save();
        }
    }

    private LocalAuth Auth => new(_secrets.Get(StateStore.LocalUserKey)!, _secrets.Get(StateStore.LocalPasswordKey)!);

    // ---------------- Состояние ----------------

    private void Update(Func<AppState, AppState> change)
    {
        var before = State.Settings;
        State = change(State);
        Save();
        if (!ReferenceEquals(before, State.Settings))
            OnSettingsChangedForTelemetry(before, State.Settings);
        StateChanged?.Invoke(this, EventArgs.Empty);
        SchedulePublish();
    }

    private void Save() => _store.Save(State, _secrets, _now());

    public void UpdateSettings(Func<AppSettings, AppSettings> change) =>
        Update(s => s with { Settings = change(s.Settings) });

    public async Task SetActiveAsync(Guid profileId, CancellationToken ct = default)
    {
        if (State.Profiles.All(p => p.Profile.Id != profileId || p.Hidden))
            return;
        Update(s => s with { ActiveProfileId = profileId });
        Rec(Ev.ActiveServer, ServerFields(State.ActiveProfile));
        if (Status.State == ConnectionState.Connected)
            await ReconnectAsync("server", ct).ConfigureAwait(false);
    }

    /// <summary>Добавляет или заменяет сервер. Если он сейчас используется — переподключение.</summary>
    public async Task SaveProfileAsync(Profile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var existed = State.Profiles.Any(p => p.Profile.Id == profile.Id);
        Update(s => s with
        {
            Profiles = existed
                ? s.Profiles.Select(p => p.Profile.Id == profile.Id ? p with { Profile = profile, LastTest = null } : p).ToList()
                : [.. s.Profiles, new StoredProfile { Profile = profile, Order = s.Profiles.Count }],
            ActiveProfileId = s.ActiveProfileId ?? profile.Id,
        });
        Rec(Ev.ServerSaved, [("new", Flag(!existed)), .. ServerFields(profile)]);
        if (Status.State == ConnectionState.Connected && State.ActiveProfileId == profile.Id)
            await ReconnectAsync("server-edit", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Удаляет серверы. Свои — стираются; из подписки — скрываются, иначе при обновлении вернутся.
    /// Если удалён выбранный сервер — выбирается первый оставшийся видимый.
    /// </summary>
    public void RemoveProfiles(IReadOnlyCollection<Guid> profileIds)
    {
        ArgumentNullException.ThrowIfNull(profileIds);
        Rec(Ev.ServersRemoved, ("n", Num(profileIds.Count)));
        Update(s =>
        {
            var ids = profileIds.ToHashSet();
            var profiles = s.Profiles
                .Where(p => !(ids.Contains(p.Profile.Id) && p.Profile.SubscriptionId is null))
                // Цепочки через удалённый сервер больше не работают — разрываем их, а не оставляем битую ссылку.
                .Select(p => p.Profile.ChainVia is { } via && ids.Contains(via) && s.Profiles.Any(x => x.Profile.Id == via && x.Profile.SubscriptionId is null)
                    ? p with { Profile = p.Profile with { ChainVia = null } }
                    : p)
                .Select(p => ids.Contains(p.Profile.Id) ? p with { Hidden = true } : p)
                .ToList();
            return s with { Profiles = profiles, ActiveProfileId = KeepActive(profiles, s.ActiveProfileId) };
        });
    }

    public void RemoveProfile(Guid profileId) => RemoveProfiles([profileId]);

    /// <summary>Вернуть серверы подписки, скрытые пользователем.</summary>
    public void RestoreHidden(Guid? subscriptionId) =>
        Update(s => s with
        {
            Profiles = s.Profiles.Select(p => p.Hidden && (subscriptionId is null || p.Profile.SubscriptionId == subscriptionId) ? p with { Hidden = false } : p).ToList(),
        });

    public void RemoveSubscription(Guid subscriptionId)
    {
        Rec(Ev.SubscriptionRemoved, ("servers", Num(State.Profiles.Count(p => p.Profile.SubscriptionId == subscriptionId))));
        Update(s =>
        {
            var profiles = s.Profiles.Where(p => p.Profile.SubscriptionId != subscriptionId).ToList();
            return s with
            {
                Subscriptions = s.Subscriptions.Where(x => x.Id != subscriptionId).ToList(),
                Profiles = profiles,
                ActiveProfileId = KeepActive(profiles, s.ActiveProfileId),
            };
        });
    }

    /// <summary>Сдвигает подписку на одну позицию (delta = −1 — левее, +1 — правее).</summary>
    public void MoveSubscription(Guid subscriptionId, int delta) =>
        Update(s =>
        {
            var list = s.Subscriptions.ToList();
            var i = list.FindIndex(x => x.Id == subscriptionId);
            var j = i + Math.Sign(delta);
            if (i < 0 || j < 0 || j >= list.Count)
                return s;
            (list[i], list[j]) = (list[j], list[i]);
            return s with { Subscriptions = list };
        });

    /// <summary>
    /// Меняет местами сервер с соседом в списке, который видит пользователь (вкладка «Все», подписка или «Мои»):
    /// <paramref name="visibleOrder"/> — Id серверов в порядке на экране.
    /// </summary>
    public void MoveProfile(Guid profileId, int delta, IReadOnlyList<Guid> visibleOrder)
    {
        ArgumentNullException.ThrowIfNull(visibleOrder);
        var i = visibleOrder.ToList().IndexOf(profileId);
        var j = i + Math.Sign(delta);
        if (i < 0 || j < 0 || j >= visibleOrder.Count)
            return;
        var other = visibleOrder[j];
        Update(s =>
        {
            var ordered = Renumber(s.Profiles);
            var a = ordered.First(p => p.Profile.Id == profileId).Order;
            var b = ordered.First(p => p.Profile.Id == other).Order;
            return s with
            {
                Profiles = ordered.Select(p => p.Profile.Id == profileId ? p with { Order = b } : p.Profile.Id == other ? p with { Order = a } : p).ToList(),
            };
        });
    }

    /// <summary>Порядок 0, 1, 2… без повторов: у подписок и своих серверов номера могли пересекаться.</summary>
    private static List<StoredProfile> Renumber(IEnumerable<StoredProfile> profiles) =>
        profiles.Select((p, i) => (p, i)).OrderBy(x => x.p.Order).ThenBy(x => x.i).Select((x, n) => x.p with { Order = n }).ToList();

    private static Guid? KeepActive(IReadOnlyList<StoredProfile> profiles, Guid? active) =>
        profiles.Any(p => p.Profile.Id == active && !p.Hidden)
            ? active
            : profiles.Where(p => !p.Hidden && p.RemovedByProvider is null).OrderBy(p => p.Order).FirstOrDefault()?.Profile.Id;

    // ---------------- Импорт ----------------

    /// <summary>
    /// Импорт из буфера обмена или поля ввода: одна строка https://… — подписка;
    /// иначе разбираем как список ссылок, base64 или JSON и добавляем серверы как «Мои».
    /// </summary>
    public async Task<ImportReport> ImportTextAsync(string text, string? subscriptionName, CancellationToken ct = default)
    {
        var trimmed = (text ?? "").Trim();
        if (IsSubscriptionUrl(trimmed))
            return await AddSubscriptionAsync(trimmed, subscriptionName, ct).ConfigureAwait(false);

        var parsed = SubscriptionParser.Parse(trimmed);
        var existing = State.Profiles.Where(p => p.Profile.SubscriptionId is null).Select(p => p.Profile.IdentityKey).ToHashSet(StringComparer.Ordinal);
        var fresh = parsed.Profiles.Select(p => p.Profile).Where(p => !existing.Contains(p.IdentityKey)).ToList();
        var order = State.Profiles.Count;
        Update(s => s with
        {
            Profiles = [.. s.Profiles, .. fresh.Select(p => new StoredProfile { Profile = p with { SubscriptionId = null }, Order = order++ })],
            ActiveProfileId = s.ActiveProfileId ?? fresh.FirstOrDefault()?.Id,
        });

        var messages = parsed.Errors.Select(e => $"Строка {e.Position}: {e.Message}")
            .Concat(parsed.Profiles.SelectMany(p => p.Warnings.Select(w => $"{p.Profile.Name}: {w}")))
            .ToList();
        if (parsed.Profiles.Count == 0 && parsed.Errors.Count == 0)
            messages.Add("Не найдено ни одной ссылки vless://, vmess://, trojan://, ss:// или hysteria2://.");
        Rec(Ev.Import, ("added", Num(fresh.Count)), ("found", Num(parsed.Profiles.Count)), ("errors", Num(parsed.Errors.Count)),
            ("skipped", Num(parsed.SkippedUnsupported)), ("protocols", string.Join(",", fresh.Select(p => p.Protocol).Distinct())));
        return new ImportReport(fresh.Count, 0, parsed.Errors.Count, parsed.SkippedUnsupported, messages);
    }

    public static bool IsSubscriptionUrl(string text) =>
        !text.Contains('\n', StringComparison.Ordinal)
        && (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        && Uri.TryCreate(text, UriKind.Absolute, out _);

    public async Task<ImportReport> AddSubscriptionAsync(string url, string? name, CancellationToken ct = default)
    {
        var existing = State.Subscriptions.FirstOrDefault(s => s.Url.Reveal() == url);
        if (existing is not null)
            return await UpdateSubscriptionAsync(existing.Id, ct).ConfigureAwait(false);

        var defaults = State.Settings.Subscriptions;
        var sub = new Subscription
        {
            Name = string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : NameSanitizer.Sanitize(name, new Uri(url).Host),
            Url = new Secret(url),
            UpdateHours = defaults.UpdateHours,
            UserAgent = defaults.SubUA,
            CustomUserAgent = defaults.SubUACustom,
            SendHwid = defaults.Hwid,
            FetchViaProxy = defaults.SubViaProxy,
        };
        Update(s => s with { Subscriptions = [.. s.Subscriptions, sub] });
        Rec(Ev.SubscriptionAdded, ("ua", sub.UserAgent.ToString()), ("viaProxy", Flag(sub.FetchViaProxy)), ("hwid", Flag(sub.SendHwid)));
        return await UpdateSubscriptionAsync(sub.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Автообновление подписок (subUpdate): обновляет те, что старше своего интервала — частоты администратора
    /// подписки или общей настройки (null — только вручную). Ошибка одной подписки не мешает остальным.
    /// </summary>
    public async Task UpdateDueSubscriptionsAsync(CancellationToken ct = default)
    {
        var defaults = State.Settings.Subscriptions;
        foreach (var sub in State.Subscriptions.Where(s => s.RefreshInterval(defaults) is { } every && (s.LastUpdated is null || _now() - s.LastUpdated >= every)).ToList())
        {
            try
            {
                await UpdateSubscriptionAsync(sub.Id, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or InvalidDataException)
            {
                Log?.Invoke(this, $"Автообновление подписки «{sub.Name}» не удалось: " + Scrubber.Scrub(ex.Message));
            }
        }
    }

    public async Task<ImportReport> UpdateSubscriptionAsync(Guid subscriptionId, CancellationToken ct = default)
    {
        var sub = State.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)
            ?? throw new InvalidOperationException("Подписка не найдена.");
        var proxy = LocalProxyIfConnected();

        FetchResult fetched;
        try
        {
            fetched = await SubscriptionFetcher.FetchAsync(sub, proxy, ct).ConfigureAwait(false);
        }
        catch (SubscriptionFetchException ex)
        {
            ReplaceSubscription(sub with { LastError = ex.Message });
            RecUrgent(Ev.SubscriptionUpdated, ("ok", "0"), ("error", NetLogClassifier.Anonymize(Scrubber.Scrub(ex.Message))),
                ("viaProxyAvailable", Flag(proxy is not null)));
            return new ImportReport(0, 0, 1, 0, [ex.Message]);
        }

        // Подписка от администратора (ADR-048): серверы плюс выбор сервера и правила.
        var admin = AdminDocument.Looks(fetched.Body) ? AdminDocument.TryParse(fetched.Body) : null;
        var parsed = admin?.Servers ?? SubscriptionParser.Parse(fetched.Body);
        var info = SubscriptionHeaders.Parse(fetched.Headers);
        var merged = SubscriptionMerger.Merge(sub.Id, State.Profiles, parsed.Profiles.Select(p => p.Profile).ToList(), _now());

        var updatedSub = sub with
        {
            Info = info,
            LastUpdated = _now(),
            LastError = parsed.Profiles.Count == 0 ? "В подписке не найдено серверов. Попробуйте «Представляться как v2rayN»." : null,
            Name = sub.Name == new Uri(sub.Url.Reveal()).Host && (admin?.Channel.Title ?? info.Title) is { } title ? title : sub.Name,
            UpdateHours = info.UpdateIntervalHours ?? sub.UpdateHours,
            Admin = admin?.Channel,
        };
        var adminRevisionChanged = sub.TrustAdmin && admin?.Channel.Revision != sub.Admin?.Revision;
        Update(s => s with
        {
            Subscriptions = s.Subscriptions.Select(x => x.Id == sub.Id ? updatedSub : x).ToList(),
            Profiles = Renumber(merged.Profiles),
            ActiveProfileId = merged.Profiles.Any(p => p.Profile.Id == s.ActiveProfileId)
                ? s.ActiveProfileId
                : merged.Profiles.Where(p => p.Profile.SubscriptionId == sub.Id && p.RemovedByProvider is null && !p.Hidden)
                    .OrderBy(p => p.Order).FirstOrDefault()?.Profile.Id,
        });

        Rec(Ev.SubscriptionUpdated, ("ok", Flag(parsed.Profiles.Count > 0)), ("servers", Num(parsed.Profiles.Count)), ("added", Num(merged.Added)),
            ("updated", Num(merged.Updated)), ("errors", Num(parsed.Errors.Count)), ("skipped", Num(parsed.SkippedUnsupported)),
            ("viaProxy", Flag(fetched.ViaProxy)), ("admin", Flag(admin is not null)), ("trustAdmin", Flag(sub.TrustAdmin)));

        // Администратор выбрал другой сервер или поменял правила — применяем сразу (если ему доверяют).
        var activeChanged = ApplyAdminActive(sub.Id);
        if ((activeChanged || adminRevisionChanged) && Status.State == ConnectionState.Connected)
        {
            try
            {
                await ReconnectAsync("admin", ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is CoreStartException or IntegrityException or UnsupportedProfileException or InvalidOperationException or IOException)
            {
                Log?.Invoke(this, "Не удалось применить изменения администратора: " + Scrubber.Scrub(ex.Message));
            }
        }

        var messages = parsed.Errors.Select(e => $"Сервер {e.Position}: {e.Message}").ToList();
        if (admin is not null)
            messages.AddRange(admin.Warnings);
        if (admin is not null && !updatedSub.TrustAdmin && (admin.Channel.ActiveServer is not null || admin.Channel.Rules.Count > 0))
            messages.Add($"Администратор подписки присылает выбор сервера и правил: {admin.Channel.Rules.Count}. Чтобы они применялись, нажмите «Доверять администратору» на вкладке подписки.");
        if (updatedSub.LastError is { } err)
            messages.Insert(0, err);
        if (fetched.ViaProxy)
            messages.Insert(0, "Адрес подписки недоступен напрямую — загружено через сервер.");
        return new ImportReport(merged.Added, merged.Updated, parsed.Errors.Count, parsed.SkippedUnsupported, messages);
    }

    /// <summary>
    /// После подключения: подписка активного сервера старше 5 минут — обновить в фоне (теперь можно и через
    /// сервер). Так новый сервер или выбор администратора доходит сразу, а не по таймеру.
    /// </summary>
    private void RefreshActiveSubscriptionSoon()
    {
        if (State.ActiveProfile?.SubscriptionId is not { } subId
            || State.Subscriptions.FirstOrDefault(s => s.Id == subId) is not { } sub
            || (sub.LastUpdated is { } at && _now() - at < TimeSpan.FromMinutes(5)))
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await UpdateSubscriptionAsync(subId).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or InvalidDataException)
            {
                Log?.Invoke(this, $"Обновление подписки «{sub.Name}» после подключения не удалось: " + Scrubber.Scrub(ex.Message));
            }
        });
    }

    private void ReplaceSubscription(Subscription sub) =>
        Update(s => s with { Subscriptions = s.Subscriptions.Select(x => x.Id == sub.Id ? sub : x).ToList() });

    // ---------------- Тесты ----------------

    /// <summary>Ядро сервера: выбор пользователя, иначе найденное проверкой; Auto — не определено.</summary>
    private CoreChoice CoreFor(Guid id) => State.Profiles.FirstOrDefault(p => p.Profile.Id == id)?.EffectiveCore ?? CoreChoice.Auto;

    /// <summary>Пользователь выбрал ядро для сервера. Если сервер сейчас подключён — переподключаемся.</summary>
    public async Task SetServerCoreAsync(Guid profileId, CoreChoice core, CancellationToken ct = default)
    {
        Update(s => s with { Profiles = s.Profiles.Select(p => p.Profile.Id == profileId ? p with { Core = core } : p).ToList() });
        Rec(Ev.ServerCore, ("core", core.ToString()), ("active", Flag(State.ActiveProfileId == profileId)));
        if (State.ActiveProfileId == profileId && Status.State == ConnectionState.Connected)
            await ReconnectAsync("server-core", ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Проверяет серверы и сохраняет результаты: они видны в списке.
    /// Для серверов с ядром «Автоматически» сначала проверяется Xray, а неработающие — ещё раз
    /// через sing-box; на каком ядре сервер заработал, то и запоминается.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, ServerTestResult>> TestAsync(
        IReadOnlyList<Profile> profiles, TestKinds kinds = TestKinds.Standard, IProgress<TestProgress>? progress = null,
        TesterOptions? options = null, AppSettings? settingsOverride = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var settings = settingsOverride ?? State.Settings;
        var all = State.Profiles.Select(p => p.Profile).ToList();
        Task<IReadOnlyDictionary<Guid, ServerTestResult>> Run(IReadOnlyList<Profile> list, CoreChoice core, IProgress<TestProgress>? report) =>
            list.Count == 0
                ? Task.FromResult<IReadOnlyDictionary<Guid, ServerTestResult>>(new Dictionary<Guid, ServerTestResult>())
                : ServerTester.TestAsync(list, all, settings with { Cores = settings.Cores with { CoreChoice = core } }, kinds,
                    _locations, _paths.RunDirectory, Scrubber, line => Log?.Invoke(this, line), report, ct, options);

        // Определять ядро есть смысл, только когда проверяется реальная задержка.
        var detect = kinds.HasFlag(TestKinds.Delay) && settings.Cores.CoreChoice == CoreChoice.Auto;
        var results = new Dictionary<Guid, ServerTestResult>();
        var detected = new Dictionary<Guid, CoreChoice?>();
        var groups = profiles.GroupBy(KeyFor);

        foreach (var g in groups)
        {
            if (g.Key != CoreChoice.Auto)
            {
                foreach (var (id, r) in await Run(g.ToList(), g.Key, progress).ConfigureAwait(false))
                    results[id] = r;
                continue;
            }

            // Автоматически: сначала Xray, затем неработающие — через sing-box.
            var first = await Run(g.ToList(), CoreChoice.Xray, progress).ConfigureAwait(false);
            var retry = g.Where(p => !Works(first.GetValueOrDefault(p.Id))).ToList();
            var second = await Run(retry, CoreChoice.SingBox, progress).ConfigureAwait(false);
            foreach (var p in g)
            {
                var x = first.GetValueOrDefault(p.Id);
                var s = second.GetValueOrDefault(p.Id);
                if (Works(x))
                    (results[p.Id], detected[p.Id]) = (x!, CoreChoice.Xray);
                else if (Works(s))
                    (results[p.Id], detected[p.Id]) = (s!, CoreChoice.SingBox);
                else
                {
                    results[p.Id] = s ?? x!;
                    detected[p.Id] = null;
                }

                progress?.Report(new TestProgress(p.Id, results[p.Id]));
            }
        }

        Update(s => s with
        {
            Profiles = s.Profiles.Select(sp => results.TryGetValue(sp.Profile.Id, out var r)
                ? sp with
                {
                    LastTest = MergeResult(sp.LastTest, r, kinds),
                    DetectedCore = detected.TryGetValue(sp.Profile.Id, out var d) ? d : sp.DetectedCore,
                }
                : sp).ToList(),
        });
        Rec(Ev.ServerTest, ("kinds", kinds.ToString()), ("n", Num(results.Count)),
            ("results", string.Join("; ", profiles.Take(50).Where(p => results.ContainsKey(p.Id)).Select(p =>
            {
                var r = results[p.Id];
                var core = detected.TryGetValue(p.Id, out var d) ? d?.ToString() ?? "none" : KeyFor(p).ToString();
                return $"{p.Name} [{p.Protocol}/{p.Transport.Type}/{p.Security.Type}]: {ServerHealth.Classify(r)} {r.DelayMs?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}ms {core}"
                    + (r.Error is { } e ? " — " + NetLogClassifier.Template(e) : "");
            }))));
        return results;

        static bool Works(ServerTestResult? r) => ServerHealth.Classify(r) is HealthStatus.Working or HealthStatus.Slow;

        // На каком ядре проверять: Auto — определить (сначала Xray, потом sing-box).
        CoreChoice KeyFor(Profile p)
        {
            if (p.Protocol == Protocol.Hysteria2 || (CorePlan.XrayRefuses(p) && p.Transport.Type != TransportType.Xhttp))
                return CoreChoice.SingBox;
            if (p.Transport.Type == TransportType.Xhttp)
                return CoreChoice.Xray;
            var stored = State.Profiles.FirstOrDefault(sp => sp.Profile.Id == p.Id);
            if (stored?.Core is { } chosen && chosen != CoreChoice.Auto)
                return chosen;
            if (detect)
                return CoreChoice.Auto;
            var known = stored?.DetectedCore ?? settings.Cores.CoreChoice;
            return known == CoreChoice.Auto ? CoreChoice.SingBox : known;
        }
    }

    /// <summary>
    /// Проверка стабильности не заменяет основной замер, а дополняет его; основной замер
    /// сохраняет прежние данные о стабильности.
    /// </summary>
    private static ServerTestResult MergeResult(ServerTestResult? old, ServerTestResult fresh, TestKinds kinds)
    {
        if (old is null)
            return fresh;
        if (kinds == TestKinds.Stability)
            return old with { Loss = fresh.Loss, JitterMs = fresh.JitterMs, MedianMs = fresh.MedianMs, Error = fresh.Error ?? old.Error };
        return fresh.Loss is null ? fresh with { Loss = old.Loss, JitterMs = old.JitterMs, MedianMs = old.MedianMs } : fresh;
    }

    // ---------------- Подключение ----------------

    public Task ConnectAsync(CancellationToken ct = default) => ConnectAsync(null, ct);

    /// <param name="source">Откуда нажали «Подключить» (home, tray, hotkey, autostart…) — для журнала разработчика.</param>
    /// <param name="ct">Отмена.</param>
    public async Task ConnectAsync(string? source, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var started = _now();
        try
        {
            if (Status.State == ConnectionState.Connected)
                return;
            SetStatus(new ConnectionStatus(ConnectionState.Connecting));
            await AttachServiceAsync(ct).ConfigureAwait(false);
            Rec(Ev.ConnectStart, [
                ("src", source ?? "?"),
                ("mode", State.Settings.Connection.Mode.ToString()),
                ("effMode", State.Settings.Connection.Mode == CaptureMode.Tun && !TunAvailable ? CaptureMode.SystemProxy.ToString() : State.Settings.Connection.Mode.ToString()),
                ("service", ServiceAvailable ? (TunAvailable ? "tun" : "no-tun") : ServiceStarting ? "starting" : "none"),
                ("killSwitch", Flag(State.Settings.General.KillSwitch)),
                ("route", State.Settings.Routing.Preset.ToString()),
                .. ServerFields(State.ActiveProfile)]);
            if (State.Settings.Connection.Mode == CaptureMode.Tun && ServiceStarting)
            {
                SetStatus(new ConnectionStatus(ConnectionState.Connecting, "Жду, пока запустится служба Тропы (так бывает сразу после входа в Windows)…"));
                var waitFrom = _now();
                var ok = await WaitForServiceAsync(ServiceStartWait, ct).ConfigureAwait(false);
                Rec(Ev.ServiceWait, ("ms", Ms(_now() - waitFrom)), ("ok", Flag(ok)));
            }

            // Ядро для сервера ещё не определено — быстро проверяем: сначала Xray, потом sing-box.
            if (State.ActiveProfile is { } active && CoreFor(active.Id) == CoreChoice.Auto
                && active.Protocol != Protocol.Hysteria2 && active.Transport.Type != TransportType.Xhttp
                && State.Settings.Cores.CoreChoice == CoreChoice.Auto)
            {
                try
                {
                    await TestAsync([active], TestKinds.Delay, ct: ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException or UnsupportedProfileException)
                {
                    // Проверка не обязательна: не удалась — подключаемся по общей настройке ядра.
                    Log?.Invoke(this, "Не удалось определить ядро для сервера: " + Scrubber.Scrub(ex.Message));
                }
            }

            await StartCoreAsync(ct).ConfigureAwait(false);
            var note = State.Settings.Connection.Mode == CaptureMode.Tun && !TunAvailable
                ? NoTunReason() + " Сейчас через Тропу идут только браузеры и программы, использующие прокси Windows."
                : null;
            // Другой VPN рядом — частая причина «подключено, но ничего не открывается».
            if (ForeignVpnWarning() is { } foreign)
                note = note is null ? foreign : foreign + " " + note;
            SetStatus(new ConnectionStatus(ConnectionState.Connected, note, _now()));
            Rec(Ev.Connected, ("ms", Ms(_now() - started)), ("viaService", Flag(_runningViaService)), ("xray", Flag(_lastPlanNeedsXray)),
                ("note", note is null ? null : NetLogClassifier.Anonymize(note)));
            if (note is not null)
                RecordEnvironment("connected-with-note");
            RefreshActiveSubscriptionSoon();
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or UnsupportedProfileException or InvalidOperationException or IOException or ServiceException or TimeoutException)
        {
            // Переподключение без Stop не удалось: старое ядро службы останавливаем, но при включённом
            // kill switch блокировку не снимаем — служба держит её до «Отключить».
            _runningViaService |= _servicePending;
            _servicePending = false;
            await StopCoreAsync(sendStop: !State.Settings.General.KillSwitch).ConfigureAwait(false);
            SetStatus(new ConnectionStatus(ConnectionState.Error, Scrubber.Scrub(ex.Message)));
            RecUrgent(Ev.ConnectFailed, ("ms", Ms(_now() - started)), ("type", ex.GetType().Name), ("msg", NetLogClassifier.Anonymize(Scrubber.Scrub(ex.Message))),
                ("inner", ex.InnerException is { } inner ? NetLogClassifier.Anonymize(Scrubber.Scrub(inner.Message)) : null));
            // Подключение не удалось — что вокруг: чужой VPN, прокси Windows, адаптеры.
            RecordEnvironment("connect-failed");
        }
        finally
        {
            _servicePending = false;
            _gate.Release();
        }
    }

    // ---------------- Обновления ----------------

    /// <summary>Версии ядер, которые реально запускаются: у службы свои файлы.</summary>
    public string CoreVersions => _service is { IsConnected: true, CoreVersions: { } v } ? v : _locations.Versions;

    public static Version AppVersion => typeof(TropaEngine).Assembly.GetName().Version ?? new Version(0, 1, 0);

    public UpdateConfig Updates { get; init; } = UpdateConfig.Embedded;

    /// <summary>
    /// Через подключение, если оно есть и так задано в настройках (geoViaProxy): сервер обновлений
    /// на GitHub в России может открываться плохо. Иначе напрямую.
    /// </summary>
    private HttpClient UpdateHttp()
    {
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (Status.State == ConnectionState.Connected && State.Settings.Cores.GeoViaProxy && _mixedPort > 0)
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy($"socks5://127.0.0.1:{_mixedPort}") { Credentials = new NetworkCredential(Auth.Username, Auth.Password) };
        }
        else
        {
            handler.UseProxy = false;
        }

        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Tropa/" + AppVersion.ToString(3));
        return http;
    }

    private CoreLocations CurrentLocations => _service is { IsConnected: true } service
        ? _locations with { Sequence = service.UpdateSequence }
        : _locations;

    /// <summary>Проверка обновлений по подписанному манифесту. Ничего не скачивает, кроме манифеста.</summary>
    public async Task<UpdateCheck> CheckUpdatesAsync(CancellationToken ct = default)
    {
        using var http = UpdateHttp();
        var check = await new UpdateClient(http, Updates).CheckAsync(CurrentLocations, AppVersion,
            State.Settings.Cores.LastManifestSequence, _now(), ct).ConfigureAwait(false);
        UpdateSettings(s => s with { Cores = s.Cores with { LastManifestSequence = check.Manifest.Manifest.Sequence, LastUpdateCheck = _now() } });
        Rec(Ev.UpdateCheck, ("sequence", check.Manifest.Manifest.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return check;
    }

    /// <summary>
    /// Скачивает и устанавливает новые ядра и наборы правил. Со службой — её каталог (она проверяет всё
    /// заново), без службы — свой. Новые версии начинают работать со следующего подключения.
    /// </summary>
    public async Task InstallCoreUpdateAsync(UpdateCheck check, IProgress<string>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(check);
        var m = check.Manifest;
        var work = Path.Combine(_paths.Local, "update-download", m.Manifest.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
            using (var http = UpdateHttp())
                await new UpdateClient(http, Updates).DownloadFilesAsync(m.Manifest, CurrentLocations, work, progress, ct).ConfigureAwait(false);

            progress?.Report("Устанавливаю…");
            await AttachServiceAsync(ct).ConfigureAwait(false);
            if (_service is { IsConnected: true } service)
            {
                var reply = await service.SendAsync(new Ipc.InstallUpdateRequest(Convert.ToBase64String(m.Bytes), Convert.ToBase64String(m.Signature), work), ct).ConfigureAwait(false);
                if (!reply.Ok)
                    throw new ServiceException(reply.Error ?? "Служба не установила обновление.");
                // Версии у службы поменялись — переподключаемся к ней, чтобы узнать новые.
                await ReattachServiceAsync(ct).ConfigureAwait(false);
            }

            // Свой каталог обновляем всегда: без службы (или если её удалят) работают эти файлы.
            if (m.Manifest.Sequence > _locations.Sequence)
                _locations = new UpdateStore(_paths.UpdatesDirectory, Updates).Install(m.Bytes, m.Signature, work, _locations);
            Log?.Invoke(this, "Обновление установлено: " + CoreVersions);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        ServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReattachServiceAsync(CancellationToken ct)
    {
        if (_runningViaService || _service is null)
            return; // во время подключения связь не рвём: новые версии узнаем при следующем запуске
        var old = _service;
        old.Disconnected -= OnServiceDisconnected;
        _service = null;
        await old.DisposeAsync().ConfigureAwait(false);
        await AttachServiceAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Скачивает установщик новой версии (SHA-256 сверяется с манифестом) и возвращает путь к нему.</summary>
    public async Task<string> DownloadAppUpdateAsync(AppRelease app, CancellationToken ct = default)
    {
        using var http = UpdateHttp();
        var path = await new UpdateClient(http, Updates).DownloadInstallerAsync(app, Path.Combine(_paths.Local, "update-download"), ct).ConfigureAwait(false);
        Rec(Ev.AppUpdateDownloaded, ("version", app.Version.ToString()));
        return path;
    }

    // ---------------- Переподключение после сна и смены сети ----------------

    private Timer? _watchTimer;
    private DateTimeOffset _lastTick;
    private string? _networkSignature;
    private int _watchBusy;

    /// <summary>
    /// reconnect: раз в 5 секунд смотрим, не было ли сна (часы прыгнули вперёд) и не сменилась ли
    /// физическая сеть (адаптеры со шлюзом, кроме нашего TUN). Если подключено — переподключаемся.
    /// </summary>
    public void StartNetworkWatch()
    {
        _lastTick = _now();
        _networkSignature = NetworkSignature();
        _watchTimer ??= new Timer(_ => _ = WatchTickAsync(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private async Task WatchTickAsync()
    {
        if (Interlocked.Exchange(ref _watchBusy, 1) == 1)
            return;
        try
        {
            var now = _now();
            var slept = now - _lastTick > TimeSpan.FromSeconds(30);
            _lastTick = now;
            var signature = NetworkSignature();
            var changed = signature != _networkSignature;
            _networkSignature = signature;
            if (slept)
                Rec(Ev.WokeUp, ("connected", Flag(Status.State == ConnectionState.Connected)));
            if (changed)
            {
                Rec(Ev.NetworkChanged, ("connected", Flag(Status.State == ConnectionState.Connected)));
                RecordEnvironment("network-changed");
            }
            if (!(slept || changed) || !State.Settings.General.Reconnect || Status.State != ConnectionState.Connected)
                return;
            // Сеть после пробуждения поднимается не сразу.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Log?.Invoke(this, slept ? "Компьютер проснулся — переподключаюсь." : "Сеть сменилась — переподключаюсь.");
            await ReconnectAsync(slept ? "wake" : "network", CancellationToken.None).ConfigureAwait(false);
            _networkSignature = NetworkSignature();
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException or ObjectDisposedException)
        {
        }
        finally
        {
            Volatile.Write(ref _watchBusy, 0);
        }
    }

    internal static string NetworkSignature()
    {
        var parts = new List<string>();
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                continue;
            var props = ni.GetIPProperties();
            var gateways = props.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any)).ToList();
            var addresses = props.UnicastAddresses.Select(u => u.Address).ToList();
            if (gateways.Count == 0 || addresses.Contains(Diagnosis.OwnTunAddress) || ni.Name.Equals(Diagnosis.OwnTunName, StringComparison.OrdinalIgnoreCase))
                continue;
            parts.Add(ni.Id + "=" + string.Join(",", addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).OrderBy(a => a.ToString(), StringComparer.Ordinal))
                + "@" + string.Join(",", gateways.OrderBy(a => a.ToString(), StringComparer.Ordinal)));
        }

        parts.Sort(StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    // ---------------- Просмотр конфига ----------------

    /// <summary>
    /// Конфиг sing-box для текущего сервера, как его увидит ядро, но без ключей (genConfig).
    /// Порты статистики и Xray — условные: настоящие выбираются при подключении.
    /// </summary>
    public string PreviewConfig()
    {
        var active = State.ActiveProfile ?? throw new InvalidOperationException("Сначала выберите сервер.");
        var settings = State.Settings with
        {
            Connection = State.Settings.Connection with { Mode = !TunAvailable && State.Settings.Connection.Mode == CaptureMode.Tun ? CaptureMode.SystemProxy : State.Settings.Connection.Mode },
            Routing = State.Settings.Routing with { Rules = [.. State.Settings.Routing.Rules, .. AdminRules()] },
        };
        var config = SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = settings,
            Active = active,
            Profiles = State.Profiles.Select(p => p.Profile).ToList(),
            RuleSetDirectory = _service?.RuleSetDirectory ?? _paths.GeoDirectory,
            LocalAuth = Auth,
            ClashApiPort = 9090,
            ClashApiSecret = "secret",
        });
        var scrubber = new SecretScrubber(_secrets.Values.Concat([Auth.Username, Auth.Password]));
        return scrubber.Scrub(config);
    }

    private readonly Dictionary<string, GeoListContent> _geoLists = new(StringComparer.Ordinal);

    /// <summary>Содержимое закреплённого набора правил без правок (ADR-039). Кэшируется до смены файлов.</summary>
    public async Task<GeoListContent> ReadGeoListAsync(string tag, CancellationToken ct = default)
    {
        var locations = _locations;
        var key = tag + "|" + (locations.Geo.Files.FirstOrDefault(f => f.Name == tag)?.Sha256 ?? "");
        lock (_geoLists)
        {
            if (_geoLists.TryGetValue(key, out var cached))
                return cached;
        }

        var content = await GeoListReader.ReadAsync(locations, tag, _paths.RunDirectory, ct).ConfigureAwait(false);
        lock (_geoLists)
            _geoLists[key] = content;
        return content;
    }

    /// <summary>Работает ли другой VPN или клиент обхода (v2rayN и т. п.): текст предупреждения или null.</summary>
    public string? ForeignVpnWarning()
    {
        try
        {
            return LocalNetwork.ForeignVpnWarning(_locations);
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---------------- Диагностика ----------------

    private DiagnosticsInput DiagnosticsInputNow() => new()
    {
        Settings = State.Settings,
        Active = State.ActiveProfile,
        Profiles = State.Profiles.Select(p => p.Profile).ToList(),
        Connected = Status.State == ConnectionState.Connected,
        Locations = _locations,
        RunDirectory = _paths.RunDirectory,
        Scrubber = Scrubber,
        Log = line => Log?.Invoke(this, line),
        SystemProxy = _systemProxy.Snapshot(),
        MixedPort = _mixedPort != 0 ? _mixedPort : State.Settings.Connection.SocksPort,
    };

    /// <summary>Десять шагов диагностики; текущее подключение не трогается.</summary>
    public async Task<IReadOnlyList<StepResult>> RunDiagnosticsAsync(IProgress<StepResult>? progress, CancellationToken ct = default)
    {
        var steps = await DiagnosticsRunner.RunAsync(DiagnosticsInputNow(), progress, ct).ConfigureAwait(false);
        // Ключ и итог шага, а для неудачных — обезличенные подробности.
        Rec(Ev.Diagnostics, steps.ToDictionary(st => st.Key,
            st => st.Status.ToString() + (st.Status.ToString() is "Ok" or "Skipped" || st.Details is null ? "" : " — " + NetLogClassifier.Anonymize(st.Details)),
            StringComparer.Ordinal));
        return steps;
    }

    /// <summary>Инструмент «Проверка домена»: ответ DNS провайдера и через туннель.</summary>
    public Task<DomainCheck> CheckDomainAsync(string domain, CancellationToken ct = default) =>
        DiagnosticsRunner.CheckDomainAsync(DiagnosticsInputNow(), domain, ct);

    /// <summary>Снять блокировку, которую служба держит после сбоя или аварийного закрытия Тропы.</summary>
    public async Task ReleaseBlockingAsync(CancellationToken ct = default)
    {
        await AttachServiceAsync(ct).ConfigureAwait(false);
        if (_service is { IsConnected: true } service)
            await service.SendAsync(new Ipc.StopRequest(), ct).ConfigureAwait(false);
        Rec(Ev.BlockingReleased);
        Blocking = false;
        ServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// «Аварийно вернуть настройки сети» (docs/02-security.md, §3.6): отключиться, вернуть прокси
    /// Windows и попросить службу откатить всё своё. Работает даже при сломанном состоянии.
    /// </summary>
    public async Task EmergencyRollbackAsync()
    {
        RecUrgent(Ev.EmergencyRollback, ("state", Status.State.ToString()), ("blocking", Flag(Blocking)));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _runningViaService = false;
            await StopCoreAsync().ConfigureAwait(false);
            _systemProxy.Restore();
            SetStatus(new ConnectionStatus(ConnectionState.Disconnected));
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            await AttachServiceAsync().ConfigureAwait(false);
            if (_service is { IsConnected: true } service)
                await service.SendAsync(new Ipc.RollbackAllRequest(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ServiceException or TimeoutException or IOException)
        {
            Log?.Invoke(this, "Служба не ответила на аварийный откат: " + ex.Message);
        }

        Blocking = false;
        ServiceChanged?.Invoke(this, EventArgs.Empty);
    }


    public async Task DisconnectAsync(string? source = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Rec(Ev.Disconnect, ("src", source ?? "?"), ("state", Status.State.ToString()),
                ("connectedMin", Status.Since is { } since ? ((long)(_now() - since).TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) : null));
            SetStatus(new ConnectionStatus(ConnectionState.Disconnecting));
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(new ConnectionStatus(ConnectionState.Disconnected));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Применяет изменённые настройки и правила: если подключено — переподключается.</summary>
    public async Task ApplyIfConnectedAsync(CancellationToken ct = default)
    {
        if (Status.State == ConnectionState.Connected)
            await ReconnectAsync("settings", ct).ConfigureAwait(false);
    }

    private async Task ReconnectAsync(string reason, CancellationToken ct)
    {
        Rec(Ev.Reconnect, ("reason", reason));
        // Через службу перезапуск — одной командой Start: служба сама остановит старое ядро
        // и не снимет блокировку kill switch между остановкой и запуском.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var viaService = _runningViaService;
            SetStatus(new ConnectionStatus(ConnectionState.Disconnecting));
            await StopCoreAsync(sendStop: !viaService).ConfigureAwait(false);
            _servicePending = viaService;
            SetStatus(new ConnectionStatus(ConnectionState.Disconnected));
        }
        finally
        {
            _gate.Release();
        }

        await ConnectAsync("reconnect:" + reason, ct).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        var active = State.ActiveProfile ?? throw new InvalidOperationException("Сначала добавьте и выберите сервер.");
        var settings = State.Settings with
        {
            Connection = State.Settings.Connection with
            {
                Mode = !TunAvailable && State.Settings.Connection.Mode == CaptureMode.Tun ? CaptureMode.SystemProxy : State.Settings.Connection.Mode,
            },
            // Правила администраторов подписок, которым доверяют, — после своих: свои главнее (ADR-048).
            Routing = State.Settings.Routing with { Rules = [.. State.Settings.Routing.Rules, .. AdminRules()] },
        };
        var effective = CompatRules.Evaluate(settings, active).Effective;

        // randomPorts: свободный случайный порт при каждом подключении — фиксированный легко найти сканированием.
        var previousPort = _mixedPort;
        _mixedPort = effective.Connection.RandomPorts ? ServerTester.FreePort() : effective.Connection.SocksPort;
        // Переподключение через службу: старое ядро ещё слушает этот порт — служба остановит его
        // перед запуском нового (одной командой, чтобы не снимать kill switch). Это не «чужая программа».
        var heldByOurOldCore = _servicePending && previousPort == _mixedPort;
        if (!heldByOurOldCore && !await WaitPortFreeAsync(_mixedPort, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"Порт {_mixedPort} занят другой программой. Закройте её или выберите другой порт в настройках.");

        // Прокси Windows, оставшийся от прошлого режима «Только браузеры», уводит браузеры и Discord во вход
        // с паролем — они не открывают ничего. В режиме «Только браузеры» его к тому же записали бы как исходный.
        if (!_systemProxyApplied && _systemProxy.DisableStaleOwn([_mixedPort, effective.Connection.SocksPort]) is { } stale)
        {
            Log?.Invoke(this, $"Выключен старый системный прокси Тропы ({stale}): он мешал браузерам и Discord.");
            Rec(Ev.StaleProxyDisabled, ("server", NetLogClassifier.Anonymize(stale)));
        }

        // Через службу наборы правил читает она — из своего защищённого каталога.
        var service = ServiceAvailable ? _service : null;
        var ruleSetDirectory = service?.RuleSetDirectory ?? _paths.GeoDirectory;
        if (service is null)
        {
            PinnedFiles.EnsureGeoInstalled(_locations.Geo, _locations.GeoSourceDirectory, _paths.GeoDirectory);
            PinnedFiles.VerifyGeo(_locations.Geo, _paths.GeoDirectory);
        }

        var clashPort = ServerTester.FreePort();
        var clashSecret = SecretStore.NewRandomToken();
        int? exitProbePort = !effective.General.HideExitRegion ? ServerTester.FreePort() : null;
        // Гибрид (docs/05-config-generation.md, §1): серверы с XHTTP и те, кому выбрано ядро Xray.
        var allProfiles = State.Profiles.Select(p => p.Profile).ToList();
        var plan = CorePlan.Make(settings, UsedProfiles(active, allProfiles, effective), CoreFor);
        _lastPlanNeedsXray = plan.NeedsXray;
        string? xrayConfig = null;
        var xrayPorts = new Dictionary<Guid, int>();
        LocalAuth? xrayAuth = null;
        if (plan.NeedsXray)
        {
            xrayAuth = new LocalAuth("xray-" + SecretStore.NewRandomToken(4), SecretStore.NewRandomToken());
            foreach (var p in plan.XrayProfiles)
                xrayPorts[p.Id] = ServerTester.FreePort();
            xrayConfig = XrayConfigBuilder.Build(plan.XrayProfiles.Select(p => new XrayTarget(p, xrayPorts[p.Id])).ToList(), settings, xrayAuth);
            var xrayViolations = XrayConfigGuard.Check(xrayConfig);
            if (xrayViolations.Count > 0)
                throw new IntegrityException("Конфиг Xray не прошёл проверку безопасности: " + xrayViolations[0]);
        }

        var config = SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = settings,
            Active = active,
            Profiles = allProfiles,
            RuleSetDirectory = ruleSetDirectory,
            LocalAuth = Auth,
            ClashApiPort = clashPort,
            ClashApiSecret = clashSecret,
            XrayPorts = xrayPorts,
            XrayAuth = xrayAuth,
            XrayPath = service?.XrayPath ?? CoreProcess.ExecutablePath(_locations, "xray"),
            ExitProbePort = exitProbePort,
        });
        var xrayReadiness = xrayPorts.Values.FirstOrDefault();

        var violations = ConfigGuard.CheckSingBox(config, new GuardPolicy
        {
            AllowedDirectories = service is null ? [_paths.GeoDirectory, _paths.RunDirectory] : [ruleSetDirectory],
            AllowLanInbound = effective.Connection.LanAllow,
        });
        if (violations.Count > 0)
            throw new IntegrityException("Конфиг не прошёл проверку безопасности: " + violations[0]);

        if (service is not null)
        {
            var reply = await service.SendAsync(new Ipc.StartRequest(
                config,
                effective.Connection.Mode switch
                {
                    CaptureMode.Tun => Ipc.CaptureModeDto.Tun,
                    CaptureMode.SystemProxy => Ipc.CaptureModeDto.SystemProxy,
                    _ => Ipc.CaptureModeDto.PortsOnly,
                },
                _mixedPort,
                effective.Connection.LanAllow,
                effective.Dns.SmartNameRes,
                xrayConfig,
                xrayReadiness,
                effective.General.KillSwitch,
                effective.Connection.LanBypass,
                effective.Connection.Mode == CaptureMode.SystemProxy ? effective.Connection.UwpLoopback : null), ct).ConfigureAwait(false);
            if (!reply.Ok)
                throw new ServiceException(reply.Error ?? "Служба отказалась запускать подключение.");
            _runningViaService = true;
        }
        else
        {
            if (xrayConfig is not null)
            {
                _xray = await CoreProcess.StartXrayAsync(_locations, xrayConfig, xrayReadiness, _paths.RunDirectory, Scrubber, line => Log?.Invoke(this, line), ct).ConfigureAwait(false);
                _xray.Exited += OnCoreExited;
            }

            _core = await CoreProcess.StartSingBoxAsync(_locations, config, _mixedPort, _paths.RunDirectory, Scrubber, line => Log?.Invoke(this, line), ct).ConfigureAwait(false);
            _core.Exited += OnCoreExited;
        }

        if (effective.Connection.Mode == CaptureMode.SystemProxy)
        {
            _systemProxy.Apply(_mixedPort, effective.Connection.SysBypass, _now());
            _systemProxyApplied = true;
        }

        _trafficCts = new CancellationTokenSource();
        _ = PumpTrafficAsync(new ClashApi(clashPort, clashSecret), _trafficCts.Token);
        _ = MonitorAsync(active.Id, _trafficCts.Token);
        if (exitProbePort is { } probe)
            _ = WatchExitRegionAsync(probe, _trafficCts.Token);
    }

    /// <summary>Серверы, которые попадут в конфиг: активный, промежуточные звенья цепочек и серверы из правил.</summary>
    private static List<Profile> UsedProfiles(Profile active, IReadOnlyList<Profile> all, AppSettings settings)
    {
        var used = new Dictionary<Guid, Profile>();
        void Add(Profile p)
        {
            if (!used.TryAdd(p.Id, p))
                return;
            if (p.ChainVia is { } via && all.FirstOrDefault(x => x.Id == via) is { } hop)
                Add(hop);
        }

        Add(active);
        foreach (var rule in settings.Routing.Rules.Where(r => r.Enabled))
        {
            foreach (var action in new[] { rule.Tcp, rule.Udp })
            {
                if (action.ServerId is { } id && all.FirstOrDefault(x => x.Id == id) is { } target)
                    Add(target);
            }
        }

        return [.. used.Values];
    }

    /// <summary>Интервал фоновой проверки текущего сервера. Внутреннее — для тестов.</summary>
    internal TimeSpan? MonitorIntervalOverride { get; set; }

    internal TesterOptions? MonitorTesterOptions { get; set; }

    /// <summary>
    /// Фоновая проверка (docs/07-testing-diagnostics.md, §1): «заморозку» замечает лишь тест скорости.
    /// Раз в 30 минут скачиваем 1 МБ через текущий сервер; если он «замёрз» или перестал отвечать —
    /// предупреждаем. Сервер сами не переключаем: авто-выбор убран (ADR-047).
    /// </summary>
    private async Task MonitorAsync(Guid activeId, CancellationToken ct)
    {
        var interval = MonitorIntervalOverride ?? TimeSpan.FromMinutes(30);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
                var active = State.Profiles.FirstOrDefault(p => p.Profile.Id == activeId)?.Profile;
                if (active is null || Status.State != ConnectionState.Connected)
                    return;

                var light = State.Settings with { Connection = State.Settings.Connection with { SpeedSizeMb = 1 } };
                var results = await TestAsync([active], TestKinds.Delay | TestKinds.Speed, null, MonitorTesterOptions, light, ct).ConfigureAwait(false);
                if (!results.TryGetValue(activeId, out var r) || !ServerHealth.ShouldExclude(r, _now()))
                    continue;

                RecUrgent(Ev.ServerUnhealthy, [("health", ServerHealth.Classify(r).ToString()), ("delayMs", r.DelayMs?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    ("speed", r.SpeedMbps?.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)), ("frozen", Flag(r.Frozen)),
                    ("error", r.Error is { } e ? NetLogClassifier.Template(e) : null), .. ServerFields(active)]);
                SetStatus(Status with
                {
                    Message = $"Сервер «{active.Name}»: {ServerHealth.Title(ServerHealth.Classify(r)).ToLowerInvariant()}. " + ServerHealth.Explain(r)
                        + " Выберите другой сервер на экране «Серверы».",
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException or InvalidOperationException)
        {
            Log?.Invoke(this, "Фоновая проверка сервера не удалась: " + Scrubber.Scrub(ex.Message));
        }
    }

    /// <summary>
    /// Раз в минуту (и по просьбе — RefreshExitRegion) спрашивает у Cloudflare (cdn-cgi/trace) страну выхода — через служебный вход ядра,
    /// который всегда ведёт в сервер. Уходит только запрос к Cloudflare с IP сервера, ничего о пользователе.
    /// Сервер может сам переключать выход внутри туннеля, поэтому страну проверяем по факту, а не по имени.
    /// </summary>
    private async Task WatchExitRegionAsync(int probePort, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                ExitRegion? region = null;
                try
                {
                    using var client = ServerTester.ProbeClient(probePort, Auth, TimeSpan.FromSeconds(15), allowRedirect: false);
                    var body = await client.GetStringAsync(new Uri(ExitRegion.TraceUrl), ct).ConfigureAwait(false);
                    region = ExitRegion.Parse(body.Length > 4096 ? body[..4096] : body, _now());
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // Сервер на минуту не ответил — оставляем прежнюю страну, если она есть; иначе прячем значок.
                    region = ExitRegion is { } prev && _now() - prev.CheckedAt < ExitRegionInterval * 3 ? prev : null;
                }

                SetExitRegion(region);
                // Ручные просьбы, пришедшие во время проверки, уже учтены ею.
                _exitRegionNow = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await Task.WhenAny(Task.Delay(ExitRegionInterval, ct), _exitRegionNow.Task).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetExitRegion(ExitRegion? region)
    {
        if (ExitRegion == region)
            return;
        if (region?.CountryCode != ExitRegion?.CountryCode)
            Rec(Ev.ExitRegion, ("country", region?.CountryCode ?? "нет"));
        ExitRegion = region;
        ExitRegionChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task PumpTrafficAsync(ClashApi api, CancellationToken ct)
    {
        using (api)
        {
            try
            {
                await foreach (var sample in api.TrafficAsync(ct).ConfigureAwait(false))
                    Traffic?.Invoke(this, sample);
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException)
            {
                // Статистика не критична для подключения.
            }
            catch (IOException)
            {
            }
        }
    }

    private void OnCoreExited(object? sender, int exitCode)
    {
        // Ядро упало: сначала возвращаем прокси Windows, чтобы браузеры не остались без интернета.
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var which = ReferenceEquals(sender, _xray) ? "xray" : "sing-box";
                await StopCoreAsync().ConfigureAwait(false);
                SetStatus(new ConnectionStatus(ConnectionState.Error, $"Ядро неожиданно остановилось (код {exitCode}). Подробности — в журнале."));
                RecUrgent(Ev.CoreExited, ("core", which), ("code", Num(exitCode)));
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private async Task StopCoreAsync(bool sendStop = true)
    {
        if (_systemProxyApplied)
        {
            _systemProxy.Restore();
            _systemProxyApplied = false;
        }

        if (_trafficCts is not null)
        {
            await _trafficCts.CancelAsync().ConfigureAwait(false);
            _trafficCts.Dispose();
            _trafficCts = null;
        }

        SetExitRegion(null);

        foreach (var core in new[] { _core, _xray })
        {
            if (core is null)
                continue;
            core.Exited -= OnCoreExited;
            await core.DisposeAsync().ConfigureAwait(false);
        }

        _core = null;
        _xray = null;

        if (_runningViaService)
        {
            _runningViaService = false;
            if (sendStop && _service is { IsConnected: true } service)
            {
                try
                {
                    await service.SendAsync(new Ipc.StopRequest(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ServiceException or TimeoutException)
                {
                    // Служба сама остановит ядро, когда интерфейс отключится от канала.
                }
            }
        }
    }

    /// <summary>Служба сообщила о сбое ядра, которое не удалось перезапустить.</summary>
    private void OnServiceStatus(object? sender, Ipc.StatusEvent status)
    {
        if (Blocking != status.Blocking)
        {
            Rec(Ev.BlockingChanged, ("blocking", Flag(status.Blocking)));
            Blocking = status.Blocking;
            ServiceChanged?.Invoke(this, EventArgs.Empty);
        }

        if (status.State == Ipc.ServiceState.Idle && _runningViaService && status.Message is not null)
        {
            // Службу остановили извне (аварийный откат из меню «Пуск»): мы тоже отключены.
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _runningViaService = false;
                    await StopCoreAsync(sendStop: false).ConfigureAwait(false);
                    SetStatus(new ConnectionStatus(ConnectionState.Disconnected, status.Message));
                    RecUrgent(Ev.ServiceStoppedOutside, ("msg", NetLogClassifier.Anonymize(status.Message)));
                }
                finally
                {
                    _gate.Release();
                }
            });
            return;
        }

        if (status.State != Ipc.ServiceState.Failed || !_runningViaService)
            return;
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                _runningViaService = false; // служба уже остановила ядро
                await StopCoreAsync().ConfigureAwait(false);
                SetStatus(new ConnectionStatus(ConnectionState.Error, status.Message ?? "Служба остановила подключение из-за сбоя ядра."));
                RecUrgent(Ev.ServiceCoreFailed, ("msg", status.Message is { } m ? NetLogClassifier.Anonymize(m) : null));
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    /// <summary>Связь со службой потеряна: она остановит ядро сама, мы возвращаем прокси Windows.</summary>
    private void OnServiceDisconnected(object? sender, EventArgs e)
    {
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var wasRunning = _runningViaService;
                _runningViaService = false;
                RecUrgent(Ev.ServiceLost, ("wasRunning", Flag(wasRunning)));
                if (_service is { } old)
                {
                    _service = null;
                    await old.DisposeAsync().ConfigureAwait(false);
                }

                if (wasRunning)
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    SetStatus(new ConnectionStatus(ConnectionState.Error, "Связь со службой Тропы потеряна. Подключение остановлено."));
                }
            }
            finally
            {
                _gate.Release();
            }

            ServiceChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SetStatus(ConnectionStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    /// <summary>Только что остановленное ядро может отпускать порт не мгновенно — ждём до 3 секунд.</summary>
    private static async Task<bool> WaitPortFreeAsync(int port, CancellationToken ct)
    {
        for (var i = 0; i < 15; i++)
        {
            if (PortIsFree(port))
                return true;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        return PortIsFree(port);
    }

    private static bool PortIsFree(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_watchTimer is not null)
            await _watchTimer.DisposeAsync().ConfigureAwait(false);
        await StopTelemetryAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            if (_service is { } service)
            {
                service.Disconnected -= OnServiceDisconnected;
                service.Status -= OnServiceStatus;
                _service = null;
                await service.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
