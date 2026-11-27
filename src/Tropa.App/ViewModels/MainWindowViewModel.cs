using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Model;
using Tropa.Infrastructure;

namespace Tropa.App.ViewModels;

/// <summary>Заглушка раздела, который появится на следующих этапах.</summary>
internal sealed class PlaceholderViewModel(string title, string text)
{
    public string Title { get; } = title;
    public string Text { get; } = text;
}

internal sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly TropaEngine _engine;

    public MainWindowViewModel(TropaEngine engine)
    {
        _engine = engine;
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(CoreVersions)));
        Info = new InfoViewModel();
        Import = new ImportViewModel(engine, Info);
        Home = new HomeViewModel(engine, Info);
        Edit = new EditServerViewModel(engine, Info);
        Servers = new ServersViewModel(engine, Info, () => Import.Open(), Edit);
        GeoEditor = new GeoListEditorViewModel(engine, Info);
        Rules = new RulesViewModel(engine, Info, GeoEditor);
        Routes = new RoutesViewModel(engine, Info, GeoEditor, Rules);
        Games = new GamesViewModel(engine, Info);
        Diagnostics = new DiagnosticsViewModel(engine, Info);
        Settings = new SettingsViewModel(engine, Info, page => Navigate(page), Home.Updates);
        Onboarding = new OnboardingViewModel(engine, Info);
        CurrentPage = Home;
        Warning = App.StartupNotice ?? engine.StartupWarning ?? PortableImportHint(engine);
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            FooterStatus = s.State != ConnectionState.Connected ? "Отключено" : _engine.NoNetwork ? "Подключено · нет сети" : "Подключено";
            FooterConnected = s.State == ConnectionState.Connected && !_engine.NoNetwork;
        });
        IsV2rayLayout = engine.State.Settings.General.Layout == WindowLayout.V2rayN;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            IsV2rayLayout = _engine.State.Settings.General.Layout == WindowLayout.V2rayN;
            // Переключили вид экрана маршрутов в настройках, пока он открыт, — показываем выбранный.
            if (IsRules && CurrentPage != RoutesPage())
                CurrentPage = RoutesPage();
        });
        // Журнал внизу вида «как в v2rayN»: строки уже без секретов (RecentLog), обновление не чаще раза в секунду.
        _logTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => FlushLog());
        engine.Log += (_, _) => _logDirty = true;
        _logTimer.Start();
    }

    /// <summary>Портативная Тропа без серверов, а на компьютере есть установленная — подсказываем перенос (ADR-063).</summary>
    private static string? PortableImportHint(TropaEngine engine) =>
        Infrastructure.Service.PortableMode.IsPortable && engine.State.Profiles.Count == 0
            && Infrastructure.Storage.PortableImport.InstalledDataExists(Infrastructure.Storage.AppPaths.Roaming)
            ? "На компьютере есть установленная Тропа. Её серверы, подписки и настройки можно перенести сюда: «Настройки → Общие → Настройки из установленной Тропы»."
            : null;

    private readonly DispatcherTimer _logTimer;
    private volatile bool _logDirty = true;

    /// <summary>Вид «как в v2rayN» (ADR-051): окно подменяет только разметку, модели те же.</summary>
    [ObservableProperty]
    public partial bool IsV2rayLayout { get; set; }

    /// <summary>В виде «как в v2rayN» вместо экрана — таблица серверов (главная и «Серверы»).</summary>
    public bool ShowsServerTable => CurrentPage == Home || CurrentPage == Servers;

    /// <summary>Экран поверх таблицы (параметры, правила, диагностика); null — показана таблица.</summary>
    public object? PageContent => ShowsServerTable ? null : CurrentPage;

    public string PageTitle => CurrentPage switch
    {
        RulesViewModel or RoutesViewModel => "Маршруты",
        GamesViewModel => "Игры",
        DiagnosticsViewModel => "Диагностика",
        SettingsViewModel => "Параметры",
        _ => "",
    };

    [ObservableProperty]
    public partial string? LogFilter { get; set; }

    partial void OnLogFilterChanged(string? value) => _logDirty = true;

    [ObservableProperty]
    public partial string LogText { get; set; } = "";

    private void FlushLog()
    {
        if (!_logDirty || !IsV2rayLayout)
            return;
        _logDirty = false;
        var lines = _engine.RecentLog.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(LogFilter))
            lines = lines.Where(l => l.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase));
        LogText = string.Join(Environment.NewLine, lines.TakeLast(200));
    }

    partial void OnIsV2rayLayoutChanged(bool value)
    {
        _logDirty = true;
        if (!value)
            Servers.ResetView();
    }

    [RelayCommand]
    private void SwitchLayout(string layout)
    {
        var target = layout == "v2rayn" ? WindowLayout.V2rayN : WindowLayout.Tropa;
        if (_engine.State.Settings.General.Layout != target)
            _engine.UpdateSettings(s => s with { General = s.General with { Layout = target } });
    }

    [RelayCommand]
    private void ShowInfo(string key) => Info.Show(key);

    [RelayCommand]
    private async Task RestartElevatedAsync()
    {
        if (await App.RestartElevatedAsync() is { } reason)
            Warning = reason;
    }

    public InfoViewModel Info { get; }
    public ImportViewModel Import { get; }
    public EditServerViewModel Edit { get; }
    public HomeViewModel Home { get; }
    public ServersViewModel Servers { get; }
    public GeoListEditorViewModel GeoEditor { get; }
    public RulesViewModel Rules { get; }
    public RoutesViewModel Routes { get; }
    public GamesViewModel Games { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public SettingsViewModel Settings { get; }
    public OnboardingViewModel Onboarding { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsServers), nameof(IsRules), nameof(IsGames), nameof(IsDiagnostics), nameof(IsSettings), nameof(ShowsServerTable), nameof(PageTitle), nameof(PageContent))]
    public partial object CurrentPage { get; set; }

    [ObservableProperty]
    public partial string FooterStatus { get; set; } = "Отключено";

    [ObservableProperty]
    public partial bool FooterConnected { get; set; }

    [ObservableProperty]
    public partial string? Warning { get; set; }

    public bool IsHome => CurrentPage == Home;
    public bool IsServers => CurrentPage == Servers;
    public bool IsRules => CurrentPage == Rules || CurrentPage == Routes;

    /// <summary>Экран маршрутов: новый (ADR-067) или прежний «Правила» — по настройке.</summary>
    private object RoutesPage() => _engine.State.Settings.General.ClassicRoutes ? Rules : Routes;

    private object ShowRoutes()
    {
        if (RoutesPage() == Routes)
            Routes.OnShown();
        return RoutesPage();
    }
    public bool IsGames => CurrentPage == Games;
    public bool IsDiagnostics => CurrentPage == Diagnostics;
    public bool IsSettings => CurrentPage == Settings;

    public string CoreVersions => _engine.CoreVersions;

    [RelayCommand]
    private void Navigate(string page) => CurrentPage = page switch
    {
        "servers" => Servers,
        "rules" => ShowRoutes(),
        "games" => ShowGames(),
        "diagnostics" => ShowDiagnostics(),
        "settings" => ShowSettingsHome(),
        "settings:cores" => ShowSettings("cores"),
        _ => Home,
    };

    private SettingsViewModel ShowSettingsHome()
    {
        // Вход в «Настройки» — всегда плитки разделов (ADR-072).
        Settings.ShowOverviewCommand.Execute(null);
        return Settings;
    }

    private SettingsViewModel ShowSettings(string section)
    {
        Settings.ShowSection(section);
        return Settings;
    }

    private GamesViewModel ShowGames()
    {
        Games.OnShown();
        return Games;
    }

    private DiagnosticsViewModel ShowDiagnostics()
    {
        Diagnostics.OnShown();
        return Diagnostics;
    }

    [RelayCommand]
    private void DismissWarning() => Warning = null;

    /// <summary>Ctrl+V в любом месте окна: если в буфере ссылка или подписка — сразу открываем импорт.</summary>
    public void OnPaste(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        var t = text.Trim();
        if (t.Contains("://", StringComparison.Ordinal) || t.StartsWith('{') || t.Length > 40)
            Import.Open(t);
    }
}
