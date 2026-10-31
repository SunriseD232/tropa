using System.Diagnostics;
using System.Globalization;
using Tropa.Core.Diagnostics;
using Tropa.Core.Model;
using Tropa.Core.Telemetry;
using Tropa.Infrastructure.Diagnostics;
using Tropa.Infrastructure.Net;
using Tropa.Infrastructure.Telemetry;

namespace Tropa.Infrastructure;

/// <summary>
/// Журнал для разработчика (ADR-050): что происходит в Тропе — настройки и их изменения, действия
/// пользователя, подключения и сбои, окружение (другие VPN, адаптеры, прокси Windows), сетевые ошибки
/// ядер по классам. Без секретов (SecretScrubber), без адресов и доменов (NetLogClassifier), пользователь —
/// только случайный идентификатор установки и имя, если сам его указал.
/// </summary>
public sealed partial class TropaEngine
{
    private TelemetryClient? _telemetry;
    private readonly Dictionary<(string Code, string Source, string Template), int> _netErrors = [];
    private Timer? _netErrorsTimer;
    private DateTimeOffset _logWindowStart;
    private int _logWindowCount;
    private int _logSuppressed;
    private DateTimeOffset _startedAt;

    /// <summary>Сколько обычных строк журнала отправлять за 10 минут — остальное считается.</summary>
    private const int LogLinesPerWindow = 100;

    public TelemetryClient? Telemetry => _telemetry;

    /// <summary>Журнал настроен в этой сборке (есть адрес сервера).</summary>
    public bool TelemetryConfigured { get; private set; }

    private void InitTelemetry(TelemetryConfig config)
    {
        TelemetryConfigured = config.Enabled;
        try
        {
            _telemetry = new TelemetryClient(Path.Combine(_paths.Local, "telemetry"), config, _now)
            {
                Allowed = !State.Settings.Telemetry.Off,
                Name = State.Settings.Telemetry.Name,
                AppVersion = AppVersion.ToString(3),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _telemetry = null;
        }

        Log += (_, line) => OnLogForTelemetry(line);
    }

    /// <summary>
    /// Запуск интерфейса: снимок окружения и настроек и фоновая отправка. Аварийный откат и установщик
    /// (Program.EmergencyRollback, SetAutostart) отправку не запускают — их события уйдут при следующем запуске.
    /// </summary>
    public void StartTelemetry(string launch)
    {
        if (_telemetry is not { } t)
            return;
        _startedAt = _now();
        t.Proxy = LocalProxyIfConnected;
        Rec(Ev.AppStart,
            ("launch", launch),
            ("os", Environment.OSVersion.Version.ToString()),
            ("cores", CoreVersions),
            ("service", ServiceAvailable ? (TunAvailable ? "tun" : "no-tun") : ServiceStarting ? "starting" : "none"));
        if (StartupWarning is { } warning)
            Rec(Ev.StartupWarning, ("msg", warning));
        Rec(Ev.SettingsSnapshot, SettingsFlattener.Flatten(State.Settings));
        RecordEnvironment("start");
        _netErrorsTimer ??= new Timer(_ => FlushNetErrors(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        t.Start();
    }

    /// <summary>Окружение: адаптеры, другие VPN, прокси Windows — главное, что ломает подключение.</summary>
    private void RecordEnvironment(string when)
    {
        if (_telemetry is not { Active: true })
            return;
        try
        {
            // Без адресов — служебные фильтры Windows (WFP, QoS) над настоящими адаптерами: только шум.
            var adapters = LocalNetwork.Adapters().Where(a => a.IsUp && a.Addresses.Count > 0).ToList();
            var foreign = Diagnosis.ForeignVpnAdapters(adapters).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            Rec(Ev.Adapters,
                ("when", when),
                ("list", string.Join("; ", adapters.Select(a =>
                    $"{a.Name} ({a.Description}){(a.HasDefaultGateway ? " gw" : "")}{(foreign.Contains(a.Name) ? " vpn" : "")}"))));
            var processes = LocalNetwork.ForeignProcesses(_locations);
            if (foreign.Count > 0 || processes.Count > 0)
                Rec(Ev.ForeignVpn, ("when", when), ("adapters", string.Join(", ", foreign)), ("processes", string.Join(", ", processes)));
            var proxy = _systemProxy.Snapshot();
            Rec(Ev.WindowsProxy,
                ("when", when),
                ("enabled", Flag(proxy.Enabled)),
                // Адрес прокси — свой или чужой локальный порт; внешние адреса прячем.
                ("server", proxy.Server is { } s ? NetLogClassifier.Anonymize(s) : ""),
                ("pac", Flag(proxy.AutoConfigUrl is { Length: > 0 })),
                ("ours", Flag(proxy.AppliedByUs)));
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or InvalidOperationException
            or System.ComponentModel.Win32Exception or UnauthorizedAccessException or IOException)
        {
        }
    }

    /// <summary>
    /// Раз в 5 с — соединения ядра, раз в минуту — сводка UDP (3.7), если по UDP шло заметно.
    /// Только при включённом журнале: при выключенном соединения не опрашиваются вовсе.
    /// </summary>
    private async Task MeasureUdpAsync(ClashApi api, Protocol protocol, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(5);
        var meter = new UdpFlowMeter();
        using (api)
        {
            try
            {
                var last = _now();
                var minuteStart = last;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(window, ct).ConfigureAwait(false);
                    if (_telemetry is not { Active: true })
                        continue;
                    IReadOnlyList<CoreConnection> connections;
                    try
                    {
                        connections = await api.ConnectionsAsync(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException
                        or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        continue; // статистика не критична
                    }

                    var now = _now();
                    meter.Add(connections, now - last);
                    last = now;
                    if (now - minuteStart < TimeSpan.FromMinutes(1))
                        continue;
                    minuteStart = now;
                    if (meter.Flush() is { } fields)
                        Rec(Ev.UdpFlow, [.. fields, ("xray", Flag(_lastPlanNeedsXray)), ("proto", protocol.ToString())]);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void Rec(string code, params (string Key, string? Value)[] fields) => RecCore(code, fields, urgent: false);

    private void RecUrgent(string code, params (string Key, string? Value)[] fields) => RecCore(code, fields, urgent: true);

    private void Rec(string code, IReadOnlyDictionary<string, string> fields) =>
        _telemetry?.Record(code, fields.ToDictionary(kv => kv.Key, kv => Scrubber.Scrub(kv.Value), StringComparer.Ordinal));

    private void RecCore(string code, (string Key, string? Value)[] fields, bool urgent)
    {
        if (_telemetry is not { Active: true } t)
            return;
        var scrubber = Scrubber;
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            if (value is not null)
                data[key] = scrubber.Scrub(value);
        }

        t.Record(code, data.Count > 0 ? data : null, urgent);
    }

    private static string Flag(bool value) => value ? "1" : "0";

    private static string Ms(TimeSpan t) => ((long)t.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private static string Num(int n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>Сервер без адреса и ключей: имя, протокол, транспорт, защита, ядро.</summary>
    private (string, string?)[] ServerFields(Profile? p, string prefix = "")
    {
        if (p is null)
            return [(prefix + "server", "нет")];
        var stored = State.Profiles.FirstOrDefault(x => x.Profile.Id == p.Id);
        return
        [
            (prefix + "server", p.Name),
            (prefix + "proto", p.Protocol.ToString()),
            (prefix + "net", p.Transport.Type.ToString()),
            (prefix + "sec", p.Security.Type.ToString() + (p.Security.AllowInsecure ? "+insecure" : "")),
            (prefix + "core", stored is null ? "?" : $"{stored.Core}/{stored.DetectedCore?.ToString() ?? "-"}"),
            (prefix + "sub", Flag(p.SubscriptionId is not null)),
            (prefix + "chain", Flag(p.ChainVia is not null)),
        ];
    }

    /// <summary>Изменение состояния: если поменялись настройки — что именно (ключ: было → стало).</summary>
    private void OnSettingsChangedForTelemetry(AppSettings before, AppSettings after)
    {
        if (_telemetry is not { } t)
            return;
        // Сначала согласие: выключили — ничего не пишем и стираем неотправленное.
        t.Name = after.Telemetry.Name;
        if (after.Telemetry.Off)
        {
            if (t.Allowed)
                t.Clear();
            t.Allowed = false;
            return;
        }

        var turnedOn = !t.Allowed;
        t.Allowed = true;
        if (turnedOn)
        {
            Rec(Ev.SettingsSnapshot, SettingsFlattener.Flatten(after));
            return;
        }

        var diff = SettingsFlattener.Diff(before, after);
        if (diff.Count > 0)
            Rec(Ev.SettingsChanged, diff);
    }

    // ---------------- Журнал ядер и движка ----------------

    private void OnLogForTelemetry(string line)
    {
        if (_telemetry is not { Active: true })
            return;
        var clean = Scrubber.Scrub(line);
        if (NetLogClassifier.Classify(clean) is { } c)
        {
            lock (_netErrors)
            {
                var key = (c.Code, c.Source, c.Template);
                _netErrors[key] = _netErrors.GetValueOrDefault(key) + 1;
                // Слишком много разных шаблонов за минуту — отправляем раньше, не копим.
                if (_netErrors.Count < 200)
                    return;
            }

            FlushNetErrors();
            return;
        }

        // Обычные строки ядра без ошибки — это сайты пользователя, они не нужны.
        if (NetLogClassifier.IsCoreLine(clean))
            return;

        var now = _now();
        lock (_netErrors)
        {
            if (now - _logWindowStart > TimeSpan.FromMinutes(10))
            {
                if (_logSuppressed > 0)
                    Rec(Ev.LogSuppressed, ("n", Num(_logSuppressed)));
                _logWindowStart = now;
                _logWindowCount = 0;
                _logSuppressed = 0;
            }

            if (++_logWindowCount > LogLinesPerWindow)
            {
                _logSuppressed++;
                return;
            }
        }

        Rec(Ev.LogLine, ("l", NetLogClassifier.Anonymize(clean)));
    }

    /// <summary>Сетевые ошибки за минуту: одна запись на класс и шаблон с числом повторов.</summary>
    private void FlushNetErrors()
    {
        List<KeyValuePair<(string Code, string Source, string Template), int>> items;
        lock (_netErrors)
        {
            if (_netErrors.Count == 0)
                return;
            items = [.. _netErrors];
            _netErrors.Clear();
        }

        var connected = Status.State == ConnectionState.Connected ? "1" : "0";
        foreach (var ((code, source, template), count) in items)
            Rec(code, ("n", Num(count)), ("src", source), ("tpl", template), ("connected", connected));
    }

    /// <summary>Необработанная ошибка интерфейса — пишется сразу в очередь, уйдёт при следующей отправке.</summary>
    public void RecordCrash(Exception ex, string where)
    {
        ArgumentNullException.ThrowIfNull(ex);
        RecUrgent(Ev.AppCrash, ("where", where), ("type", ex.GetType().FullName), ("msg", NetLogClassifier.Anonymize(ex.Message)),
            ("stack", new StackTrace(ex, fNeedFileInfo: false).ToString()));
    }

    /// <summary>Действие пользователя, которое движок сам не видит (например, проверка обновлений из окна).</summary>
    public void RecordAction(string code, params (string Key, string? Value)[] fields) => RecCore(code, fields, urgent: false);

    private async Task StopTelemetryAsync()
    {
        if (_netErrorsTimer is not null)
            await _netErrorsTimer.DisposeAsync().ConfigureAwait(false);
        FlushNetErrors();
        if (_telemetry is { } t)
        {
            if (_startedAt != default)
                Rec(Ev.AppExit, ("uptimeMin", ((long)(_now() - _startedAt).TotalMinutes).ToString(CultureInfo.InvariantCulture)));
            await t.DisposeAsync().ConfigureAwait(false);
        }
    }
}
