using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.App.Services;
using Tropa.Core.Model;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Net;

namespace Tropa.App.ViewModels;

/// <summary>Пункт быстрого выбора сервера на главной.</summary>
internal sealed record ServerChoice(Guid Id, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Главная: кнопка подключения, текущий сервер, режим, маршрут, трафик.</summary>
internal sealed partial class HomeViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private readonly DispatcherTimer _clock;
    private long _totalUp;
    private long _totalDown;

    public HomeViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        Updates = new UpdatesViewModel(engine);
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateClock());
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => OnStatus(s));
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        engine.Traffic += (_, t) => Dispatcher.UIThread.Post(() => OnTraffic(t));
        engine.ExitRegionChanged += (_, _) => Dispatcher.UIThread.Post(OnExitRegion);
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(TunAvailable));
            OnPropertyChanged(nameof(ModeNote));
            OnPropertyChanged(nameof(HowItWorks));
            OnPropertyChanged(nameof(Blocking));
            OnPropertyChanged(nameof(KillSwitchReason));
        });
        Refresh();
        OnStatus(engine.Status);
    }

    public UpdatesViewModel Updates { get; }

    /// <summary>Быстрое переключение между серверами под кнопкой подключения.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ServerChoice> Servers { get; } = [];

    [ObservableProperty]
    public partial ServerChoice? SelectedServer { get; set; }

    private bool _fillingServers;

    partial void OnSelectedServerChanged(ServerChoice? value)
    {
        if (_fillingServers || value is null || value.Id == _engine.State.ActiveProfileId)
            return;
        // Если подключено — Тропа сразу переподключится на выбранный сервер.
        _ = _engine.SetActiveAsync(value.Id);
    }

    private void FillServers()
    {
        _fillingServers = true;
        try
        {
            Servers.Clear();
            var subs = _engine.State.Subscriptions.ToDictionary(s => s.Id, s => s.Name);
            // Сначала рабочие по задержке, затем остальные; удалённые провайдером не показываем.
            foreach (var sp in _engine.State.Profiles.Where(p => p.RemovedByProvider is null && !p.Hidden)
                         .OrderBy(p => p.LastTest?.DelayMs ?? int.MaxValue).ThenBy(p => p.Order))
            {
                var delay = sp.LastTest?.DelayMs is { } ms ? $" · {ms} мс" : "";
                var group = sp.Profile.SubscriptionId is { } sid && subs.TryGetValue(sid, out var name) ? $"  ({name})" : "";
                Servers.Add(new ServerChoice(sp.Profile.Id, Format.NameWithoutFlag(sp.Profile.Name) + delay + group));
            }

            SelectedServer = Servers.FirstOrDefault(s => s.Id == _engine.State.ActiveProfileId);
        }
        finally
        {
            _fillingServers = false;
        }
    }

    [ObservableProperty]
    public partial string StateText { get; set; } = "Подключить";

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsError { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string SessionTime { get; set; } = "";

    [ObservableProperty]
    public partial string ServerName { get; set; } = "Сервер не выбран";

    [ObservableProperty]
    public partial string ServerDetails { get; set; } = "Добавьте подписку на экране «Серверы» или нажмите Ctrl+V";

    [ObservableProperty]
    public partial string CountryCode { get; set; } = "–";

    /// <summary>Флаг страны выхода: при подключении — по живой проверке, иначе — по последней проверке сервера.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlag))]
    public partial Avalonia.Media.Imaging.Bitmap? Flag { get; set; }

    public bool HasFlag => Flag is not null;

    private void UpdateFlag()
    {
        var s = _engine.State;
        var live = _engine.Status.State == ConnectionState.Connected ? _engine.ExitRegion?.CountryCode : null;
        var tested = s.ActiveProfile is { } a ? s.Profiles.FirstOrDefault(p => p.Profile.Id == a.Id)?.LastTest?.ExitCountry : null;
        var code = live ?? tested;
        CountryCode = code ?? "–";
        Flag = Flags.Get(code);
    }

    [ObservableProperty]
    public partial bool HasServer { get; set; }

    [ObservableProperty]
    public partial string Down { get; set; } = "0 Б/с";

    [ObservableProperty]
    public partial string Up { get; set; } = "0 Б/с";

    [ObservableProperty]
    public partial string Totals { get; set; } = "";

    [ObservableProperty]
    public partial string? SubscriptionLine { get; set; }

    /// <summary>«Нидерланды» — страна, из которой сайты видят подключение; null — значок скрыт.</summary>
    [ObservableProperty]
    public partial string? ExitRegion { get; set; }

    [ObservableProperty]
    public partial string? ExitRegionTip { get; set; }

    private void OnExitRegion()
    {
        var r = _engine.Status.State == ConnectionState.Connected ? _engine.ExitRegion : null;
        ExitRegion = r?.Name;
        UpdateFlag();
        ExitRegionTip = r is null ? null
            : $"Сайты видят вас из страны «{r.Name}» ({r.CountryCode}). Проверено в {r.CheckedAt.ToLocalTime():HH:mm}, обновляется раз в минуту и по правому клику на значке в трее: сервер может сам переключать выход.";
    }

    [ObservableProperty]
    public partial bool ModeTun { get; set; }

    [ObservableProperty]
    public partial int RouteIndex { get; set; }

    [ObservableProperty]
    public partial string RouteDescription { get; set; } = "";

    public bool TunAvailable => _engine.TunAvailable;

    /// <summary>Служба держит аварийную блокировку — показываем плашку с кнопкой «Снять блокировку».</summary>
    public bool Blocking => _engine.Blocking && _engine.Status.State != ConnectionState.Connected;

    [ObservableProperty]
    public partial bool KillSwitch { get; set; }

    /// <summary>Почему kill switch сейчас недоступен (null — доступен).</summary>
    public string? KillSwitchReason
    {
        get
        {
            var s = _engine.State.Settings;
            if (!_engine.TunAvailable)
                return "Сейчас недоступно: нужна служба Тропы и режим «Весь компьютер».";
            return Core.Compatibility.CompatRules.Evaluate(s).Disabled.TryGetValue("killSwitch", out var reason) ? "Сейчас недоступно: " + reason + "." : null;
        }
    }

    public bool KillSwitchEnabled => KillSwitchReason is null;

    partial void OnKillSwitchChanged(bool value)
    {
        if (_engine.State.Settings.General.KillSwitch != value)
            _engine.UpdateSettings(s => s with { General = s.General with { KillSwitch = value } });
    }

    [RelayCommand]
    private async Task ReleaseBlockingAsync()
    {
        try
        {
            await _engine.ReleaseBlockingAsync();
        }
        catch (Exception ex) when (ex is Infrastructure.Service.ServiceException or TimeoutException)
        {
            Message = "Служба не ответила: " + ex.Message + " Используйте «Аварийно вернуть настройки сети» в меню значка в трее.";
        }

        OnPropertyChanged(nameof(Blocking));
    }

    public string ModeNote => _engine.TunAvailable ? "" : _engine.NoTunReason() + " Сейчас Тропа работает как прокси для браузеров.";

    /// <summary>Пояснение «как это работает сейчас» — зависит от того, что реально включится.</summary>
    public string HowItWorks => ModeTun && _engine.TunAvailable
        ? "Служба Тропы создаёт виртуальную сетевую карту, и через неё идёт трафик всех программ: игры, голос Discord, мессенджеры. Правила для приложений работают. При отключении, сбое или закрытии Тропы служба останавливает подключение и возвращает настройки DNS Windows."
        : "Тропа включает прокси Windows, и через сервер идут браузеры и программы, которые его используют. При отключении и даже после сбоя прежние настройки прокси возвращаются.";

    partial void OnModeTunChanged(bool value)
    {
        OnPropertyChanged(nameof(HowItWorks));
        OnPropertyChanged(nameof(TunToggle));
    }

    // ---------------- «TUN главный» (ADR-056) ----------------

    /// <summary>
    /// Переключатель TUN (оба вида окна): включён — Тропа работает для всего компьютера; выключен — Тропа
    /// работает только если в «Системном прокси» выбрано «Установить» (для браузеров), иначе выключена.
    /// </summary>
    public bool TunToggle
    {
        get => ModeTun && _engine.Status.State is ConnectionState.Connected or ConnectionState.Connecting;
        set
        {
            if (value != TunToggle)
                _ = SwitchTunAsync(value);
        }
    }

    private async Task SwitchTunAsync(bool on)
    {
        try
        {
            if (on)
            {
                _engine.UpdateSettings(s => s with { Connection = s.Connection with { Mode = CaptureMode.Tun } });
                await ConnectOrApplyAsync("tun");
            }
            else if (_engine.State.Settings.Connection.SystemProxy == SystemProxyChoice.Set)
            {
                // TUN выключен, но выбран «Установить системный прокси» — Тропа остаётся для браузеров.
                _engine.UpdateSettings(s => s with { Connection = s.Connection with { Mode = CaptureMode.SystemProxy } });
                await ConnectOrApplyAsync("sysproxy");
            }
            else
            {
                await _engine.DisconnectAsync("tun");
                ClearIfAsked();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Infrastructure.Service.ServiceException or TimeoutException)
        {
            Message = _engine.Scrubber.Scrub(ex.Message);
        }
        finally
        {
            OnPropertyChanged(nameof(TunToggle));
        }
    }

    private async Task ConnectOrApplyAsync(string source)
    {
        if (_engine.Status.State == ConnectionState.Connected)
            await _engine.ApplyIfConnectedAsync();
        else if (_engine.Status.State is ConnectionState.Disconnected or ConnectionState.Error)
            await _engine.ConnectAsync(source);
    }

    /// <summary>«Системный прокси»: 0 — Установить, 1 — Очистить, 2 — Не менять (как в v2rayN).</summary>
    public int ProxyIndex
    {
        get => _engine.State.Settings.Connection.Mode == CaptureMode.SystemProxy ? 0 : (int)_engine.State.Settings.Connection.SystemProxy;
        set
        {
            if (value is < 0 or > 2 || value == ProxyIndex)
                return;
            _ = SwitchProxyAsync((SystemProxyChoice)value);
        }
    }

    private async Task SwitchProxyAsync(SystemProxyChoice choice)
    {
        try
        {
            var tunRunning = TunToggle;
            _engine.UpdateSettings(s => s with
            {
                Connection = s.Connection with
                {
                    SystemProxy = choice,
                    // Пока работает TUN, выбор нужен на потом — режим не меняем.
                    Mode = tunRunning ? s.Connection.Mode
                        : choice == SystemProxyChoice.Set ? CaptureMode.SystemProxy
                        : s.Connection.Mode == CaptureMode.SystemProxy ? CaptureMode.Tun : s.Connection.Mode,
                },
            });
            if (tunRunning)
                return;
            if (choice == SystemProxyChoice.Set)
            {
                await ConnectOrApplyAsync("sysproxy");
                return;
            }

            // «Очистить» и «Не менять» при выключенном TUN — Тропа не работает; прокси, который ставила она, вернётся.
            if (_engine.Status.State is ConnectionState.Connected or ConnectionState.Connecting)
                await _engine.DisconnectAsync("sysproxy");
            ClearIfAsked();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Infrastructure.Service.ServiceException or TimeoutException)
        {
            Message = _engine.Scrubber.Scrub(ex.Message);
        }
        finally
        {
            OnPropertyChanged(nameof(ProxyIndex));
            OnPropertyChanged(nameof(TunToggle));
        }
    }

    private void ClearIfAsked()
    {
        if (_engine.State.Settings.Connection.SystemProxy == SystemProxyChoice.Clear && _engine.ClearOwnSystemProxy() is { } done)
            Message = done;
    }


    private void Refresh()
    {
        var s = _engine.State;
        var active = s.ActiveProfile;
        HasServer = active is not null;
        if (active is not null)
        {
            ServerName = Format.NameWithoutFlag(active.Name);
            ServerDetails = $"{Format.Protocol(active)} · {Format.Transport(active)}";
            var sub = s.Subscriptions.FirstOrDefault(x => x.Id == active.SubscriptionId);
            SubscriptionLine = sub is null ? null : SubscriptionText(sub);
        }
        else
        {
            ServerName = "Сервер не выбран";
            ServerDetails = "Добавьте подписку на экране «Серверы» или нажмите Ctrl+V";
            SubscriptionLine = null;
        }

        UpdateFlag();

        ModeTun = s.Settings.Connection.Mode == CaptureMode.Tun;
        OnPropertyChanged(nameof(ProxyIndex));
        OnPropertyChanged(nameof(TunToggle));
        FillServers();
        KillSwitch = s.Settings.General.KillSwitch;
        OnPropertyChanged(nameof(KillSwitchReason));
        OnPropertyChanged(nameof(KillSwitchEnabled));
        RouteIndex = (int)s.Settings.Routing.Preset;
        RouteDescription = RouteText(s.Settings.Routing.Preset);
    }

    internal static string SubscriptionText(Subscription sub)
    {
        var parts = new List<string> { sub.Name };
        if (sub.Info?.Remaining is { } left)
            parts.Add("осталось " + Format.Bytes(left));
        if (sub.Info?.Expire is { } exp)
        {
            var days = (exp - DateTimeOffset.Now).TotalDays;
            parts.Add(days < 0 ? "срок истёк" : days < 3 ? $"истекает через {Math.Ceiling(days)} дн." : "до " + exp.LocalDateTime.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")));
        }

        return string.Join(" · ", parts);
    }

    private static string RouteText(RoutePreset preset) => preset switch
    {
        RoutePreset.ExceptRu => "Госуслуги, банки и российские сайты идут напрямую, остальное — через сервер. Заблокированное в РФ — всегда через сервер.",
        RoutePreset.BlockedOnly => "Через сервер идут только сайты из списка блокировок. Остальное — напрямую.",
        _ => "Через сервер идёт всё. Госуслуги и банки всё равно напрямую — они не пускают с зарубежных IP.",
    };

    partial void OnRouteIndexChanged(int value)
    {
        var preset = (RoutePreset)Math.Clamp(value, 0, 2);
        RouteDescription = RouteText(preset);
        if (_engine.State.Settings.Routing.Preset == preset)
            return;
        _engine.UpdateSettings(s => s with { Routing = s.Routing with { Preset = preset } });
        // Маршрут меняется сразу: при подключении — переподключение с новыми правилами, без «Применить».
        _ = ApplyNowAsync();
    }

    private async Task ApplyNowAsync()
    {
        try
        {
            await _engine.ApplyIfConnectedAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Infrastructure.Service.ServiceException or TimeoutException)
        {
            Message = _engine.Scrubber.Scrub(ex.Message);
        }
    }

    [RelayCommand]
    private void SetMode(string mode)
    {
        var m = mode == "tun" ? CaptureMode.Tun : CaptureMode.SystemProxy;
        _engine.UpdateSettings(s => s with { Connection = s.Connection with { Mode = m } });
    }

    [RelayCommand]
    private async Task ToggleAsync()
    {
        if (_engine.Status.State is ConnectionState.Connected)
            await _engine.DisconnectAsync("home");
        else if (_engine.Status.State is ConnectionState.Disconnected or ConnectionState.Error)
            await _engine.ConnectAsync("home");
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);

    private void OnStatus(ConnectionStatus s)
    {
        OnPropertyChanged(nameof(TunToggle));
        IsConnected = s.State == ConnectionState.Connected;
        IsBusy = s.State is ConnectionState.Connecting or ConnectionState.Disconnecting;
        IsError = s.State == ConnectionState.Error;
        Message = s.Message;
        OnExitRegion();
        OnPropertyChanged(nameof(Blocking));
        StateText = s.State switch
        {
            ConnectionState.Connected => "Подключено",
            ConnectionState.Connecting => "Подключение…",
            ConnectionState.Disconnecting => "Отключение…",
            ConnectionState.Error => "Повторить",
            _ => "Подключить",
        };
        if (IsConnected)
        {
            _totalUp = _totalDown = 0;
            _clock.Start();
            UpdateClock();
        }
        else
        {
            _clock.Stop();
            SessionTime = "";
            Down = Up = "0 Б/с";
            Totals = "";
        }
    }

    private void OnTraffic(TrafficSample t)
    {
        _totalUp += t.Up;
        _totalDown += t.Down;
        Down = Format.Speed(t.Down);
        Up = Format.Speed(t.Up);
        Totals = $"за сеанс: загружено {Format.Bytes(_totalDown)} · отдано {Format.Bytes(_totalUp)}";
    }

    private void UpdateClock()
    {
        if (_engine.Status.Since is { } since)
            SessionTime = Format.Duration(DateTimeOffset.Now - since);
    }
}
