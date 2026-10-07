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
    private StartRequest? _request;
    private SecretScrubber _scrubber = SecretScrubber.PatternsOnly;
    private DnsClientPolicy? _dnsPolicy;

    public StatusEvent Status { get; private set; } = new(ServiceState.Idle, null, null);

    /// <summary>Откат изменений, оставшихся после аварийного завершения службы.</summary>
    public void RecoverAfterCrash()
    {
        Directory.CreateDirectory(options.DataDirectory);
        if (options.DnsRegistry is { } registry)
            new DnsClientPolicy(registry, new ChangeJournal(options.JournalPath)).Restore();
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
            await StopCoreAsync().ConfigureAwait(false);

            if (request.Mode == CaptureModeDto.Tun && !options.Privileged)
                return "Режим «Весь компьютер» требует службу Тропы с правами системы. Установите службу или выберите «Только браузеры».";

            var violations = ConfigGuard.CheckSingBox(request.SingBoxConfig, new GuardPolicy
            {
                AllowedDirectories = [options.GeoDirectory, options.RunDirectory],
                AllowLanInbound = request.AllowLanInbound,
            });
            if (violations.Count > 0)
                return "Конфиг не прошёл проверку безопасности службы: " + violations[0];
            if (request.ReadinessPort is < 1 or > 65535)
                return "Неверный порт готовности.";

            SetStatus(new StatusEvent(ServiceState.Starting, null, null));
            PrepareDirectories();
            PinnedFiles.EnsureGeoInstalled(options.Cores.GeoSourceDirectory, options.GeoDirectory);
            PinnedFiles.VerifyGeo(options.GeoDirectory);

            _request = request;
            _scrubber = new SecretScrubber(ExtractSecrets(request.SingBoxConfig));
            _restarts.Clear();
            await LaunchAsync(ct).ConfigureAwait(false);

            if (request.Mode == CaptureModeDto.Tun && request.DisableSmartNameResolution && options.DnsRegistry is { } registry)
            {
                _dnsPolicy = new DnsClientPolicy(registry, new ChangeJournal(options.JournalPath));
                _dnsPolicy.Apply(DateTimeOffset.Now);
            }

            SetStatus(new StatusEvent(ServiceState.Running, null, DateTimeOffset.Now));
            return null;
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException or UnauthorizedAccessException)
        {
            await StopCoreAsync().ConfigureAwait(false);
            var message = _scrubber.Scrub(ex.Message);
            SetStatus(new StatusEvent(ServiceState.Failed, message, null));
            return message;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Status.State is ServiceState.Idle)
                return;
            SetStatus(new StatusEvent(ServiceState.Stopping, null, null));
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(new StatusEvent(ServiceState.Idle, null, null));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PrepareDirectories()
    {
        if (options.Privileged)
            SecureDirectory.Ensure(options.DataDirectory);
        Directory.CreateDirectory(options.DataDirectory);
        Directory.CreateDirectory(options.RunDirectory);
        Directory.CreateDirectory(options.GeoDirectory);
    }

    private async Task LaunchAsync(CancellationToken ct)
    {
        var request = _request ?? throw new InvalidOperationException("Нет конфига для запуска.");
        _core = await CoreProcess.StartSingBoxAsync(options.Cores, request.SingBoxConfig, request.ReadinessPort,
            options.RunDirectory, _scrubber, onLog, ct).ConfigureAwait(false);
        _core.Exited += OnCoreExited;
    }

    private void OnCoreExited(object? sender, int exitCode) => _ = Task.Run(async () =>
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_core is null || !ReferenceEquals(sender, _core))
                return;
            _core.Exited -= OnCoreExited;
            await _core.DisposeAsync().ConfigureAwait(false);
            _core = null;

            var now = DateTimeOffset.Now;
            while (_restarts.Count > 0 && now - _restarts.Peek() > RestartWindow)
                _restarts.Dequeue();
            if (_restarts.Count >= MaxRestarts)
            {
                await StopCoreAsync().ConfigureAwait(false);
                SetStatus(new StatusEvent(ServiceState.Failed, $"Ядро падает слишком часто (последний код {exitCode}). Подключение остановлено.", null));
                return;
            }

            _restarts.Enqueue(now);
            onLog($"Ядро завершилось с кодом {exitCode}, перезапуск ({_restarts.Count} из {MaxRestarts})…");
            try
            {
                await LaunchAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(new StatusEvent(ServiceState.Running, "Ядро перезапущено после сбоя.", Status.Since ?? now));
            }
            catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException)
            {
                await StopCoreAsync().ConfigureAwait(false);
                SetStatus(new StatusEvent(ServiceState.Failed, _scrubber.Scrub(ex.Message), null));
            }
        }
        finally
        {
            _gate.Release();
        }
    });

    private async Task StopCoreAsync()
    {
        if (_core is not null)
        {
            _core.Exited -= OnCoreExited;
            await _core.DisposeAsync().ConfigureAwait(false);
            _core = null;
        }

        _dnsPolicy?.Restore();
        _dnsPolicy = null;
        _request = null;
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
                            && p.Name is "uuid" or "password" or "public_key" or "short_id" or "secret" or "username")
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
