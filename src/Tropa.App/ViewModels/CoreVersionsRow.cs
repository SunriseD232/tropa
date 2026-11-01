using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Model;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.Updates;

namespace Tropa.App.ViewModels;

/// <summary>
/// «Версии ядер» (ADR-058): ручное обновление, выбор версии (в том числе preview и с GitHub) и откат.
/// Выбор начинает работать со следующего подключения.
/// </summary>
internal sealed partial class CoreVersionsRow : SettingRow
{
    private readonly TropaEngine _engine;

    public CoreVersionsRow(TropaEngine engine)
        : base("Версии ядер", "coreVersions", null, "Своя версия sing-box или Xray вместо версии выпуска. Применяется при следующем подключении.")
    {
        _engine = engine;
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() => _ = LoadAsync(online: false));
    }

    public ObservableCollection<CoreVersionItem> Cores { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool Busy { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    protected override void OnLoad(AppSettings settings) => _ = LoadAsync(online: false);

    private bool CanRun() => !Busy;

    /// <summary>Спросить манифест и (если разрешено) GitHub, какие версии есть.</summary>
    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        await LoadAsync(online: true);
    }

    private async Task LoadAsync(bool online)
    {
        if (Busy)
            return;
        Busy = true;
        if (online)
            Report("Узнаю, какие версии есть…");
        try
        {
            var notes = new List<string>();
            var list = await Task.Run(() => _engine.ListCoreVersionsAsync(notes, online));
            Cores.Clear();
            foreach (var info in list)
                Cores.Add(new CoreVersionItem(info, this));
            if (online)
            {
                var count = list.Sum(i => i.Options.Count(o => !o.Installed));
                Report(notes.Count > 0 ? string.Join(" ", notes)
                    : count == 0 ? "Других версий нет." + (_engine.State.Settings.Cores.GitHubCores ? "" : " Выпуски с GitHub можно включить пунктом ниже.")
                    : null, error: notes.Count > 0);
            }
        }
        finally
        {
            Busy = false;
        }
    }

    internal async Task RunAsync(Func<IProgress<string>, Task> action, string done)
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            // Progress создаём здесь: он запомнит поток интерфейса и будет сообщать в него.
            var progress = new Progress<string>(m => Report(m));
            await Task.Run(() => action(progress));
            Report(done + (_engine.Status.State == ConnectionState.Connected ? " Заработает после переподключения." : ""));
        }
        catch (Exception ex) when (ex is IntegrityException or HttpRequestException or TaskCanceledException or InvalidOperationException
            or InvalidDataException or IOException or ServiceException or UnauthorizedAccessException)
        {
            Report(ex.Message, error: true);
        }
        finally
        {
            Busy = false;
        }

        await LoadAsync(online: false);
    }

    internal TropaEngine Engine => _engine;

    private void Report(string? text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }
}

/// <summary>Одно ядро в «Версиях ядер».</summary>
internal sealed partial class CoreVersionItem : ObservableObject
{
    private readonly CoreVersionsInfo _info;
    private readonly CoreVersionsRow _row;

    public CoreVersionItem(CoreVersionsInfo info, CoreVersionsRow row)
    {
        _info = info;
        _row = row;
        Options = info.Options;
        SelectedOption = info.Options.FirstOrDefault(o => !o.Selected);
        row.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CoreVersionsRow.Busy))
            {
                InstallCommand.NotifyCanExecuteChanged();
                RollbackCommand.NotifyCanExecuteChanged();
                ReleaseCommand.NotifyCanExecuteChanged();
            }
        };
    }

    public string Name => _info.Core == "xray" ? "Xray" : "sing-box";

    public string Current => _info.SelectedVersion is { } v
        ? $"Сейчас {v} — выбрана вами (в выпуске {_info.ReleaseVersion})"
        : $"Сейчас {_info.ReleaseVersion} — как в выпуске";

    public IReadOnlyList<CoreVersionOption> Options { get; }

    public bool HasOptions => Options.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Warning))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial CoreVersionOption? SelectedOption { get; set; }

    /// <summary>Чем рискует пользователь с выбранной в списке версией.</summary>
    public string? Warning => SelectedOption switch
    {
        { Source: CorePickSource.GitHub, Preview: true } => "Выпуск с GitHub, владелец его не проверял, и это preview: может работать нестабильно. Хэш сверяется с данными GitHub.",
        { Source: CorePickSource.GitHub } => "Выпуск с GitHub, владелец его не проверял. Хэш сверяется с данными GitHub.",
        { Preview: true } => "Preview-версия: может работать нестабильно.",
        _ => null,
    };

    public string InstallLabel => SelectedOption?.Installed == true ? "Выбрать" : "Скачать и выбрать";

    public bool CanRollback => _info.PreviousVersion is not null && _info.PreviousVersion != (_info.SelectedVersion ?? "");

    public string RollbackLabel => _info.PreviousVersion is { Length: > 0 } p ? $"Вернуть прежнюю ({p})" : "Вернуть прежнюю (как в выпуске)";

    public bool IsPicked => _info.SelectedVersion is not null;

    partial void OnSelectedOptionChanged(CoreVersionOption? value) => OnPropertyChanged(nameof(InstallLabel));

    private bool CanInstall() => !_row.Busy && SelectedOption is { Selected: false };

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync()
    {
        var option = SelectedOption!;
        return _row.RunAsync(p => _row.Engine.InstallCoreVersionAsync(option, p), $"Выбрана версия {Name} {option.Version}.");
    }

    private bool CanRollbackNow() => !_row.Busy && CanRollback;

    [RelayCommand(CanExecute = nameof(CanRollbackNow))]
    private Task RollbackAsync()
    {
        var previous = _info.PreviousVersion is { Length: > 0 } p ? p : null;
        return _row.RunAsync(_ => _row.Engine.SelectCoreVersionAsync(_info.Core, previous),
            previous is null ? $"{Name}: снова версия выпуска." : $"{Name}: возвращена версия {previous}.");
    }

    private bool CanRelease() => !_row.Busy && IsPicked;

    [RelayCommand(CanExecute = nameof(CanRelease))]
    private Task ReleaseAsync() =>
        _row.RunAsync(_ => _row.Engine.SelectCoreVersionAsync(_info.Core, null), $"{Name}: снова версия выпуска.");
}
