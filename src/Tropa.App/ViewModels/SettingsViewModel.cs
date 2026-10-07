using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Compatibility;
using Tropa.Core.Model;
using Tropa.Infrastructure;
using Tropa.Infrastructure.SystemIntegration;
using V = Tropa.Core.Model.SettingsValidation;

namespace Tropa.App.ViewModels;

/// <summary>
/// Экран «Настройки» (docs/06-features.md, §5). Показаны только работающие пункты; у каждого «i»,
/// недоступные выключены с причиной из CompatRules. Изменения сохраняются сразу, а если Тропа
/// подключена — предлагается переподключиться.
/// </summary>
internal sealed partial class SettingsViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private readonly Action<string> _navigate;

    public SettingsViewModel(TropaEngine engine, InfoViewModel info, Action<string> navigate)
    {
        _engine = engine;
        _info = info;
        _navigate = navigate;
        Sections =
        [
            General(),
            Subscriptions(),
            Connection(),
            Dns(),
            Dpi(),
            CoresSection(),
            Expert(),
        ];
        foreach (var row in Sections.SelectMany(s => s.Rows))
        {
            row.Commit = Commit;
            row.ShowInfo = (key, reason) => _info.Show(key, reason);
        }

        Current = Sections[0];
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Load);
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(Load);
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            if (s.State != ConnectionState.Connected)
                NeedsApply = false;
        });
        Load();
    }

    public IReadOnlyList<SettingsSection> Sections { get; }

    [ObservableProperty]
    public partial SettingsSection Current { get; set; }

    [ObservableProperty]
    public partial bool NeedsApply { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool MessageIsError { get; set; }

    [ObservableProperty]
    public partial string? ConfigPreview { get; set; }

    /// <summary>Файл настроек: экспорт и импорт делает вид (диалог выбора файла).</summary>
    public event EventHandler? ExportRequested;

    public event EventHandler? ImportRequested;

    [RelayCommand]
    private void Select(SettingsSection section) => Current = section;

    partial void OnCurrentChanged(SettingsSection value)
    {
        foreach (var s in Sections)
            s.IsCurrent = ReferenceEquals(s, value);
    }

    private void Load()
    {
        // Без службы «Весь компьютер» недоступен — пункты TUN показываем выключенными с причиной.
        var settings = _engine.State.Settings;
        var effective = !_engine.TunAvailable && settings.Connection.Mode == CaptureMode.Tun
            ? settings with { Connection = settings.Connection with { Mode = CaptureMode.SystemProxy } }
            : settings;
        var compat = CompatRules.Evaluate(effective, _engine.State.ActiveProfile);
        if (!ReferenceEquals(effective, settings))
        {
            // Режим выбран, но служба не установлена — так и пишем, а не «работает только в режиме…».
            var tunOnly = CompatRules.Evaluate(settings with { Connection = settings.Connection with { Mode = CaptureMode.SystemProxy } }).Disabled
                .Where(kv => kv.Value.Contains("«Весь компьютер»", StringComparison.Ordinal)).Select(kv => kv.Key).ToHashSet();
            compat = compat with
            {
                Disabled = compat.Disabled.ToDictionary(kv => kv.Key,
                    kv => tunOnly.Contains(kv.Key) ? "нужна служба Тропы — без неё режим «Весь компьютер» не работает" : kv.Value),
            };
        }
        foreach (var row in Sections.SelectMany(s => s.Rows))
            row.Load(_engine.State.Settings, compat);
    }

    private void Commit(Func<AppSettings, AppSettings> change)
    {
        _engine.UpdateSettings(change);
        NeedsApply = _engine.Status.State == ConnectionState.Connected;
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        NeedsApply = false;
        await _engine.ApplyIfConnectedAsync();
    }

    private void Report(string? text, bool error = false)
    {
        Message = text;
        MessageIsError = error;
    }

    // ---------------- Разделы ----------------

    private static string[] Opts(params string[] o) => o;

    private SettingsSection General() => new("general", "Общие", null,
    [
        new ActionRow("Автонастройка", "autoSetup", "Включает рекомендуемые значения всех базовых пунктов. Правила, серверы, тема и горячая клавиша не меняются.",
            ("Включить рекомендуемые", new RelayCommand(ApplyRecommended))),
        new ToggleRow("Запускать вместе с Windows", "autostart", s => s.General.Autostart, (s, v) =>
        {
            SyncAutostart(v);
            return s with { General = s.General with { Autostart = v } };
        }),
        new ToggleRow("Подключаться сразу после запуска", "autoconnect", s => s.General.Autoconnect, (s, v) => s with { General = s.General with { Autoconnect = v } }),
        new ToggleRow("Запускаться свёрнутой в трей", "startMin", s => s.General.StartMin, (s, v) => s with { General = s.General with { StartMin = v } }),
        new ToggleRow("Ждать сеть перед подключением", "waitNet", s => s.General.WaitNet, (s, v) => s with { General = s.General with { WaitNet = v } }, hint: "До 30 секунд после входа в Windows."),
        new ToggleRow("Переподключаться после сна и смены сети", "reconnect", s => s.General.Reconnect, (s, v) => s with { General = s.General with { Reconnect = v } }),
        new ToggleRow("Аварийная блокировка (kill switch)", "killSwitch", s => s.General.KillSwitch, (s, v) => s with { General = s.General with { KillSwitch = v } }),
        new ToggleRow("Блокировать IPv6 мимо сервера", "ipv6Block", s => s.General.Ipv6Block, (s, v) => s with { General = s.General with { Ipv6Block = v } }),
        new ToggleRow("Пароль на локальный прокси", "localPass", s => s.General.LocalPass, (s, v) => s with { General = s.General with { LocalPass = v } }),
        new TextRow("Горячая клавиша подключения", "hotkey", s => s.General.Hotkey, (s, v) =>
        {
            var error = App.RegisterHotkey(v);
            if (error is not null)
                Dispatcher.UIThread.Post(() => Report(error, error: true));
            return s with { General = s.General with { Hotkey = v } };
        }, V.Hotkey, placeholder: "Ctrl+Alt+P"),
        new ChoiceRow("Тема", "theme", Opts("Тёмная", "Светлая", "Как в Windows"),
            s => s.General.Theme switch { AppTheme.Light => 1, AppTheme.System => 2, _ => 0 },
            (s, i) =>
            {
                var theme = i switch { 1 => AppTheme.Light, 2 => AppTheme.System, _ => AppTheme.Dark };
                App.ApplyTheme(theme);
                return s with { General = s.General with { Theme = theme } };
            }),
        new ActionRow("Резервная копия настроек и правил", "backup", "Серверы, подписки и ключи в файл не попадают — им можно поделиться.",
            ("Сохранить в файл…", new RelayCommand(() => ExportRequested?.Invoke(this, EventArgs.Empty))),
            ("Загрузить из файла…", new RelayCommand(() => ImportRequested?.Invoke(this, EventArgs.Empty)))),
    ]);

    private static SettingsSection Subscriptions() => new("subs", "Подписки", "Значения для подписок. Обновить вручную можно на экране «Серверы».",
    [
        new ChoiceRow("Обновлять подписки", "subUpdate", Opts("Каждые 6 часов", "Каждые 12 часов", "Раз в сутки", "Только вручную"),
            s => s.Subscriptions.UpdateHours switch { 6 => 0, 12 => 1, 24 => 2, _ => 3 },
            (s, i) => s with { Subscriptions = s.Subscriptions with { UpdateHours = i switch { 0 => 6, 1 => 12, 2 => 24, _ => 0 } } }),
        new ToggleRow("Обновлять через подключение", "subViaProxy", s => s.Subscriptions.SubViaProxy, (s, v) => s with { Subscriptions = s.Subscriptions with { SubViaProxy = v } }),
        new ChoiceRow("Представляться провайдеру как", "subUA", Opts("Тропа", "v2rayN", "sing-box", "Свой"),
            s => (int)s.Subscriptions.SubUA, (s, i) => s with { Subscriptions = s.Subscriptions with { SubUA = (UserAgentMode)i } }),
        new TextRow("Свой User-Agent", "subUA", s => s.Subscriptions.SubUACustom, (s, v) => s with { Subscriptions = s.Subscriptions with { SubUACustom = v } },
            V.UserAgent, compatKey: "subUACustom"),
        new ToggleRow("Отправлять идентификатор устройства (HWID)", "hwid", s => s.Subscriptions.Hwid, (s, v) => s with { Subscriptions = s.Subscriptions with { Hwid = v } }),
    ]);

    private SettingsSection Connection() => new("connection", "Подключение", null,
    [
        new ChoiceRow("Что пускать через Тропу", "mode", Opts("Весь компьютер (TUN)", "Только браузеры (прокси Windows)", "Только порты (без изменений в системе)"),
            s => (int)s.Connection.Mode, (s, i) => s with { Connection = s.Connection with { Mode = (CaptureMode)i } }),
        new ChoiceRow("Сетевой стек TUN", "tunStack", Opts("Смешанный (рекомендуется)", "gVisor", "Системный"),
            s => (int)s.Connection.TunStack, (s, i) => s with { Connection = s.Connection with { TunStack = (TunStackKind)i } }),
        TextRow.Number("MTU", "mtu", s => s.Connection.Mtu, (s, v) => s with { Connection = s.Connection with { Mtu = v } }, V.Mtu),
        new ToggleRow("Строгий маршрут", "strictRoute", s => s.Connection.StrictRoute, (s, v) => s with { Connection = s.Connection with { StrictRoute = v } }),
        new ToggleRow("Локальная сеть напрямую", "lanBypass", s => s.Connection.LanBypass, (s, v) => s with { Connection = s.Connection with { LanBypass = v } }),
        new TextRow("Исключения прокси Windows", "sysBypass", s => s.Connection.SysBypass, (s, v) => s with { Connection = s.Connection with { SysBypass = v } }, V.SysBypass),
        new ActionRow("Приложения из Microsoft Store", "uwpLoopback", "Выбираются на экране «Правила».",
            ("Открыть «Правила»", new RelayCommand(() => _navigate("rules")))),
        TextRow.Number("Порт прокси (SOCKS5 и HTTP)", "ports", s => s.Connection.SocksPort, (s, v) => s with { Connection = s.Connection with { SocksPort = v } },
            v => V.Port(v, _engine.State.Settings.Connection.LanPort), hint: "Только 127.0.0.1."),
        new ToggleRow("Случайный порт при каждом подключении", "ports", s => s.Connection.RandomPorts, (s, v) => s with { Connection = s.Connection with { RandomPorts = v } },
            compatKey: "randomPorts", hint: "Программы с вручную заданным портом тогда не подключатся."),
        new ToggleRow("Пускать устройства из локальной сети", "lanAllow", s => s.Connection.LanAllow, (s, v) => s with { Connection = s.Connection with { LanAllow = v } }),
        TextRow.Number("Порт для локальной сети", "ports", s => s.Connection.LanPort, (s, v) => s with { Connection = s.Connection with { LanPort = v } },
            v => V.Port(v, _engine.State.Settings.Connection.SocksPort), compatKey: "lanPort", hint: "Всегда с паролем."),
        new ToggleRow("Авто-выбор лучшего сервера", "autoSelect", s => s.Connection.AutoSelect, (s, v) => s with { Connection = s.Connection with { AutoSelect = v } }),
        new ChoiceRow("Проверять задержку", "autoInterval", Opts("Каждые 3 минуты", "Каждые 10 минут", "Каждые 30 минут"),
            s => s.Connection.AutoIntervalMinutes switch { 10 => 1, 30 => 2, _ => 0 },
            (s, i) => s with { Connection = s.Connection with { AutoIntervalMinutes = i switch { 1 => 10, 2 => 30, _ => 3 } } }),
        TextRow.Number("Допуск при переключении, мс", "autoTol", s => s.Connection.AutoTolMs, (s, v) => s with { Connection = s.Connection with { AutoTolMs = v } },
            v => v is >= 0 and <= 1000 ? null : "От 0 до 1000 мс."),
        new TextRow("Адрес проверки задержки", "testUrl", s => s.Connection.TestUrl, (s, v) => s with { Connection = s.Connection with { TestUrl = v } },
            v => V.HttpUrl(v, httpsOnly: false)),
        new TextRow("Адрес теста скорости", "speedTest", s => s.Connection.SpeedUrl, (s, v) => s with { Connection = s.Connection with { SpeedUrl = v } },
            v => V.HttpUrl(v, httpsOnly: true)),
        new ChoiceRow("Объём теста скорости", "speedTest", Opts("1 МБ", "10 МБ", "25 МБ"),
            s => s.Connection.SpeedSizeMb switch { 1 => 0, 25 => 2, _ => 1 },
            (s, i) => s with { Connection = s.Connection with { SpeedSizeMb = i switch { 0 => 1, 2 => 25, _ => 10 } } }),
        new ToggleRow("Проверять UDP", "udpTest", s => s.Connection.UdpTestOn, (s, v) => s with { Connection = s.Connection with { UdpTestOn = v } }),
        new TextRow("STUN-сервер для проверки UDP", "udpTest", s => s.Connection.StunServer, (s, v) => s with { Connection = s.Connection with { StunServer = v } },
            V.HostPort, compatKey: "stunServer"),
        new ChoiceRow("Проверять серверов одновременно", "parallel", Opts("2", "5", "10"),
            s => s.Connection.Parallel switch { 2 => 0, 10 => 2, _ => 1 },
            (s, i) => s with { Connection = s.Connection with { Parallel = i switch { 0 => 2, 2 => 10, _ => 5 } } }),
    ]);

    private SettingsSection Dns() => new("dns", "DNS", null,
    [
        new TextRow("Удалённый DNS (через сервер)", "remoteDns", s => s.Dns.RemoteDns, (s, v) => s with { Dns = s.Dns with { RemoteDns = v } }, V.Dns,
            placeholder: "https://1.1.1.1/dns-query"),
        new TextRow("Локальный DNS (напрямую)", "localDns", s => s.Dns.LocalDns, (s, v) => s with { Dns = s.Dns with { LocalDns = v } }, V.Dns,
            placeholder: "77.88.8.8"),
        new ChoiceRow("Что спрашивать у локального DNS", "localDnsRule", Opts("Российские домены", "Всё, что идёт напрямую", "Ничего"),
            s => (int)s.Dns.LocalDnsRule, (s, i) => s with { Dns = s.Dns with { LocalDnsRule = (LocalDnsRule)i } }),
        new ToggleRow("FakeIP", "fakeip", s => s.Dns.Fakeip, (s, v) => s with { Dns = s.Dns with { Fakeip = v } }),
        new ToggleRow("Определять домен по трафику (sniffing)", "sniffing", s => s.Dns.Sniffing, (s, v) => s with { Dns = s.Dns with { Sniffing = v } }),
        new ToggleRow("Перехватывать DNS программ", "dnsHijack", s => s.Dns.DnsHijack, (s, v) => s with { Dns = s.Dns with { DnsHijack = v } }),
        new ToggleRow("Отключать умное разрешение имён Windows", "smartNameRes", s => s.Dns.SmartNameRes, (s, v) => s with { Dns = s.Dns with { SmartNameRes = v } }),
        new ToggleRow("Кэш DNS", "dnsCache", s => s.Dns.DnsCache, (s, v) => s with { Dns = s.Dns with { DnsCache = v } }),
        new TextRow("Свои адреса (hosts)", "hosts", s => s.Dns.Hosts, (s, v) => s with { Dns = s.Dns with { Hosts = v } }, V.Hosts,
            multiline: true, placeholder: "router.lan 192.168.1.1"),
        new ActionRow("Проверка домена", "dnsCheck", "Сравнивает ответ DNS провайдера и ответ через туннель.",
            ("Открыть «Диагностику»", new RelayCommand(() => _navigate("diagnostics")))),
    ]);

    // Ручная правка любого приёма обхода переводит набор в «Свои» (docs/06-features.md, §5).
    private static AppSettings Custom(AppSettings s, Func<DpiSettings, DpiSettings> change) =>
        s with { Dpi = change(s.Dpi) with { DpiPreset = DpiPreset.Custom } };

    private static SettingsSection Dpi() => new("dpi", "Обход DPI", "Только для трафика, который идёт напрямую, и только средствами ядер — без сторонних драйверов.",
    [
        new ToggleRow("Сначала обход DPI, при неудаче — через сервер", "dpiFirst", s => s.Routing.DpiFirst,
            (s, v) => s with { Routing = s.Routing with { DpiFirst = v } }),
        new ChoiceRow("Набор", "dpiPreset", Opts("Выключено", "Мягко", "Агрессивно", "Свои"),
            s => (int)s.Dpi.DpiPreset, (s, i) => s with { Dpi = s.Dpi.WithPreset((DpiPreset)i) }),
        new ToggleRow("Фрагментация TLS", "fragment", s => s.Dpi.Fragment, (s, v) => Custom(s, d => d with { Fragment = v })),
        new ChoiceRow("Каким сайтам", "fragScope", Opts("Из списка (YouTube и связанные)", "Всем прямым HTTPS"),
            s => (int)s.Dpi.FragScope, (s, i) => Custom(s, d => d with { FragScope = (FragmentScope)i }), compatKey: "fragParams"),
        new TextRow("Какие пакеты дробить", "fragment", s => s.Dpi.FragPackets, (s, v) => Custom(s, d => d with { FragPackets = v }), V.FragPackets,
            compatKey: "fragParams", hint: "Длина, интервал и пакеты работают, когда прямой трафик идёт через Xray (включён шум)."),
        new TextRow("Длина фрагментов, байт", "fragment", s => s.Dpi.FragLen, (s, v) => Custom(s, d => d with { FragLen = v }), v => V.Range(v, 1, 1000),
            compatKey: "fragParams"),
        new TextRow("Интервал между фрагментами, мс", "fragment", s => s.Dpi.FragInt, (s, v) => Custom(s, d => d with { FragInt = v }), v => V.Range(v, 0, 1000),
            compatKey: "fragParams"),
        new ChoiceRow("Отпечаток браузера (uTLS)", "utls", Opts("Chrome", "Firefox", "Edge", "Safari", "Случайный"),
            s => s.Dpi.Utls switch { "firefox" => 1, "edge" => 2, "safari" => 3, "random" or "randomized" => 4, _ => 0 },
            (s, i) => s with { Dpi = s.Dpi with { Utls = i switch { 1 => "firefox", 2 => "edge", 3 => "safari", 4 => "random", _ => "chrome" } } }),
        new ToggleRow("Предупреждать о серверах без проверки сертификата", "allowInsecureWarn", s => s.Dpi.AllowInsecureWarn,
            (s, v) => s with { Dpi = s.Dpi with { AllowInsecureWarn = v } }, hint: "Такие серверы не попадают в авто-выбор."),
        new ToggleRow("UDP-шум", "noise", s => s.Dpi.Noise, (s, v) => Custom(s, d => d with { Noise = v })),
        new ChoiceRow("Вид шума", "noise", Opts("Случайные байты", "Строка", "Base64"),
            s => s.Dpi.NoiseType switch { "str" => 1, "base64" => 2, _ => 0 },
            (s, i) => Custom(s, d => d with { NoiseType = i switch { 1 => "str", 2 => "base64", _ => "rand" } }), compatKey: "noiseParams"),
        new TextRow("Длина шума, байт", "noise", s => s.Dpi.NoiseLen, (s, v) => Custom(s, d => d with { NoiseLen = v }), v => V.Range(v, 1, 2000), compatKey: "noiseParams"),
        new TextRow("Задержка шума, мс", "noise", s => s.Dpi.NoiseDelay, (s, v) => Custom(s, d => d with { NoiseDelay = v }), v => V.Range(v, 0, 1000), compatKey: "noiseParams"),
        new ToggleRow("Mux (несколько соединений в одном)", "mux", s => s.Dpi.Mux, (s, v) => s with { Dpi = s.Dpi with { Mux = v } }),
        TextRow.Number("Соединений в одном Mux", "muxConc", s => s.Dpi.MuxConc, (s, v) => s with { Dpi = s.Dpi with { MuxConc = v } },
            v => v is >= 1 and <= 128 ? null : "От 1 до 128."),
    ]);

    private SettingsSection CoresSection() => new("cores", "Ядра и обновления", null,
    [
        new LabelRow("Версии", "cores", () => $"Тропа {TropaEngine.AppVersion.ToString(3)} · {_engine.CoreVersions}"),
        new LabelRow("Что умеют ядра", "coreMatrix", () => "sing-box: TUN, правила, DNS · Xray: XHTTP, шум"),
        new LabelRow("Списки сайтов и адресов", "geoBases", () => "Обновляются вместе с ядрами по подписанному манифесту"),
        new ChoiceRow("Какое ядро использовать", "coreChoice", Opts("Автоматически (рекомендуется)", "Всегда sing-box", "Всегда Xray"),
            s => (int)s.Cores.CoreChoice, (s, i) => s with { Cores = s.Cores with { CoreChoice = (CoreChoice)i } }),
        new ToggleRow("Проверять новые ядра и списки сайтов раз в сутки", "geoUpdate", s => s.Cores.GeoUpdate, (s, v) => s with { Cores = s.Cores with { GeoUpdate = v } }),
        new ToggleRow("Скачивать обновления через подключение", "geoViaProxy", s => s.Cores.GeoViaProxy, (s, v) => s with { Cores = s.Cores with { GeoViaProxy = v } }),
        new ToggleRow("Сообщать о новой версии Тропы", "appUpdate", s => s.Cores.AppCheck, (s, v) => s with { Cores = s.Cores with { AppCheck = v } }),
    ]);

    private SettingsSection Expert() => new("expert", "Для экспертов", "Обычно менять не нужно.",
    [
        new ToggleRow("Определять HTTP", "sniffProto", s => s.Expert.SniffHttp, (s, v) => s with { Expert = s.Expert with { SniffHttp = v } }),
        new ToggleRow("Определять TLS", "sniffProto", s => s.Expert.SniffTls, (s, v) => s with { Expert = s.Expert with { SniffTls = v } }),
        new ToggleRow("Определять QUIC", "sniffProto", s => s.Expert.SniffQuic, (s, v) => s with { Expert = s.Expert with { SniffQuic = v } }),
        new ChoiceRow("Подробность журнала ядер", "logLevel", Opts("Только ошибки и предупреждения", "Информация", "Отладка"),
            s => (int)s.Expert.LogLevel, (s, i) => s with { Expert = s.Expert with { LogLevel = (CoreLogLevel)i } },
            hint: "Отладка сильно замедляет ядро — включайте ненадолго."),
        new ActionRow("Сгенерированный конфиг", "genConfig", "Ключи и пароли скрыты.",
            ("Показать", new RelayCommand(ShowConfig)), ("Скрыть", new RelayCommand(() => ConfigPreview = null))),
        new ActionRow("Сбросить экспертные настройки", "genConfig", null,
            ("Сбросить", new RelayCommand(() => Commit(s => s with { Expert = new ExpertSettings() })))),
    ]);

    private void ApplyRecommended()
    {
        Commit(RecommendedSettings.Apply);
        SyncAutostart(true);
        Report("Включены рекомендуемые настройки." + (_engine.Status.State == ConnectionState.Connected ? " Нажмите «Применить сейчас», чтобы они заработали." : ""));
    }

    private void ShowConfig()
    {
        try
        {
            ConfigPreview = _engine.PreviewConfig();
            Report(null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Core.Generation.UnsupportedProfileException)
        {
            Report(ex.Message, error: true);
        }
    }

    internal static void SyncAutostart(bool enabled)
    {
        if (Environment.ProcessPath is not { } exe || !Autostart.IsInstalledCopy(exe))
            return;
        try
        {
            new Autostart(new RegistryRunKey()).Sync(enabled, exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    // ---------------- Резервная копия ----------------

    public string ExportText() => SettingsBackup.Export(_engine.State.Settings, DateTimeOffset.Now);

    public void ImportText(string json)
    {
        try
        {
            var known = _engine.State.Profiles.Select(p => p.Profile.Id).ToHashSet();
            var (settings, warnings) = SettingsBackup.Import(json, _engine.State.Settings, known);
            Commit(_ => settings);
            App.ApplyTheme(settings.General.Theme);
            Report(warnings.Count == 0 ? "Настройки и правила загружены." : "Загружено с замечаниями: " + string.Join(" ", warnings), error: warnings.Count > 0);
        }
        catch (InvalidDataException ex)
        {
            Report(ex.Message, error: true);
        }
    }

    public void ReportExported(string fileName) => Report("Сохранено: " + fileName);
}
