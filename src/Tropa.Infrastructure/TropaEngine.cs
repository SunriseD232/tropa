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
public sealed class TropaEngine : IAsyncDisposable
{
    private readonly EnginePaths _paths;
    private readonly StateStore _store;
    private readonly SecretStore _secrets;
    private readonly SystemProxy _systemProxy;
    private CoreLocations _locations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;

    private readonly Func<CancellationToken, Task<ServiceClient?>> _connectService;

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

    private TropaEngine(EnginePaths paths, StateStore store, LoadResult loaded, SystemProxy systemProxy, CoreLocations locations,
        Func<DateTimeOffset> now, Func<CancellationToken, Task<ServiceClient?>> connectService)
    {
        _paths = paths;
        _store = store;
        _secrets = loaded.Secrets;
        _systemProxy = systemProxy;
        _locations = locations;
        _now = now;
        _connectService = connectService;
        State = loaded.State;
        StartupWarning = loaded.Warning;
        Log += (_, line) => Remember(line);
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

    public event EventHandler<string>? Log;

    public SecretScrubber Scrubber => new(_secrets.Values);

    public static TropaEngine Open(EnginePaths paths, IProxySettingsStore? proxyStore = null, CoreLocations? locations = null,
        Func<DateTimeOffset>? now = null, Func<CancellationToken, Task<ServiceClient?>>? connectService = null)
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
            connectService ?? (_ => Task.FromResult<ServiceClient?>(null)));

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
        State = change(State);
        Save();
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Save() => _store.Save(State, _secrets, _now());

    public void UpdateSettings(Func<AppSettings, AppSettings> change) =>
        Update(s => s with { Settings = change(s.Settings) });

    public async Task SetActiveAsync(Guid profileId, CancellationToken ct = default)
    {
        if (State.Profiles.All(p => p.Profile.Id != profileId))
            return;
        Update(s => s with { ActiveProfileId = profileId });
        if (Status.State == ConnectionState.Connected)
            await ReconnectAsync(ct).ConfigureAwait(false);
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
        if (Status.State == ConnectionState.Connected && State.ActiveProfileId == profile.Id)
            await ReconnectAsync(ct).ConfigureAwait(false);
    }

    public void RemoveProfile(Guid profileId) =>
        Update(s => s with
        {
            Profiles = s.Profiles.Where(p => p.Profile.Id != profileId).ToList(),
            ActiveProfileId = s.ActiveProfileId == profileId ? null : s.ActiveProfileId,
        });

    public void RemoveSubscription(Guid subscriptionId) =>
        Update(s =>
        {
            var profiles = s.Profiles.Where(p => p.Profile.SubscriptionId != subscriptionId).ToList();
            return s with
            {
                Subscriptions = s.Subscriptions.Where(x => x.Id != subscriptionId).ToList(),
                Profiles = profiles,
                ActiveProfileId = profiles.Any(p => p.Profile.Id == s.ActiveProfileId) ? s.ActiveProfileId : null,
            };
        });

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
            messages.Add("Не найдено ни одной ссылки vless://, vmess:// или trojan://.");
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
        return await UpdateSubscriptionAsync(sub.Id, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Автообновление подписок (subUpdate): обновляет те, что старше заданного интервала. 0 — только вручную.
    /// Ошибка одной подписки не мешает остальным.
    /// </summary>
    public async Task UpdateDueSubscriptionsAsync(CancellationToken ct = default)
    {
        var hours = State.Settings.Subscriptions.UpdateHours;
        if (hours <= 0)
            return;
        foreach (var sub in State.Subscriptions.Where(s => s.LastUpdated is null || _now() - s.LastUpdated > TimeSpan.FromHours(hours)).ToList())
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
        LocalProxy? proxy = Status.State == ConnectionState.Connected
            ? new LocalProxy(_mixedPort, CompatRules.Evaluate(State.Settings).Effective.General.LocalPass ? Auth.Username : null,
                CompatRules.Evaluate(State.Settings).Effective.General.LocalPass ? Auth.Password : null)
            : null;

        FetchResult fetched;
        try
        {
            fetched = await SubscriptionFetcher.FetchAsync(sub, proxy, ct).ConfigureAwait(false);
        }
        catch (SubscriptionFetchException ex)
        {
            ReplaceSubscription(sub with { LastError = ex.Message });
            return new ImportReport(0, 0, 1, 0, [ex.Message]);
        }

        var parsed = SubscriptionParser.Parse(fetched.Body);
        var info = SubscriptionHeaders.Parse(fetched.Headers);
        var merged = SubscriptionMerger.Merge(sub.Id, State.Profiles, parsed.Profiles.Select(p => p.Profile).ToList(), _now());

        var updatedSub = sub with
        {
            Info = info,
            LastUpdated = _now(),
            LastError = parsed.Profiles.Count == 0 ? "В подписке не найдено серверов. Попробуйте «Представляться как v2rayN»." : null,
            Name = sub.Name == new Uri(sub.Url.Reveal()).Host && info.Title is { } title ? title : sub.Name,
            UpdateHours = info.UpdateIntervalHours ?? sub.UpdateHours,
        };
        Update(s => s with
        {
            Subscriptions = s.Subscriptions.Select(x => x.Id == sub.Id ? updatedSub : x).ToList(),
            Profiles = merged.Profiles,
            ActiveProfileId = merged.Profiles.Any(p => p.Profile.Id == s.ActiveProfileId)
                ? s.ActiveProfileId
                : merged.Profiles.FirstOrDefault(p => p.Profile.SubscriptionId == sub.Id && p.RemovedByProvider is null)?.Profile.Id,
        });

        var messages = parsed.Errors.Select(e => $"Сервер {e.Position}: {e.Message}").ToList();
        if (updatedSub.LastError is { } err)
            messages.Insert(0, err);
        if (fetched.ViaProxy)
            messages.Insert(0, "Адрес подписки недоступен напрямую — загружено через сервер.");
        return new ImportReport(merged.Added, merged.Updated, parsed.Errors.Count, parsed.SkippedUnsupported, messages);
    }

    private void ReplaceSubscription(Subscription sub) =>
        Update(s => s with { Subscriptions = s.Subscriptions.Select(x => x.Id == sub.Id ? sub : x).ToList() });

    // ---------------- Тесты ----------------

    /// <summary>Проверяет серверы и сохраняет результаты: они видны в списке и учитываются авто-выбором.</summary>
    public async Task<IReadOnlyDictionary<Guid, ServerTestResult>> TestAsync(
        IReadOnlyList<Profile> profiles, TestKinds kinds = TestKinds.Standard, IProgress<TestProgress>? progress = null,
        TesterOptions? options = null, AppSettings? settingsOverride = null, CancellationToken ct = default)
    {
        var results = await ServerTester.TestAsync(profiles, State.Profiles.Select(p => p.Profile).ToList(), settingsOverride ?? State.Settings,
            kinds, _locations, _paths.RunDirectory, Scrubber, line => Log?.Invoke(this, line), progress, ct, options).ConfigureAwait(false);
        Update(s => s with
        {
            Profiles = s.Profiles.Select(sp => results.TryGetValue(sp.Profile.Id, out var r)
                ? sp with { LastTest = MergeResult(sp.LastTest, r, kinds) }
                : sp).ToList(),
        });
        return results;
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

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Status.State == ConnectionState.Connected)
                return;
            SetStatus(new ConnectionStatus(ConnectionState.Connecting));
            await AttachServiceAsync(ct).ConfigureAwait(false);
            await StartCoreAsync(ct).ConfigureAwait(false);
            var note = State.Settings.Connection.Mode == CaptureMode.Tun && !TunAvailable
                ? "Служба Тропы не установлена, поэтому режим «Весь компьютер» недоступен. Сейчас через Тропу идут браузеры и программы, использующие прокси Windows."
                : null;
            // Другой VPN рядом — частая причина «подключено, но ничего не открывается».
            if (ForeignVpnWarning() is { } foreign)
                note = note is null ? foreign : foreign + " " + note;
            SetStatus(new ConnectionStatus(ConnectionState.Connected, note, _now()));
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or UnsupportedProfileException or InvalidOperationException or IOException or ServiceException or TimeoutException)
        {
            // Переподключение без Stop не удалось: старое ядро службы останавливаем, но при включённом
            // kill switch блокировку не снимаем — служба держит её до «Отключить».
            _runningViaService |= _servicePending;
            await StopCoreAsync(sendStop: !State.Settings.General.KillSwitch).ConfigureAwait(false);
            SetStatus(new ConnectionStatus(ConnectionState.Error, Scrubber.Scrub(ex.Message)));
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
        return await new UpdateClient(http, Updates).DownloadInstallerAsync(app, Path.Combine(_paths.Local, "update-download"), ct).ConfigureAwait(false);
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
            if (!(slept || changed) || !State.Settings.General.Reconnect || Status.State != ConnectionState.Connected)
                return;
            // Сеть после пробуждения поднимается не сразу.
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            Log?.Invoke(this, slept ? "Компьютер проснулся — переподключаюсь." : "Сеть сменилась — переподключаюсь.");
            await ApplyIfConnectedAsync().ConfigureAwait(false);
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
            if (gateways.Count == 0 || addresses.Contains(Diagnosis.OwnTunAddress))
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
    };

    /// <summary>Девять шагов диагностики; текущее подключение не трогается.</summary>
    public Task<IReadOnlyList<StepResult>> RunDiagnosticsAsync(IProgress<StepResult>? progress, CancellationToken ct = default) =>
        DiagnosticsRunner.RunAsync(DiagnosticsInputNow(), progress, ct);

    /// <summary>Инструмент «Проверка домена»: ответ DNS провайдера и через туннель.</summary>
    public Task<DomainCheck> CheckDomainAsync(string domain, CancellationToken ct = default) =>
        DiagnosticsRunner.CheckDomainAsync(DiagnosticsInputNow(), domain, ct);

    /// <summary>Снять блокировку, которую служба держит после сбоя или аварийного закрытия Тропы.</summary>
    public async Task ReleaseBlockingAsync(CancellationToken ct = default)
    {
        await AttachServiceAsync(ct).ConfigureAwait(false);
        if (_service is { IsConnected: true } service)
            await service.SendAsync(new Ipc.StopRequest(), ct).ConfigureAwait(false);
        Blocking = false;
        ServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// «Аварийно вернуть настройки сети» (docs/02-security.md, §3.6): отключиться, вернуть прокси
    /// Windows и попросить службу откатить всё своё. Работает даже при сломанном состоянии.
    /// </summary>
    public async Task EmergencyRollbackAsync()
    {
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


    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
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
            await ReconnectAsync(ct).ConfigureAwait(false);
    }

    private async Task ReconnectAsync(CancellationToken ct)
    {
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

        await ConnectAsync(ct).ConfigureAwait(false);
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
        };
        var effective = CompatRules.Evaluate(settings, active).Effective;

        // randomPorts: свободный случайный порт при каждом подключении — фиксированный легко найти сканированием.
        _mixedPort = effective.Connection.RandomPorts ? ServerTester.FreePort() : effective.Connection.SocksPort;
        if (!PortIsFree(_mixedPort))
            throw new InvalidOperationException($"Порт {_mixedPort} занят другой программой. Закройте её или выберите другой порт в настройках.");

        // Через службу наборы правил читает она — из своего защищённого каталога.
        var service = ServiceAvailable ? _service : null;
        var ruleSetDirectory = service?.RuleSetDirectory ?? _paths.GeoDirectory;
        if (service is null)
        {
            PinnedFiles.EnsureGeoInstalled(_locations.Geo, _locations.GeoSourceDirectory, _paths.GeoDirectory);
            PinnedFiles.VerifyGeo(_locations.Geo, _paths.GeoDirectory);
        }

        var clashPort = ServerTester.FreePort();
        var dpiProbePort = effective.Routing.DpiFirst ? ServerTester.FreePort() : (int?)null;
        var clashSecret = SecretStore.NewRandomToken();
        var group = effective.Connection.AutoSelect
            ? State.Profiles.Where(p => p.Profile.SubscriptionId == active.SubscriptionId && p.RemovedByProvider is null
                    && ProfileCompat.Issues(p.Profile).Count == 0
                    && !(p.Profile.Transport.Type == TransportType.Xhttp && p.Profile.ChainVia is not null)
                    // urltest видит только задержку и «заморозку» не замечает — такие серверы убираем сами.
                    && !ServerHealth.ShouldExclude(p.LastTest, _now())
                    // allowInsecureWarn: сервер без проверки сертификата — только если вы выбрали его сами.
                    && !(effective.Dpi.AllowInsecureWarn && p.Profile.Security.AllowInsecure && p.Profile.Id != active.Id))
                .Select(p => p.Profile).ToList()
            : [];

        // Гибрид (docs/05-config-generation.md, §1): серверы с XHTTP — через Xray, шум — тоже через Xray.
        var allProfiles = State.Profiles.Select(p => p.Profile).ToList();
        var plan = CorePlan.Make(settings, UsedProfiles(active, group, allProfiles, effective));
        string? xrayConfig = null;
        var xrayPorts = new Dictionary<Guid, int>();
        int? xrayDirectPort = null;
        LocalAuth? xrayAuth = null;
        if (plan.NeedsXray)
        {
            xrayAuth = new LocalAuth("xray-" + SecretStore.NewRandomToken(4), SecretStore.NewRandomToken());
            foreach (var p in plan.XrayProfiles)
                xrayPorts[p.Id] = ServerTester.FreePort();
            xrayDirectPort = plan.XrayDirect ? ServerTester.FreePort() : null;
            xrayConfig = XrayConfigBuilder.Build(plan.XrayProfiles.Select(p => new XrayTarget(p, xrayPorts[p.Id])).ToList(), settings, xrayAuth, xrayDirectPort);
            var xrayViolations = XrayConfigGuard.Check(xrayConfig);
            if (xrayViolations.Count > 0)
                throw new IntegrityException("Конфиг Xray не прошёл проверку безопасности: " + xrayViolations[0]);
        }

        var config = SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = settings,
            Active = active,
            Profiles = allProfiles,
            AutoSelectGroup = group,
            RuleSetDirectory = ruleSetDirectory,
            LocalAuth = Auth,
            ClashApiPort = clashPort,
            ClashApiSecret = clashSecret,
            DpiProbePort = dpiProbePort,
            XrayPorts = xrayPorts,
            XrayDirectPort = xrayDirectPort,
            XrayAuth = xrayAuth,
            XrayPath = service?.XrayPath ?? CoreProcess.ExecutablePath(_locations, "xray"),
        });
        var xrayReadiness = xrayPorts.Values.Concat(xrayDirectPort is { } dp ? [dp] : []).FirstOrDefault();

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
        if (dpiProbePort is { } probe)
            _ = DpiMonitorAsync(new ClashApi(clashPort, clashSecret), probe, _trafficCts.Token);
        else
            SetDpiStatus(new Dictionary<string, bool>());
    }

    /// <summary>Серверы, которые попадут в конфиг: активный, группа, промежуточные звенья цепочек и серверы из правил.</summary>
    private static List<Profile> UsedProfiles(Profile active, IReadOnlyList<Profile> group, IReadOnlyList<Profile> all, AppSettings settings)
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
        foreach (var p in group)
            Add(p);
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
    /// Фоновая проверка (docs/07-testing-diagnostics.md, §1): urltest видит только задержку, а «заморозку»
    /// замечает лишь тест скорости. Раз в ≥30 минут скачиваем 1 МБ через текущий сервер; если он
    /// «замёрз» или перестал отвечать — переключаемся на лучший рабочий (при включённом авто-выборе)
    /// или предупреждаем.
    /// </summary>
    private async Task MonitorAsync(Guid activeId, CancellationToken ct)
    {
        var interval = MonitorIntervalOverride
            ?? TimeSpan.FromMinutes(Math.Max(30, State.Settings.Connection.AutoIntervalMinutes * 10));
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

                var name = active.Name;
                var replacement = State.Settings.Connection.AutoSelect
                    ? ServerHealth.PickReplacement(
                        State.Profiles.Where(p => p.Profile.SubscriptionId == active.SubscriptionId && p.RemovedByProvider is null
                                && !(State.Settings.Dpi.AllowInsecureWarn && p.Profile.Security.AllowInsecure))
                            .Select(p => (p.Profile.Id, p.LastTest)), activeId, _now())
                    : null;

                if (replacement is { } next)
                {
                    var nextName = State.Profiles.First(p => p.Profile.Id == next).Profile.Name;
                    // Не ct: переподключение само отменяет этот токен, и с ним подключиться заново было бы нельзя.
                    await SetActiveAsync(next, CancellationToken.None).ConfigureAwait(false);
                    if (Status.State == ConnectionState.Connected)
                        SetStatus(Status with { Message = $"Сервер «{name}» перестал работать ({ServerHealth.Title(ServerHealth.Classify(r)).ToLowerInvariant()}). Переключено на «{nextName}»." });
                    return;
                }

                SetStatus(Status with
                {
                    Message = $"Сервер «{name}»: {ServerHealth.Title(ServerHealth.Classify(r)).ToLowerInvariant()}. " + ServerHealth.Explain(r)
                        + (State.Settings.Connection.AutoSelect ? " Проверенных рабочих замен нет — нажмите «Тест всех» на экране «Серверы»." : ""),
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

    // ---------------- Сначала обход DPI ----------------

    /// <summary>Группа → идёт ли она сейчас напрямую с обходом (true) или через сервер (false).</summary>
    public IReadOnlyDictionary<string, bool> DpiStatus { get; private set; } = new Dictionary<string, bool>();

    public event EventHandler? DpiStatusChanged;

    private void SetDpiStatus(IReadOnlyDictionary<string, bool> status)
    {
        DpiStatus = status;
        DpiStatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Как часто перепроверять обход DPI (в тестах — чаще).</summary>
    internal TimeSpan DpiCheckInterval { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Только для тестов: свой адрес проверки группы вместо настоящего сервиса.</summary>
    internal Func<Core.Routing.DpiGroup, Uri>? DpiProbeUrlOverride { get; set; }

    /// <summary>
    /// Проверяет каждую группу через вход проверки (тот же путь, что у обычного трафика: напрямую
    /// с фрагментацией). Не открылось — группа переключается на сервер; заработало снова — обратно.
    /// Переключение мгновенное (Clash API), без переподключения.
    /// </summary>
    private async Task DpiMonitorAsync(ClashApi api, int probePort, CancellationToken ct)
    {
        using (api)
        {
            var current = new Dictionary<string, bool>();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    foreach (var g in Core.Routing.DpiGroups.All)
                    {
                        var works = await ProbeDpiAsync(probePort, DpiProbeUrlOverride?.Invoke(g) ?? g.ProbeUrl, ct).ConfigureAwait(false);
                        if (!current.TryGetValue(g.Key, out var was) || was != works)
                        {
                            await api.SelectAsync(g.SelectorTag, works ? Core.Routing.DpiGroups.DirectTag : SingBoxConfigBuilder.ProxyTag, ct).ConfigureAwait(false);
                            current[g.Key] = works;
                            Log?.Invoke(this, $"Обход DPI для «{g.Title}»: " + (works ? "работает, напрямую." : "не открывается, через сервер."));
                            SetDpiStatus(new Dictionary<string, bool>(current));
                        }
                    }

                    await Task.Delay(DpiCheckInterval, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (HttpRequestException ex)
            {
                Log?.Invoke(this, "Проверка обхода DPI остановилась: " + Scrubber.Scrub(ex.Message));
            }
        }
    }

    /// <summary>Открывается ли адрес через вход проверки: ответ с заголовками за 8 секунд.</summary>
    private async Task<bool> ProbeDpiAsync(int probePort, Uri url, CancellationToken ct)
    {
        using var client = ServerTester.ProbeClient(probePort, Auth, TimeSpan.FromSeconds(8), allowRedirect: false);
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            return (int)response.StatusCode < 500;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
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
                await StopCoreAsync().ConfigureAwait(false);
                SetStatus(new ConnectionStatus(ConnectionState.Error, $"Ядро неожиданно остановилось (код {exitCode}). Подробности — в журнале."));
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
