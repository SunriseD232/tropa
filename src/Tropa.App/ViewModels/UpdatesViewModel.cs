using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Service;
using Tropa.Infrastructure.Updates;

namespace Tropa.App.ViewModels;

/// <summary>
/// Карточка «Версии и обновления» (docs/06-features.md, «Ядра и обновления»). Автоматической установки
/// нет (docs/02-security.md, §3.8): Тропа только сообщает, ставит пользователь кнопкой.
/// </summary>
internal sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private UpdateCheck? _check;

    public UpdatesViewModel(TropaEngine engine)
    {
        _engine = engine;
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(Versions)));
        Status = engine.Updates.Enabled ? null : "Источник обновлений в этой сборке не настроен.";
        // Тихая проверка раз в сутки, если включено appCheck.
        var s = engine.State.Settings.Cores;
        if (engine.Updates.Enabled && (s.AppCheck || s.GeoUpdate) && (s.LastUpdateCheck is null || DateTimeOffset.Now - s.LastUpdateCheck > TimeSpan.FromDays(1)))
            Dispatcher.UIThread.Post(() => _ = CheckAsync(quiet: true), DispatcherPriority.Background);
    }

    public string Versions => $"Тропа {TropaEngine.AppVersion.ToString(3)} · {_engine.CoreVersions}";

    public bool Enabled => _engine.Updates.Enabled;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(InstallCoresCommand), nameof(InstallAppCommand))]
    public partial bool Busy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    public partial string? Status { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner), nameof(DismissLabel))]
    public partial string? CoresOffer { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner), nameof(DismissLabel))]
    public partial string? AppOffer { get; set; }

    /// <summary>«Позже» на плашке вида v2rayN: прячет её до следующей найденной версии.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBanner))]
    public partial bool BannerDismissed { get; set; }

    /// <summary>
    /// Плашка об обновлении в виде v2rayN: там нет главного экрана с карточкой «Версии и обновления».
    /// Видна, пока есть что поставить или есть ответ на «Проверить обновления».
    /// </summary>
    public bool ShowBanner => !BannerDismissed && (CoresOffer is not null || AppOffer is not null || (Enabled && Status is not null));

    public string DismissLabel => CoresOffer is not null || AppOffer is not null ? "Позже" : "Понятно";

    [RelayCommand]
    private void DismissBanner() => BannerDismissed = true;

    private bool CanRun() => !Busy && Enabled;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CheckAsync() => CheckAsync(quiet: false);

    private async Task CheckAsync(bool quiet)
    {
        Busy = true;
        if (!quiet)
            Report("Проверяю…");
        try
        {
            _check = await Task.Run(() => _engine.CheckUpdatesAsync());
            var before = (CoresOffer, AppOffer);
            CoresOffer = _check.NewCores || _check.NewGeo
                ? "Есть новые " + (_check.NewCores && _check.NewGeo ? "ядра и списки сайтов" : _check.NewCores ? "версии ядер" : "списки сайтов")
                    + $" (выпуск №{_check.Manifest.Manifest.Sequence})."
                : null;
            AppOffer = _check.NewApp is { } app ? $"Доступна Тропа {app.Version.ToString(3)}." + (app.Notes is null ? "" : " " + app.Notes) : null;
            if (!quiet || (CoresOffer, AppOffer) != before)
                BannerDismissed = false;
            if (!quiet || _check.Any)
                Report(_check.Any ? null : "Установлены последние версии.");
        }
        catch (Exception ex) when (ex is IntegrityException or HttpRequestException or TaskCanceledException or InvalidOperationException or InvalidDataException or IOException)
        {
            if (!quiet)
                Report(Explain(ex), error: true);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallCoresAsync()
    {
        if (_check is null)
            return;
        Busy = true;
        try
        {
            var progress = new Progress<string>(m => Report(m));
            await Task.Run(() => _engine.InstallCoreUpdateAsync(_check, progress));
            CoresOffer = null;
            Report(_engine.Status.State == ConnectionState.Connected
                ? "Обновление установлено. Новые версии заработают после переподключения."
                : "Обновление установлено.");
            OnPropertyChanged(nameof(Versions));
        }
        catch (Exception ex) when (ex is IntegrityException or HttpRequestException or TaskCanceledException or InvalidOperationException
            or InvalidDataException or IOException or ServiceException or UnauthorizedAccessException)
        {
            Report(Explain(ex), error: true);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallAppAsync()
    {
        if (_check?.NewApp is not { } app)
            return;
        Busy = true;
        try
        {
            Report($"Скачиваю Тропу {app.Version.ToString(3)}…");
            var installer = await Task.Run(() => _engine.DownloadAppUpdateAsync(app));
            Report($"Устанавливаю Тропу {app.Version.ToString(3)}: Windows попросит разрешение, Тропа закроется на полминуты и откроется сама.");
            // Тихая установка без мастера (installer/tropa.iss, IsInAppUpdate): установщик сам вернёт прокси,
            // закроет Тропу, обновит службу и запустит новую версию; подключение восстановится, если было.
            var args = new List<string> { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL", "/SP-", "/TROPAUPDATE" };
            if (_engine.Status.State == ConnectionState.Connected)
                args.Add("/TROPARECONNECT");
            // Выбор «Запускать вместе с Windows» берём из настроек, а не из прошлой установки.
            args.Add(_engine.State.Settings.General.Autostart ? "/MERGETASKS=autostart" : "/MERGETASKS=!autostart");
            // Файл держим открытым без права записи, пока установщик не запущен: между проверкой и запуском его не подменить.
            using (PinnedFiles.OpenVerified(installer, app.Sha256))
                Process.Start(new ProcessStartInfo(installer, string.Join(' ', args)) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Report("Обновление отменено: Windows не получила разрешения. Нажмите «Установить» ещё раз и ответьте «Да».", error: true);
        }
        catch (Exception ex) when (ex is IntegrityException or HttpRequestException or TaskCanceledException or IOException or System.ComponentModel.Win32Exception)
        {
            Report(Explain(ex), error: true);
        }
        finally
        {
            Busy = false;
        }
    }

    private void Report(string? text, bool error = false)
    {
        Status = text;
        StatusIsError = error;
    }

    private static string Explain(Exception ex) => ex switch
    {
        HttpRequestException or TaskCanceledException => "Сервер обновлений недоступен. Попробуйте позже или после подключения.",
        _ => ex.Message,
    };
}
