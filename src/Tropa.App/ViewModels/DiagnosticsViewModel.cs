using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Diagnostics;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Diagnostics;

namespace Tropa.App.ViewModels;

/// <summary>Строка шага диагностики.</summary>
internal sealed partial class StepItemViewModel(int number, string key, string title) : ObservableObject
{
    public int Number { get; } = number;

    public string Key { get; } = key;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Glyph), nameof(IsOk), nameof(IsWarn), nameof(IsBad), nameof(IsRunning))]
    public partial StepStatus Status { get; set; } = StepStatus.Pending;

    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial string? Details { get; set; }

    [ObservableProperty]
    public partial string? Action { get; set; }

    public string Glyph => Status switch
    {
        StepStatus.Ok => "✓",
        StepStatus.Warn => "!",
        StepStatus.Bad => "✕",
        StepStatus.Running => "…",
        StepStatus.Skipped => "–",
        _ => Number.ToString(CultureInfo.InvariantCulture),
    };

    public bool IsOk => Status == StepStatus.Ok;
    public bool IsWarn => Status == StepStatus.Warn;
    public bool IsBad => Status == StepStatus.Bad;
    public bool IsRunning => Status == StepStatus.Running;

    public void Apply(StepResult r)
    {
        Status = r.Status;
        Title = r.Title;
        Details = r.Details;
        Action = r.Action;
    }

    public StepResult ToResult() => new(Key, Status, Title, Details, Action);
}

/// <summary>Экран «Диагностика» (docs/06-features.md, §4): шаги, проверка домена, отчёт, журнал.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "_cts живёт только во время проверки и освобождается в её finally")]
internal sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private CancellationTokenSource? _cts;

    public DiagnosticsViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        var n = 1;
        foreach (var (key, title) in DiagnosticsRunner.Steps)
            Steps.Add(new StepItemViewModel(n++, key, title));
    }

    public ObservableCollection<StepItemViewModel> Steps { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CheckDomainCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; set; } = "Диагностика проверяет сеть, сервер и настройки по шагам. Текущее подключение не прерывается.";

    [ObservableProperty]
    public partial string? Domain { get; set; }

    [ObservableProperty]
    public partial string? DomainResult { get; set; }

    [ObservableProperty]
    public partial bool DomainSpoofed { get; set; }

    [ObservableProperty]
    public partial string? ReportText { get; set; }

    [ObservableProperty]
    public partial bool ReportOpen { get; set; }

    [ObservableProperty]
    public partial string? LogFilter { get; set; }

    [ObservableProperty]
    public partial string LogText { get; set; } = "";

    partial void OnLogFilterChanged(string? value) => RefreshLog();

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        _cts = new CancellationTokenSource();
        Summary = "Проверяю… Это займёт до минуты: шаг «Данные идут» скачивает 10 МБ через сервер.";
        foreach (var s in Steps)
            s.Apply(new StepResult(s.Key, StepStatus.Pending, DiagnosticsRunner.Steps[s.Number - 1].Title));
        try
        {
            var progress = new Progress<StepResult>(r => Steps.FirstOrDefault(s => s.Key == r.Key)?.Apply(r));
            var results = await Task.Run(() => _engine.RunDiagnosticsAsync(progress, _cts.Token));
            // Progress доставляет асинхронно: итог применяем ещё раз, чтобы последний шаг не потерялся.
            foreach (var r in results)
                Steps.FirstOrDefault(s => s.Key == r.Key)?.Apply(r);
            var bad = results.Count(r => r.Status == StepStatus.Bad);
            var warn = results.Count(r => r.Status == StepStatus.Warn);
            Summary = (bad, warn) switch
            {
                (0, 0) => "Всё в порядке.",
                (0, _) => $"Есть замечания: {warn}. Подробности и что сделать — под каждым шагом.",
                _ => $"Найдены проблемы: {bad}, замечания: {warn}. Начните с первой красной строки — следующие часто из неё вытекают.",
            };
        }
        catch (OperationCanceledException)
        {
            Summary = "Проверка остановлена.";
        }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            RefreshLog();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CheckDomainAsync()
    {
        var domain = Core.Routing.RuleInput.NormalizeDomain(Domain);
        if (domain is null)
        {
            DomainResult = "Укажите домен, например discord.com.";
            DomainSpoofed = false;
            return;
        }

        IsRunning = true;
        DomainResult = $"Спрашиваю {domain}…";
        try
        {
            var check = await Task.Run(() => _engine.CheckDomainAsync(domain));
            DomainSpoofed = check.Verdict is DnsVerdict.Spoofed or DnsVerdict.NoAnswer;
            DomainResult = string.Join(Environment.NewLine,
                $"DNS провайдера{(check.ProviderDns is null ? "" : $" ({check.ProviderDns})")}: {Answer(check.Provider)}",
                $"Через туннель (1.1.1.1): {(_engine.State.ActiveProfile is null ? "не выбран сервер" : Answer(check.Tunnel))}",
                check.Verdict switch
                {
                    DnsVerdict.Spoofed => "Провайдер подменяет ответ. Удалённый DNS в Тропе это обходит.",
                    DnsVerdict.NoAnswer => "Провайдер не ответил на запрос об этом домене — похоже на блокировку.",
                    DnsVerdict.Same => "Ответы совпадают: подмены нет.",
                    DnsVerdict.Different => "Адреса разные, но оба настоящие — для сетей доставки контента это нормально.",
                    _ => "Сравнить не удалось: через туннель ответа нет.",
                });
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException or Core.Generation.UnsupportedProfileException)
        {
            DomainSpoofed = false;
            DomainResult = "Проверка не запустилась: " + _engine.Scrubber.Scrub(ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private static string Answer(DnsAnswer? a) => a switch
    {
        null => "нет ответа",
        { IsNxDomain: true } => "«такого домена нет»",
        { Addresses.Count: 0 } => "пустой ответ",
        _ => string.Join(", ", a.Addresses.Where(x => x.AddressFamily == AddressFamily.InterNetwork).Take(4)),
    };

    [RelayCommand]
    private void OpenReport()
    {
        var input = new ReportInput
        {
            AppVersion = typeof(DiagnosticsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?",
            CoreVersions = $"sing-box {PinnedFiles.Cores.Get("sing-box").Version}, Xray {PinnedFiles.Cores.Get("xray").Version}",
            WindowsVersion = Environment.OSVersion.VersionString,
            Settings = _engine.State.Settings,
            Profiles = _engine.State.Profiles.Select(p => p.Profile).ToList(),
            Steps = Steps.Select(s => s.ToResult()).ToList(),
            Log = _engine.RecentLog,
            Scrubber = _engine.Scrubber,
        };
        ReportText = DiagnosticReport.Build(input);
        ReportOpen = true;
    }

    [RelayCommand]
    private void CloseReport() => ReportOpen = false;

    [RelayCommand]
    private void RefreshLog()
    {
        var lines = _engine.RecentLog.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(LogFilter))
            lines = lines.Where(l => l.Contains(LogFilter.Trim(), StringComparison.OrdinalIgnoreCase));
        LogText = string.Join(Environment.NewLine, lines.TakeLast(DiagnosticReport.LogLines));
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);

    /// <summary>Вызывается при открытии экрана: журнал обновляется по месту.</summary>
    public void OnShown() => Dispatcher.UIThread.Post(RefreshLog);
}
