using System.Text.Json;
using Tropa.Core.Security;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Ipc;

namespace Tropa.Service;

/// <summary>
/// Держит ядро запущенным (docs/03-architecture.md, §1): повторно проверяет конфиг, проверяет хэши,
/// перезапускает ядро при сбое (не больше 3 раз за минуту) и возвращает систему в исходное
/// состояние при остановке.
/// </summary>
internal sealed class CoreSupervisor(ServiceOptions options, Action<StatusEvent> onStatus, Action<string> onLog) : IAsyncDisposable
{
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _restarts = new();
    private CoreProcess? _core;
    private CoreProcess? _xray;
    private StartRequest? _request;
    private SecretScrubber _scrubber = SecretScrubber.PatternsOnly;
    private DnsClientPolicy? _dnsPolicy;
    private LoopbackExemption? _loopback;
    private IKillSwitch? _killSwitch;

    public StatusEvent Status { get; private set; } = new(ServiceState.Idle, null, null);

    /// <summary>Откат изменений, оставшихся после аварийного завершения службы.</summary>
    public void RecoverAfterCrash()
    {
        Directory.CreateDirectory(options.DataDirectory);
        if (options.DnsRegistry is { } registry)
            new DnsClientPolicy(registry, new ChangeJournal(options.JournalPath)).Restore();
        if (options.LoopbackStore is { } loopback)
            new LoopbackExemption(loopback, new ChangeJournal(options.JournalPath)).Restore();
        // Фильтры kill switch восстанавливать не нужно: динамический сеанс WFP закрылся вместе с упавшей службой.
        // Конфиги с ключами, оставшиеся от прерванного запуска, удаляем.
        if (Directory.Exists(options.RunDirectory))
        {
            foreach (var f in Directory.GetFiles(options.RunDirectory, "config-*.json"))
                File.Delete(f);
        }
    }

    /// <summary>null — ядро запущено; иначе понятная причина отказа.</summary>
    public async Task<string?> StartAsync(StartRequest request, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wantKillSwitch = request.KillSwitch && request.Mode == CaptureModeDto.Tun;
            // При переподключении блокировку не снимаем: иначе между остановкой и запуском трафик ушёл бы напрямую.
            await StopCoreAsync(keepKillSwitch: wantKillSwitch).ConfigureAwait(false);

            if (request.Mode == CaptureModeDto.Tun && !options.Privileged)
                return "Режим «Весь компьютер» требует службу Тропы с правами системы. Установите службу или выберите «Только браузеры».";

            var violations = ConfigGuard.CheckSingBox(request.SingBoxConfig, new GuardPolicy
            {
                AllowedDirectories = [options.GeoDirectory, options.RunDirectory],
                AllowLanInbound = request.AllowLanInbound,
            });
            if (violations.Count > 0)
                return "Конфиг не прошёл проверку безопасности службы: " + violations[0];
            if (request.XrayConfig is { } xrayConfig)
            {
                var xrayViolations = XrayConfigGuard.Check(xrayConfig);
                if (xrayViolations.Count > 0)
                    return "Конфиг Xray не прошёл проверку безопасности службы: " + xrayViolations[0];
                if (request.XrayReadinessPort is < 1 or > 65535)
                    return "Неверный порт готовности Xray.";
            }
            if (request.ReadinessPort is < 1 or > 65535)
                return "Неверный порт готовности.";
            if (request.LoopbackSids is { } sids && sids.Any(sid => !LoopbackExemption.IsAppContainerSid(sid)))
                return "Неверный идентификатор приложения Store.";
            if (wantKillSwitch && options.KillSwitchFactory is null)
                return "Kill switch недоступен: служба работает без прав системы.";

            SetStatus(new StatusEvent(ServiceState.Starting, null, null));
            PrepareDirectories();
            PinnedFiles.EnsureGeoInstalled(options.Cores.GeoSourceDirectory, options.GeoDirectory);
            PinnedFiles.VerifyGeo(options.GeoDirectory);

            _request = request;
            _scrubber = new SecretScrubber(ExtractSecrets(request.SingBoxConfig).Concat(request.XrayConfig is { } xc ? ExtractSecrets(xc) : []));
            _restarts.Clear();
            // Блокировка включается ДО запуска ядра: ядра в ней разрешены по пути к exe.
            if (wantKillSwitch)
                _killSwitch ??= options.KillSwitchFactory!(AllowedApps(), request.KillSwitchAllowLan);
            else
                DisposeKillSwitch();
            await LaunchAsync(ct).ConfigureAwait(false);

            if (request.Mode == CaptureModeDto.Tun && request.DisableSmartNameResolution && options.DnsRegistry is { } registry)
            {
                _dnsPolicy = new DnsClientPolicy(registry, new ChangeJournal(options.JournalPath));
                _dnsPolicy.Apply(DateTimeOffset.Now);
            }

            if (request.Mode == CaptureModeDto.SystemProxy && request.LoopbackSids is { Count: > 0 } loopbackSids && options.LoopbackStore is { } store)
            {
                _loopback = new LoopbackExemption(store, new ChangeJournal(options.JournalPath));
                _loopback.Apply(loopbackSids, DateTimeOffset.Now);
            }

            SetStatus(new StatusEvent(ServiceState.Running, null, DateTimeOffset.Now));
            return null;
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or ArgumentException or PlatformNotSupportedException or InvalidOperationException)
        {
            // Не удалось запустить: блокировка остаётся, если пользователь её включил, — так и задумано.
            await StopCoreAsync(keepKillSwitch: request.KillSwitch && request.Mode == CaptureModeDto.Tun).ConfigureAwait(false);
            var message = _scrubber.Scrub(ex.Message);
            SetStatus(new StatusEvent(ServiceState.Failed, message, null, Blocking: _killSwitch is not null));
            return message;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <param name="keepBlocking">
    /// Интерфейс пропал (закрыт или упал): ядро останавливаем, но включённую блокировку держим —
    /// для этого она и нужна. Снимает её команда «Отключить» или «Аварийно вернуть настройки сети».
    /// </param>
    public async Task StopAsync(bool keepBlocking = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Status.State is ServiceState.Idle && _killSwitch is null)
                return;
            SetStatus(new StatusEvent(ServiceState.Stopping, null, null));
            await StopCoreAsync(keepKillSwitch: keepBlocking).ConfigureAwait(false);
            SetStatus(_killSwitch is null
                ? new StatusEvent(ServiceState.Idle, null, null)
                : new StatusEvent(ServiceState.Idle, "Интернет заблокирован аварийной блокировкой: Тропа закрылась, не отключившись.", null, Blocking: true));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>«Аварийно вернуть настройки сети»: всё остановить и откатить, даже если состояние непонятное.</summary>
    public async Task RollbackAllAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync(keepKillSwitch: false).ConfigureAwait(false);
            RecoverAfterCrash();
            SetStatus(new StatusEvent(ServiceState.Idle, "Настройки сети возвращены.", null));
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<string> AllowedApps() =>
        [CoreProcess.ExecutablePath(options.Cores, "sing-box"), CoreProcess.ExecutablePath(options.Cores, "xray")];

    private void DisposeKillSwitch()
    {
        _killSwitch?.Dispose();
        _killSwitch = null;
    }

    private void PrepareDirectories()
    {
        if (options.SecureDataDirectory)
            SecureDirectory.Ensure(options.DataDirectory);
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.RunDirectory);
        Directory.CreateDirectory(options.GeoDirectory);
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        var request = _request ?? throw new InvalidOperationException("Нет конфига для запуска.");
        // Xray первым: sing-box сразу отправляет в него трафик гибридных серверов.
        if (request.XrayConfig is { } xrayConfig)
        {
            _xray = await CoreProcess.StartXrayAsync(options.Cores, xrayConfig, request.XrayReadinessPort,
                options.RunDirectory, _scrubber, onLog, ct).ConfigureAwait(false);
            _xray.Exited += OnCoreExited;
        }

        _core = await CoreProcess.StartSingBoxAsync(options.Cores, request.SingBoxConfig, request.ReadinessPort,
            options.RunDirectory, _scrubber, onLog, ct).ConfigureAwait(false);
        _core.Exited += OnCoreExited;

        // После каждого запуска у TUN может быть новый интерфейс: разрешаем трафик именно через него.
        if (_killSwitch is not null)
        {
            var luid = await options.FindTunLuid(ct).ConfigureAwait(false)
                ?? throw new CoreStartException("Интерфейс туннеля не появился, интернет заблокирован аварийной блокировкой.");
            _killSwitch.AllowTunInterface(luid);
        }
    }

    private void OnCoreExited(object? sender, int exitCode) => _ = Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(sender, _core) && !ReferenceEquals(sender, _xray))
                return;
            // Упало любое из ядер — перезапускаем оба: гибрид работает только парой.
            await DisposeCoresAsync().ConfigureAwait(false);

            var now = DateTimeOffset.Now;
            while (_restarts.Count > 0 && now - _restarts.Peek() > RestartWindow)
                _restarts.Dequeue();
            if (_restarts.Count >= MaxRestarts)
            {
                await StopCoreAsync(keepKillSwitch: true).ConfigureAwait(false);
                SetStatus(new StatusEvent(ServiceState.Failed,
                    $"Ядро падает слишком часто (последний код {exitCode}). Подключение остановлено"
                        + (_killSwitch is null ? "." : ", интернет мимо туннеля заблокирован. Нажмите «Отключить», чтобы снять блокировку."),
                    null, Blocking: _killSwitch is not null));
                return;
            }

            _restarts.Enqueue(now);
            onLog($"Ядро завершилось с кодом {exitCode}, перезапуск ({_restarts.Count} из {MaxRestarts})…");
            try
            {
                await LaunchAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(new StatusEvent(ServiceState.Running, "Ядро перезапущено после сбоя.", Status.Since ?? now));
            }
            catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException or System.ComponentModel.Win32Exception)
            {
                await StopCoreAsync(keepKillSwitch: true).ConfigureAwait(false);
                SetStatus(new StatusEvent(ServiceState.Failed, _scrubber.Scrub(ex.Message), null, Blocking: _killSwitch is not null));
            }
        }
        finally
        {
            _gate.Release();
        }
    });

    private async Task StopCoreAsync(bool keepKillSwitch = false)
    {
        await DisposeCoresAsync().ConfigureAwait(false);
        _dnsPolicy?.Restore();
        _dnsPolicy = null;
        _loopback?.Restore();
        _loopback = null;
        _request = null;
        if (!keepKillSwitch)
            DisposeKillSwitch();
    }

    private async Task DisposeCoresAsync()
    {
        foreach (var core in new[] { _core, _xray })
        {
            if (core is null)
                continue;
            core.Exited -= OnCoreExited;
            await core.DisposeAsync().ConfigureAwait(false);
        }

        _core = null;
        _xray = null;
    }

    private void SetStatus(StatusEvent status)
    {
        Status = status;
        onStatus(status);
    }

    /// <summary>Значения, которые нельзя показывать в журнале: ключи из самого конфига.</summary>
    internal static IEnumerable<string> ExtractSecrets(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            Walk(doc.RootElement);
        }
        catch (JsonException)
        {
        }

        return result;

        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var p in e.EnumerateObject())
                    {
                        if (p.Value.ValueKind == JsonValueKind.String
                            && p.Name is "uuid" or "password" or "public_key" or "short_id" or "secret" or "username"
                                or "id" or "pass" or "user" or "publicKey" or "shortId")
                            result.Add(p.Value.GetString()!);
                        Walk(p.Value);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in e.EnumerateArray())
                        Walk(item);
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
