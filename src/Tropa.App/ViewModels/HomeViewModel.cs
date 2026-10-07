using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.App.Services;
using Tropa.Core.Model;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Net;

namespace Tropa.App.ViewModels;

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
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateClock());
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() => OnStatus(s));
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        engine.Traffic += (_, t) => Dispatcher.UIThread.Post(() => OnTraffic(t));
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(TunAvailable));
            OnPropertyChanged(nameof(ModeNote));
            OnPropertyChanged(nameof(HowItWorks));
        });
        Refresh();
        OnStatus(engine.Status);
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
    public partial string CountryCode { get; set; } = "··";

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

    [ObservableProperty]
    public partial bool ModeTun { get; set; }

    [ObservableProperty]
    public partial int RouteIndex { get; set; }

    [ObservableProperty]
    public partial string RouteDescription { get; set; } = "";

    public bool TunAvailable => _engine.TunAvailable;

    public string ModeNote => (_engine.TunAvailable, _engine.ServiceAvailable) switch
    {
        (true, _) => "",
        (false, true) => "Служба Тропы запущена без прав системы, поэтому режим «Весь компьютер» недоступен. Сейчас Тропа работает как прокси для браузеров.",
        _ => "Режим «Весь компьютер» требует службу Тропы, а она не установлена или не запущена. Сейчас Тропа работает как прокси для браузеров.",
    };

    /// <summary>Пояснение «как это работает сейчас» — зависит от того, что реально включится.</summary>
    public string HowItWorks => ModeTun && _engine.TunAvailable
        ? "Служба Тропы создаёт виртуальную сетевую карту, и через неё идёт трафик всех программ: игры, голос Discord, мессенджеры. Правила для приложений работают. При отключении, сбое или закрытии Тропы служба останавливает подключение и возвращает настройки DNS Windows."
        : "Тропа включает прокси Windows, и через сервер идут браузеры и программы, которые его используют. При отключении и даже после сбоя прежние настройки прокси возвращаются.";

    partial void OnModeTunChanged(bool value) => OnPropertyChanged(nameof(HowItWorks));

    private void Refresh()
    {
        var s = _engine.State;
        var active = s.ActiveProfile;
        HasServer = active is not null;
        if (active is not null)
        {
            ServerName = Format.NameWithoutFlag(active.Name);
            CountryCode = Format.CountryCode(active.Name);
            ServerDetails = $"{Format.Protocol(active)} · {Format.Transport(active)}";
            var sub = s.Subscriptions.FirstOrDefault(x => x.Id == active.SubscriptionId);
            SubscriptionLine = sub is null ? null : SubscriptionText(sub);
        }
        else
        {
            ServerName = "Сервер не выбран";
            CountryCode = "··";
            ServerDetails = "Добавьте подписку на экране «Серверы» или нажмите Ctrl+V";
            SubscriptionLine = null;
        }

        ModeTun = s.Settings.Connection.Mode == CaptureMode.Tun;
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
        if (_engine.State.Settings.Routing.Preset != preset)
            _engine.UpdateSettings(s => s with { Routing = s.Routing with { Preset = preset } });
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
            await _engine.DisconnectAsync();
        else if (_engine.Status.State is ConnectionState.Disconnected or ConnectionState.Error)
            await _engine.ConnectAsync();
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);

    private void OnStatus(ConnectionStatus s)
    {
        IsConnected = s.State == ConnectionState.Connected;
        IsBusy = s.State is ConnectionState.Connecting or ConnectionState.Disconnecting;
        IsError = s.State == ConnectionState.Error;
        Message = s.Message;
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
        Totals = $"за сеанс ↓ {Format.Bytes(_totalDown)} · ↑ {Format.Bytes(_totalUp)}";
    }

    private void UpdateClock()
    {
        if (_engine.Status.Since is { } since)
            SessionTime = Format.Duration(DateTimeOffset.Now - since);
    }
}
