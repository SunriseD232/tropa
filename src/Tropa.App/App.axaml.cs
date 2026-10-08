using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Tropa.App.ViewModels;
using Tropa.App.Views;
using Tropa.Infrastructure;

namespace Tropa.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Живёт всё время работы процесса; иконка трея освобождается в ExitAsync")]
internal sealed partial class App : Application
{
    private TropaEngine? _engine;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _toggleItem;
    private bool _exiting;

    public static bool StartMinimized { get; set; }

    /// <summary>Запуск из автозагрузки Windows: окно по настройке startMin, подключение по autoconnect.</summary>
    public static bool FromAutostart { get; set; }

    private static App? Instance => Current as App;

    private Services.GlobalHotkey? _hotkey;
    private DispatcherTimer? _subscriptionTimer;

    /// <summary>Тема меняется сразу: палитра подключена через DynamicResource (App.axaml).</summary>
    public static void ApplyTheme(Core.Model.AppTheme theme)
    {
        if (Current is { } app)
        {
            app.RequestedThemeVariant = theme switch
            {
                Core.Model.AppTheme.Light => Avalonia.Styling.ThemeVariant.Light,
                Core.Model.AppTheme.System => Avalonia.Styling.ThemeVariant.Default,
                _ => Avalonia.Styling.ThemeVariant.Dark,
            };
        }
    }

    /// <summary>Регистрирует глобальную горячую клавишу. null — успешно.</summary>
    public static string? RegisterHotkey(string? spec) => Instance?._hotkey?.Register(spec);

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _engine = TropaEngine.Open(DataPaths(), connectService: TropaEngine.DefaultServiceConnector());
            _ = _engine.AttachServiceAsync();
            var vm = new MainWindowViewModel(_engine);
            _window = new MainWindow { DataContext = vm };
            ApplyTheme(_engine.State.Settings.General.Theme);
            CreateTray();
            _hotkey = new Services.GlobalHotkey(_window, () => Dispatcher.UIThread.Post(() => _ = ToggleAsync()));
            _hotkey.Register(_engine.State.Settings.General.Hotkey);
            _engine.StartNetworkWatch();
            // Автообновление подписок (subUpdate): первая проверка через минуту, потом раз в 15 минут.
            _subscriptionTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, async (_, _) =>
            {
                _subscriptionTimer!.Interval = TimeSpan.FromMinutes(15);
                if (_engine is { } e)
                    await e.UpdateDueSubscriptionsAsync();
            });
            _subscriptionTimer.Start();
            _engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => UpdateTray(s));
            _engine.DpiOnlyChanged += (_, _) => Dispatcher.UIThread.Post(() => { if (_engine is { } e) UpdateTray(e.Status); });

            // Выход из Windows или завершение сеанса: вернуть прокси до того, как процесс убьют.
            desktop.ShutdownRequested += (_, _) => ShutdownEngine();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownEngine();

            SyncAutostart();
            var settings = _engine.State.Settings.General;
            if (!(StartMinimized || (FromAutostart && settings.StartMin)))
                _window.Show();
            if (FromAutostart && settings.Autoconnect && _engine.State.ActiveProfile is not null)
                _ = AutoconnectAsync(_engine, settings.WaitNet);
            else if (_engine.State.Settings.Dpi.Bypass)
                _ = _engine.RestartDpiOnlyAsync(); // кнопка «Обход DPI» включена — обход работает с запуска Тропы
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static EnginePaths DataPaths()
    {
#if DEBUG
        // Только для разработки: отдельный каталог данных, чтобы проверки не трогали настоящий профиль.
        if (Environment.GetEnvironmentVariable("TROPA_DATA_DIR") is { Length: > 0 } dir)
            return new EnginePaths(Path.Combine(dir, "roaming"), Path.Combine(dir, "local"));
#endif
        return EnginePaths.Default();
    }

    private static WindowIcon LoadIcon(string name) =>
        new(AssetLoader.Open(new Uri($"avares://Tropa/Assets/{name}")));

    private void CreateTray()
    {
        _toggleItem = new NativeMenuItem("Подключить");
        _toggleItem.Click += async (_, _) =>
        {
            if (_engine!.Status.State == ConnectionState.Connected)
                await _engine.DisconnectAsync();
            else
                await _engine.ConnectAsync();
        };
        var open = new NativeMenuItem("Открыть Тропу");
        open.Click += (_, _) => ShowWindow();
        // Работает даже при сломанной конфигурации: отключает, возвращает прокси Windows и просит службу откатить всё.
        var emergency = new NativeMenuItem("Аварийно вернуть настройки сети");
        emergency.Click += async (_, _) => await _engine!.EmergencyRollbackAsync();
        var exit = new NativeMenuItem("Выход");
        exit.Click += async (_, _) => await ExitAsync();

        _tray = new TrayIcon
        {
            Icon = LoadIcon("tray-off.ico"),
            ToolTipText = "Тропа — отключено",
            Menu = new NativeMenu { Items = { _toggleItem, open, new NativeMenuItemSeparator(), emergency, exit } },
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void UpdateTray(ConnectionStatus s)
    {
        if (_tray is null || _toggleItem is null)
            return;
        (_tray.Icon, _tray.ToolTipText, _toggleItem.Header) = s.State switch
        {
            ConnectionState.Connected => (LoadIcon("tray-on.ico"), "Тропа — подключено", "Отключить"),
            ConnectionState.Connecting or ConnectionState.Disconnecting => (LoadIcon("tray-busy.ico"), "Тропа — подключение…", "Подключить"),
            ConnectionState.Error => (LoadIcon("tray-error.ico"), "Тропа — ошибка подключения", "Подключить"),
            _ => (LoadIcon("tray-off.ico"), "Тропа — отключено", "Подключить"),
        };
        if (s.State != ConnectionState.Connected && _engine is { DpiOnlyActive: true })
            _tray.ToolTipText = "Тропа — обход DPI без сервера";
    }

    /// <summary>Автозагрузка следует настройке autostart; только у установленной копии.</summary>
    private void SyncAutostart()
    {
        var exe = Environment.ProcessPath;
        if (_engine is null || exe is null || !Infrastructure.SystemIntegration.Autostart.IsInstalledCopy(exe))
            return;
        try
        {
            new Infrastructure.SystemIntegration.Autostart(new Infrastructure.SystemIntegration.RegistryRunKey()).Sync(_engine.State.Settings.General.Autostart, exe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    /// <summary>После входа в Windows сеть появляется не сразу: ждём её до 30 секунд (waitNet), затем подключаемся.</summary>
    private static async Task AutoconnectAsync(TropaEngine engine, bool waitNet)
    {
        if (waitNet)
        {
            for (var i = 0; i < 30 && !System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable(); i++)
                await Task.Delay(1000);
        }

        await engine.AttachServiceAsync();
        await engine.ConnectAsync();
    }

    private async Task ToggleAsync()
    {
        if (_engine is not { } engine)
            return;
        if (engine.Status.State == ConnectionState.Connected)
            await engine.DisconnectAsync();
        else if (engine.Status.State is ConnectionState.Disconnected or ConnectionState.Error)
            await engine.ConnectAsync();
    }

    private void ShowWindow()
    {
        if (_window is null)
            return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        _subscriptionTimer?.Stop();
        _hotkey?.Dispose();
        if (_engine is not null)
            await _engine.DisposeAsync();
        _engine = null;
        if (_tray is not null)
        {
            _tray.IsVisible = false;
            _tray.Dispose();
        }
        if (_window is not null)
        {
            _window.AllowClose = true;
            _window.Close();
        }

        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    private void ShutdownEngine()
    {
        var engine = Interlocked.Exchange(ref _engine, null);
        if (engine is null)
            return;
        // Синхронно: при завершении сеанса Windows асинхронное продолжение может не успеть.
        engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
    }
}
