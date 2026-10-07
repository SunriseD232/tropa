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

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _engine = TropaEngine.Open(DataPaths());
            var vm = new MainWindowViewModel(_engine);
            _window = new MainWindow { DataContext = vm };
            CreateTray();
            _engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => UpdateTray(s));

            // Выход из Windows или завершение сеанса: вернуть прокси до того, как процесс убьют.
            desktop.ShutdownRequested += (_, _) => ShutdownEngine();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownEngine();

            if (!StartMinimized)
                _window.Show();
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
        var exit = new NativeMenuItem("Выход");
        exit.Click += async (_, _) => await ExitAsync();

        _tray = new TrayIcon
        {
            Icon = LoadIcon("tray-off.ico"),
            ToolTipText = "Тропа — отключено",
            Menu = new NativeMenu { Items = { _toggleItem, open, new NativeMenuItemSeparator(), exit } },
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
