using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Model;
using Tropa.Core.Testing;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Testing;

namespace Tropa.App.ViewModels;

/// <summary>
/// Первый запуск (docs/06-features.md, §1). Шаг 0 — «Настроить автоматически?»: при согласии
/// включаются рекомендуемые настройки, и после вставки ссылки Тропа сама проверяет серверы и
/// подключается. Иначе: шаг 1 — подписка, шаг 2 — режим и автозапуск, шаг 3 — подключение.
/// </summary>
internal sealed partial class OnboardingViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;

    public OnboardingViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        var g = engine.State.Settings.General;
        // Об автонастройке спрашиваем при запуске всегда, пока не ответили; мастер — пока нет серверов.
        IsOpen = !g.AutoSetupAsked || (engine.State.Profiles.Count == 0 && !g.OnboardingDone);
        Step = g.AutoSetupAsked ? 1 : 0;
        Autostart = g.Autostart;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep0), nameof(IsStep1), nameof(IsStep2), nameof(IsStep3))]
    public partial int Step { get; set; } = 1;

    public bool IsStep0 => Step == 0;
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

    /// <summary>Выбрана автонастройка: после ссылки сразу подключаемся, без шага 2.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(AddLabel))]
    public partial bool Auto { get; set; }

    public string Subtitle => Auto
        ? "Настройки включены. Осталось вставить ссылку — Тропа сама найдёт быстрый сервер и подключится."
        : "Три шага — и всё работает. Все настройки потом можно поменять.";

    public string AddLabel => Auto ? "Добавить и подключиться" : "Добавить";

    [RelayCommand]
    private void ChooseAuto()
    {
        Auto = true;
        _engine.UpdateSettings(s =>
        {
            var r = RecommendedSettings.Apply(s);
            return r with { General = r.General with { AutoSetupAsked = true } };
        });
        SettingsViewModel.SyncAutostart(true);
        if (_engine.State.Profiles.Count > 0)
        {
            Message = null;
            Finish();
            return;
        }

        Step = 1;
    }

    [RelayCommand]
    private void ChooseManual()
    {
        Auto = false;
        _engine.UpdateSettings(s => s with { General = s.General with { AutoSetupAsked = true } });
        if (_engine.State.Profiles.Count > 0)
            Finish();
        else
            Step = 1;
    }

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
            if (Auto)
            {
                // Автонастройка: всё уже включено — проверяем серверы и подключаемся.
                Step = 3;
                Busy = false;
                await ConnectAsync();
                return;
            }

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
        _engine.UpdateSettings(s => s with { General = s.General with { OnboardingDone = true, AutoSetupAsked = true } });
        IsOpen = false;
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}
