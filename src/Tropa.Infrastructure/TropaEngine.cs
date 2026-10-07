using System.Net;
using System.Net.Sockets;
using Tropa.Core.Compatibility;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Core.Security;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Net;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.Storage;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Infrastructure.Testing;

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
    private readonly CoreLocations _locations;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<DateTimeOffset> _now;

    private readonly Func<CancellationToken, Task<ServiceClient?>> _connectService;

    private CoreProcess? _core;
    private ServiceClient? _service;
    private bool _runningViaService;
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
        }
        catch (ServiceException ex)
        {
            StartupWarning = ex.Message;
        }

        ServiceChanged?.Invoke(this, EventArgs.Empty);
    }

    public AppState State { get; private set; }

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
        var engine = new TropaEngine(paths, store, loaded, systemProxy, locations ?? CoreLocations.Default(), now ?? (() => DateTimeOffset.Now),
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

    public Task<IReadOnlyDictionary<Guid, Core.Testing.ServerTestResult>> TestAsync(
        IReadOnlyList<Profile> profiles, TestKinds kinds = TestKinds.Standard, IProgress<TestProgress>? progress = null,
        TesterOptions? options = null, CancellationToken ct = default) =>
        ServerTester.TestAsync(profiles, State.Profiles.Select(p => p.Profile).ToList(), State.Settings, kinds, _locations,
            _paths.RunDirectory, Scrubber, line => Log?.Invoke(this, line), progress, ct, options);

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
            SetStatus(new ConnectionStatus(ConnectionState.Connected, note, _now()));
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or UnsupportedProfileException or InvalidOperationException or IOException or ServiceException or TimeoutException)
        {
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(new ConnectionStatus(ConnectionState.Error, Scrubber.Scrub(ex.Message)));
        }
        finally
        {
            _gate.Release();
        }
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
        await DisconnectAsync().ConfigureAwait(false);
        await ConnectAsync(ct).ConfigureAwait(false);
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        var active = State.ActiveProfile ?? throw new InvalidOperationException("Сначала добавьте и выберите сервер.");
        var settings = State.Settings with
        {
            Connection = State.Settings.Connection with
            {
                Mode = TunAvailable ? State.Settings.Connection.Mode : CaptureMode.SystemProxy,
            },
        };
        var effective = CompatRules.Evaluate(settings, active).Effective;

        _mixedPort = effective.Connection.SocksPort;
        if (!PortIsFree(_mixedPort))
            throw new InvalidOperationException($"Порт {_mixedPort} занят другой программой. Закройте её или выберите другой порт в настройках.");

        // Через службу наборы правил читает она — из своего защищённого каталога.
        var service = ServiceAvailable ? _service : null;
        var ruleSetDirectory = service?.RuleSetDirectory ?? _paths.GeoDirectory;
        if (service is null)
        {
            PinnedFiles.EnsureGeoInstalled(_locations.GeoSourceDirectory, _paths.GeoDirectory);
            PinnedFiles.VerifyGeo(_paths.GeoDirectory);
        }

        var clashPort = ServerTester.FreePort();
        var clashSecret = SecretStore.NewRandomToken();
        var group = effective.Connection.AutoSelect
            ? State.Profiles.Where(p => p.Profile.SubscriptionId == active.SubscriptionId && p.RemovedByProvider is null
                    && p.Profile.Transport.Type != TransportType.Xhttp && ProfileCompat.Issues(p.Profile).Count == 0)
                .Select(p => p.Profile).ToList()
            : [];
        var config = SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = settings,
            Active = active,
            Profiles = State.Profiles.Select(p => p.Profile).ToList(),
            AutoSelectGroup = group,
            RuleSetDirectory = ruleSetDirectory,
            LocalAuth = Auth,
            ClashApiPort = clashPort,
            ClashApiSecret = clashSecret,
        });

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
                effective.Dns.SmartNameRes), ct).ConfigureAwait(false);
            if (!reply.Ok)
                throw new ServiceException(reply.Error ?? "Служба отказалась запускать подключение.");
            _runningViaService = true;
        }
        else
        {
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

    private async Task StopCoreAsync()
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

        if (_core is not null)
        {
            _core.Exited -= OnCoreExited;
            await _core.DisposeAsync().ConfigureAwait(false);
            _core = null;
        }

        if (_runningViaService)
        {
            _runningViaService = false;
            if (_service is { IsConnected: true } service)
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
