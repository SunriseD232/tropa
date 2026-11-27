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
/// нет (docs/02-security.md, §3.8): Тропа только сообщает — плашкой и окном «Вышла новая версия»
/// (ADR-069), — ставит пользователь кнопкой.
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
        if (!engine.Updates.Enabled)
            return;
        // Тихая проверка при каждом запуске (это один маленький манифест) и потом раз в QuietCheckInterval, пока Тропа
        // работает. Раньше проверка была только при запуске и не чаще раза в сутки: у тех, кто не выключает компьютер,
        // Тропа не узнавала о выпуске днями, а найденная и отложенная версия не напоминала о себе при следующем запуске
        // (журнал 30.10–03.11: 4 установки из 7 остались на старых версиях). Неудачная проверка повторяется через полчаса.
        Dispatcher.UIThread.Post(() => QuietCheck(force: true), DispatcherPriority.Background);
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(30), DispatcherPriority.Background, (_, _) => QuietCheck(force: false));
        _timer.Start();
    }

    internal static readonly TimeSpan QuietCheckInterval = TimeSpan.FromHours(6);

    private readonly DispatcherTimer? _timer;

    private void QuietCheck(bool force)
    {
        var s = _engine.State.Settings.Cores;
        if (Busy || !(s.AppCheck || s.GeoUpdate))
            return;
        if (force || s.LastUpdateCheck is null || DateTimeOffset.Now - s.LastUpdateCheck > QuietCheckInterval)
            _ = CheckAsync(quiet: true);
    }

    public string Versions => $"Тропа {TropaEngine.AppVersion.ToString(3)} · {_engine.CoreVersions}";

    // ---------------- Окно «Вышла новая версия» (ADR-069) ----------------

    /// <summary>
    /// Нужно показать окно Тропы: журнал 24–27.11 — пять установок из тринадцати видели новую версию только
    /// плашкой и неделями оставались на старой (одна — на 0.1.22). Окно открывается само, из трея.
    /// </summary>
    public event EventHandler? AttentionNeeded;

    /// <summary>«Не сейчас»: снова через сутки или при следующем запуске.</summary>
    internal static readonly TimeSpan PromptSnooze = TimeSpan.FromHours(24);

    private string? _snoozedVersion;
    private DateTimeOffset _snoozedUntil;

    [ObservableProperty]
    public partial bool IsPromptOpen { get; set; }

    [ObservableProperty]
    public partial string PromptTitle { get; set; } = "";

    [ObservableProperty]
    public partial string PromptText { get; set; } = "";

    [ObservableProperty]
    public partial string? PromptNotes { get; set; }

    private string? _promptVersion;

    /// <summary>Показывать ли окно для этой версии: не пропущена навсегда и не отложена.</summary>
    internal static bool ShouldPrompt(string version, string? skipped, string? snoozedVersion, DateTimeOffset snoozedUntil, DateTimeOffset now) =>
        !string.Equals(version, skipped, StringComparison.Ordinal)
        && !(string.Equals(version, snoozedVersion, StringComparison.Ordinal) && now < snoozedUntil);

    private void MaybePrompt()
    {
        if (_check?.NewApp is not { } app || IsPromptOpen || !_engine.State.Settings.Cores.AppCheck)
            return;
        var version = app.Version.ToString(3);
        if (!ShouldPrompt(version, _engine.State.Settings.Cores.SkippedAppVersion, _snoozedVersion, _snoozedUntil, DateTimeOffset.Now))
            return;
        _promptVersion = version;
        PromptTitle = $"Вышла Тропа {version}";
        PromptText = $"У вас {TropaEngine.AppVersion.ToString(3)}. Новые версии исправляют ошибки подключения — лучше не откладывать."
            + " Обновление займёт полминуты: Windows спросит разрешение, Тропа закроется и откроется сама"
            + (_engine.Status.State == ConnectionState.Connected ? " и снова подключится." : ".");
        PromptNotes = app.Notes;
        IsPromptOpen = true;
        AttentionNeeded?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private async Task PromptUpdateAsync()
    {
        IsPromptOpen = false;
        await InstallAppAsync();
    }

    [RelayCommand]
    private void PromptLater()
    {
        IsPromptOpen = false;
        _snoozedVersion = _promptVersion;
        _snoozedUntil = DateTimeOffset.Now + PromptSnooze;
    }

    [RelayCommand]
    private void PromptSkip()
    {
        IsPromptOpen = false;
        if (_promptVersion is { } version)
            _engine.UpdateSettings(s => s with { Cores = s.Cores with { SkippedAppVersion = version } });
    }

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
            // Тихая проверка нашла новую Тропу — окно. По кнопке «Проверить» человек и так смотрит на ответ.
            if (quiet)
                MaybePrompt();
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
        if (Infrastructure.Service.PortableMode.Directory is { } portableDir)
        {
            await InstallPortableAsync(app, portableDir);
            return;
        }

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

    /// <summary>
    /// Портативная Тропа (ADR-063): zip из выпуска распаковывается рядом, новая версия запускается из распакованной
    /// папки, ждёт закрытия этой, копирует файлы поверх (папка data остаётся) и открывает обновлённую Тропу.
    /// </summary>
    private async Task InstallPortableAsync(Core.Updates.AppRelease app, string portableDir)
    {
        if (app.PortableUrl is null)
        {
            Report($"У выпуска {app.Version.ToString(3)} нет портативной версии: скачайте её со страницы выпуска на GitHub.", error: true);
            return;
        }

        Busy = true;
        try
        {
            Report($"Скачиваю Тропу {app.Version.ToString(3)}…");
            var staged = await Task.Run(() => _engine.StagePortableUpdateAsync(app));
            Report($"Обновляю Тропу до {app.Version.ToString(3)}: она закроется на несколько секунд и откроется сама.");
            var info = new ProcessStartInfo(Path.Combine(staged, "Tropa.exe")) { UseShellExecute = false, WorkingDirectory = staged };
            info.ArgumentList.Add(Infrastructure.Updates.PortableUpdater.ApplyArgument);
            info.ArgumentList.Add(portableDir);
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (_engine.Status.State == ConnectionState.Connected)
                info.ArgumentList.Add("--reconnect");
            using (Process.Start(info))
            {
            }

            await App.ExitForUpdateAsync();
        }
        catch (Exception ex) when (ex is IntegrityException or HttpRequestException or TaskCanceledException or IOException
            or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
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
