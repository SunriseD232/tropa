using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Model;
using Tropa.Core.Testing;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Testing;

namespace Tropa.App.ViewModels;

/// <summary>
/// Первый запуск (docs/06-features.md, §1): шаг 1 — подписка, шаг 2 — режим и автозапуск,
/// шаг 3 — проверка серверов и подключение к лучшему. Можно пропустить на любом шаге.
/// </summary>
internal sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;

    public OnboardingViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        IsOpen = engine.State.Profiles.Count == 0 && !engine.State.Settings.General.OnboardingDone;
        Autostart = engine.State.Settings.General.Autostart;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1), nameof(IsStep2), nameof(IsStep3))]
    public partial int Step { get; set; } = 1;

    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ImportCommand), nameof(ConnectCommand))]
    public partial bool Busy { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool ModeTun { get; set; } = true;

    [ObservableProperty]
    public partial bool Autostart { get; set; }

    public bool TunAvailable => _engine.TunAvailable;

    public string ModeNote => _engine.TunAvailable
        ? "Рекомендуется: через Тропу идут все программы, включая игры и голос Discord."
        : "Служба Тропы не установлена, поэтому доступен только режим «Только браузеры».";

    private bool NotBusy() => !Busy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ImportAsync()
    {
        var text = Text.Trim();
        if (text.Length == 0)
        {
            Message = "Вставьте ссылку подписки (https://…) или ссылки серверов (vless://…).";
            return;
        }

        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            Message = "Адрес подписки начинается с http:// — данные пойдут без шифрования. Попросите у продавца ссылку https://.";
            return;
        }

        Busy = true;
        Message = "Загружаю…";
        try
        {
            var report = await _engine.ImportTextAsync(text, null);
            if (report.Added + report.Updated == 0)
            {
                Message = "Серверов не найдено. " + string.Join(" ", report.Messages.Take(3));
                return;
            }

            Message = null;
            ModeTun = _engine.TunAvailable;
            OnPropertyChanged(nameof(TunAvailable));
            OnPropertyChanged(nameof(ModeNote));
            Step = 2;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or InvalidDataException)
        {
            Message = "Не удалось загрузить подписку: " + _engine.Scrubber.Scrub(ex.Message);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Next()
    {
        var mode = ModeTun && _engine.TunAvailable ? CaptureMode.Tun : CaptureMode.SystemProxy;
        _engine.UpdateSettings(s => s with
        {
            Connection = s.Connection with { Mode = mode },
            General = s.General with { Autostart = Autostart, Autoconnect = Autostart },
        });
        SettingsViewModel.SyncAutostart(Autostart);
        Step = 3;
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private async Task ConnectAsync()
    {
        Busy = true;
        try
        {
            var profiles = _engine.State.Profiles.Select(p => p.Profile).ToList();
            Message = $"Проверяю серверы: {profiles.Count}…";
            await Task.Run(() => _engine.TestAsync(profiles, TestKinds.Tcp | TestKinds.Delay));
            var best = ServerHealth.PickReplacement(_engine.State.Profiles.Select(p => (p.Profile.Id, p.LastTest)), Guid.Empty, DateTimeOffset.Now);
            if (best is not { } id)
            {
                Message = "Ни один сервер сейчас не отвечает. Проверьте интернет или подписку (экран «Диагностика»).";
                return;
            }

            await _engine.SetActiveAsync(id);
            Message = "Подключаюсь к " + _engine.State.ActiveProfile?.Name + "…";
            await _engine.ConnectAsync();
            if (_engine.Status.State == ConnectionState.Connected)
                Finish();
            else
                Message = _engine.Status.Message ?? "Подключиться не удалось.";
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private void Finish()
    {
        _engine.UpdateSettings(s => s with { General = s.General with { OnboardingDone = true } });
        IsOpen = false;
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}
