using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    public MainWindowViewModel(TropaEngine engine)
    {
        Info = new InfoViewModel();
        Import = new ImportViewModel(engine, Info);
        Home = new HomeViewModel(engine, Info);
        Edit = new EditServerViewModel(engine, Info);
        Servers = new ServersViewModel(engine, Info, () => Import.Open(), Edit);
        Rules = new RulesViewModel(engine, Info);
        Diagnostics = new DiagnosticsViewModel(engine, Info);
        Settings = new PlaceholderViewModel("Настройки", "Полный экран настроек — в одной из следующих версий. Сейчас настройки хранятся с безопасными значениями по умолчанию.");
        CurrentPage = Home;
        Warning = engine.StartupWarning;
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => FooterStatus = s.State == ConnectionState.Connected ? "Подключено" : "Отключено");
    }

    public InfoViewModel Info { get; }
    public ImportViewModel Import { get; }
    public EditServerViewModel Edit { get; }
    public HomeViewModel Home { get; }
    public ServersViewModel Servers { get; }
    public RulesViewModel Rules { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public PlaceholderViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHome), nameof(IsServers), nameof(IsRules), nameof(IsDiagnostics), nameof(IsSettings))]
    public partial object CurrentPage { get; set; }

    [ObservableProperty]
    public partial string FooterStatus { get; set; } = "Отключено";

    [ObservableProperty]
    public partial string? Warning { get; set; }

    public bool IsHome => CurrentPage == Home;
    public bool IsServers => CurrentPage == Servers;
    public bool IsRules => CurrentPage == Rules;
    public bool IsDiagnostics => CurrentPage == Diagnostics;
    public bool IsSettings => CurrentPage == Settings;

    public static string CoreVersions =>
        $"sing-box {Infrastructure.Cores.PinnedFiles.Cores.Get("sing-box").Version} · Xray {Infrastructure.Cores.PinnedFiles.Cores.Get("xray").Version}";

    [RelayCommand]
    private void Navigate(string page) => CurrentPage = page switch
    {
        "servers" => Servers,
        "rules" => Rules,
        "diagnostics" => ShowDiagnostics(),
        "settings" => Settings,
        _ => Home,
    };

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
