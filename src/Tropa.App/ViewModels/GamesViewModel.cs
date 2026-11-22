using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Games;
using Tropa.Core.Model;
using Tropa.Infrastructure;
using Tropa.Ipc;

namespace Tropa.App.ViewModels;

/// <summary>Строка игры на вкладке «Игры» (ADR-065): режим, действия, Teredo, домены.</summary>
internal sealed partial class GameItemViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly Action _changed;
    private GameProfile _game;
    private bool _loading;

    public GameItemViewModel(TropaEngine engine, GameProfile game, Action changed)
    {
        _engine = engine;
        _changed = changed;
        _game = game;
        Load(game);
    }

    public Guid Id => _game.Id;

    /// <summary>Своя группа переключателей у каждой строки.</summary>
    public string Group => "game-" + _game.Id.ToString("N");

    public string Title => _game.Title;

    public string Exe => _game.Exe;

    public string LauncherText => _game.Launcher switch
    {
        GameLauncher.Xbox => "Xbox / Game Pass",
        GameLauncher.Steam => "Steam",
        GameLauncher.Epic => "Epic Games",
        GameLauncher.Gog => "GOG",
        GameLauncher.Ea => "EA",
        GameLauncher.Ubisoft => "Ubisoft",
        GameLauncher.Riot => "Riot",
        _ => _game.Source == GameSource.Manual ? "добавлена вручную" : "игра",
    };

    public string SeenText => _game.LastSeen is { } seen
        ? "Запускалась " + seen.ToLocalTime().ToString("d MMMM, HH:mm", CultureInfo.GetCultureInfo("ru-RU"))
        : "Ещё не запускалась при открытой Тропе";

    public string Summary => _game.Mode == GameMode.Auto
        ? $"TCP {Act(_game.Tcp)}, UDP {Act(_game.Udp)}" + (_game.NeedsTeredo ? ", Teredo" : "")
            + $", адресов с настоящим DNS: {_game.Domains.Count.ToString(CultureInfo.InvariantCulture)}"
        : "Тропа ничего не меняет для этой игры.";

    /// <summary>Подсказка: игра «как было» обращалась к условному адресу FakeIP.</summary>
    public bool FakeIpHint => _game.FakeIpSeen && _game.Mode == GameMode.Original;

    public ObservableCollection<string> Domains { get; } = [];

    [ObservableProperty]
    public partial bool IsAuto { get; set; }

    [ObservableProperty]
    public partial bool IsOriginal { get; set; }

    [ObservableProperty]
    public partial int TcpIndex { get; set; }

    [ObservableProperty]
    public partial int UdpIndex { get; set; }

    [ObservableProperty]
    public partial bool NeedsTeredo { get; set; }

    [ObservableProperty]
    public partial bool Learn { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial string? NewDomain { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? AdminText { get; set; }

    /// <summary>Подставить свежую версию игры (нашлась, выучены домены) без пересоздания строки.</summary>
    public void Load(GameProfile game)
    {
        _loading = true;
        _game = game;
        IsAuto = game.Mode == GameMode.Auto;
        IsOriginal = !IsAuto;
        TcpIndex = Index(game.Tcp);
        UdpIndex = Index(game.Udp);
        NeedsTeredo = game.NeedsTeredo;
        Learn = !game.NoLearning;
        if (!Domains.SequenceEqual(game.Domains))
        {
            Domains.Clear();
            foreach (var d in game.Domains)
                Domains.Add(d);
        }

        var t = _engine.AdminTemplateFor(game);
        AdminText = t is null ? null
            : $"Администратор подписки советует: TCP {Act(t.Tcp)}, UDP {Act(t.Udp)}{(t.NeedsTeredo ? ", Teredo" : "")}, адресов: {t.Domains.Count.ToString(CultureInfo.InvariantCulture)}.";
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(SeenText));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(FakeIpHint));
        OnPropertyChanged(nameof(LauncherText));
        _loading = false;
    }

    /// <summary>Строку убрали с экрана: её выпадающие списки при отцеплении сбрасываются, это не выбор пользователя.</summary>
    public void Detach() => _loading = true;

    private static string Act(RuleAction a) => a.Kind switch
    {
        RuleActionKind.Direct => "напрямую",
        RuleActionKind.Block => "блокировать",
        _ => "через сервер",
    };

    private static int Index(RuleAction a) => a.Kind switch
    {
        RuleActionKind.Proxy or RuleActionKind.Server => 0,
        RuleActionKind.Block => 2,
        _ => 1,
    };

    private static RuleAction Action(int i) => i switch
    {
        0 => RuleAction.Proxy,
        2 => RuleAction.Block,
        _ => RuleAction.Direct,
    };

    partial void OnIsAutoChanged(bool value)
    {
        if (_loading || !value)
            return;
        _engine.SetGameMode(Id, GameMode.Auto);
        _changed();
    }

    partial void OnIsOriginalChanged(bool value)
    {
        if (_loading || !value)
            return;
        _engine.SetGameMode(Id, GameMode.Original);
        _changed();
    }

    partial void OnTcpIndexChanged(int value)
    {
        if (value < 0)
            Dispatcher.UIThread.Post(() => TcpIndex = Index(_game.Tcp));
        else
            Push(g => g with { Tcp = Action(value) });
    }

    partial void OnUdpIndexChanged(int value)
    {
        if (value < 0)
            Dispatcher.UIThread.Post(() => UdpIndex = Index(_game.Udp));
        else
            Push(g => g with { Udp = Action(value) });
    }

    partial void OnNeedsTeredoChanged(bool value) => Push(g => g with { NeedsTeredo = value });

    partial void OnLearnChanged(bool value) => Push(g => g with { NoLearning = !value });

    private void Push(Func<GameProfile, GameProfile> change)
    {
        if (_loading)
            return;
        _engine.UpdateGame(Id, change);
        _changed();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void AddDomain()
    {
        if (GameDefaults.NormalizeDomain(NewDomain) is not { } d)
        {
            Error = "Укажите адрес сайта, например playfabapi.com (действует и на поддомены).";
            return;
        }

        Error = null;
        NewDomain = null;
        Push(g => g with { Domains = GameList.MergeDomains(g.Domains, [d]) });
    }

    [RelayCommand]
    private void RemoveDomain(string domain) => Push(g => g with { Domains = g.Domains.Where(x => x != domain).ToList() });

    [RelayCommand]
    private void ClearDomains() => Push(g => g with { Domains = [] });

    [RelayCommand]
    private void UseAdmin()
    {
        if (_engine.AdminTemplateFor(_game) is { } t)
            Push(g => GameList.ApplyTemplate(g, t));
    }

    [RelayCommand]
    private void Remove()
    {
        _engine.RemoveGame(Id);
        _changed();
    }
}

/// <summary>Вкладка «Игры» (ADR-065).</summary>
internal sealed partial class GamesViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private bool _loading;

    public GamesViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Load);
        engine.GamesChanged += (_, _) => Dispatcher.UIThread.Post(Load);
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            if (s.State != ConnectionState.Connected)
                NeedsApply = false;
            OnPropertyChanged(nameof(ModeNote));
        });
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ModeNote)));
        Load();
    }

    public ObservableCollection<GameItemViewModel> Games { get; } = [];

    public ObservableCollection<string> RunningApps { get; } = [];

    [ObservableProperty]
    public partial string? NewGame { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    [ObservableProperty]
    public partial bool NeedsApply { get; set; }

    [ObservableProperty]
    public partial bool TeredoWatch { get; set; }

    [ObservableProperty]
    public partial string TeredoText { get; set; } = "Состояние ещё не проверялось.";

    [ObservableProperty]
    public partial string? TeredoHint { get; set; }

    [ObservableProperty]
    public partial bool TeredoBusy { get; set; }

    [ObservableProperty]
    public partial bool TeredoOk { get; set; }

    public bool HasGames => Games.Count > 0;

    /// <summary>Правила по программам работают только в TUN.</summary>
    public string? ModeNote => _engine.TunAvailable && _engine.State.Settings.Connection.Mode == CaptureMode.Tun
        ? null
        : "Автонастройка игр работает только в режиме «Весь компьютер» (TUN): в режиме «Только браузеры» игры идут мимо Тропы и так.";

    /// <summary>Вкладку открыли: свежий поиск, запущенные программы и состояние Teredo.</summary>
    public void OnShown()
    {
        RefreshRunningApps();
        _ = Task.Run(() =>
        {
            try
            {
                _engine.ScanGames();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
            {
            }
        });
        _ = CheckTeredoAsync();
    }

    private void Load()
    {
        _loading = true;
        var games = _engine.State.Settings.Games;
        TeredoWatch = !games.TeredoHandsOff;
        // Строки обновляем на месте: при обучении доменам раскрытая игра не должна схлопываться.
        foreach (var gone in Games.Where(i => games.List.All(g => g.Id != i.Id)).ToList())
        {
            gone.Detach();
            Games.Remove(gone);
        }

        for (var i = 0; i < games.List.Count; i++)
        {
            var g = games.List[i];
            var item = Games.FirstOrDefault(x => x.Id == g.Id);
            if (item is null)
                Games.Insert(Math.Min(i, Games.Count), new GameItemViewModel(_engine, g, OnItemChanged));
            else
                item.Load(g);
        }

        OnPropertyChanged(nameof(HasGames));
        OnPropertyChanged(nameof(ModeNote));
        _loading = false;
    }

    private void OnItemChanged()
    {
        if (_engine.Status.State == ConnectionState.Connected)
            NeedsApply = true;
    }

    partial void OnTeredoWatchChanged(bool value)
    {
        if (_loading)
            return;
        _engine.UpdateSettings(s => s with { Games = s.Games with { TeredoHandsOff = !value } });
    }

    [RelayCommand]
    private void AddGame()
    {
        Error = _engine.AddGameManually(NewGame);
        if (Error is null)
            NewGame = null;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        Error = null;
        var added = await Task.Run(_engine.ScanGames);
        Notice = added switch
        {
            0 => "Новых игр среди запущенных программ нет. Запустите игру и нажмите ещё раз — или добавьте её вручную.",
            1 => "Найдена новая игра. Включите для неё «Автонастройку Тропы», если в сети есть проблемы.",
            _ => $"Найдено новых игр: {added.ToString(CultureInfo.InvariantCulture)}.",
        };
        RefreshRunningApps();
    }

    [RelayCommand]
    private void RefreshRunningApps()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.SessionId != 0 && !string.IsNullOrEmpty(p.ProcessName))
                    names.Add(p.ProcessName + ".exe");
            }
        }

        RunningApps.Clear();
        foreach (var n in names.Take(400))
            RunningApps.Add(n);
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        NeedsApply = false;
        await _engine.ApplyIfConnectedAsync();
        Notice = "Применено. Если игра уже была запущена — перезапустите её: она могла запомнить старые адреса.";
        _ = CheckTeredoLaterAsync();
    }

    private async Task CheckTeredoLaterAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        await CheckTeredoAsync();
    }

    [RelayCommand]
    private Task CheckTeredoAsync() => RunTeredoAsync(TeredoAction.Status);

    [RelayCommand]
    private Task EnableTeredoAsync() => RunTeredoAsync(TeredoAction.Enable);

    [RelayCommand]
    private Task RestartTeredoAsync() => RunTeredoAsync(TeredoAction.Restart);

    private async Task RunTeredoAsync(TeredoAction action)
    {
        if (TeredoBusy)
            return;
        TeredoBusy = true;
        try
        {
            if (action != TeredoAction.Status)
                TeredoText = action == TeredoAction.Enable ? "Включаю Teredo…" : "Перезапускаю службу IP Helper…";
            var view = await _engine.TeredoAsync(action);
            Show(view);
        }
        finally
        {
            TeredoBusy = false;
        }
    }

    private void Show(TeredoView view)
    {
        var s = view.Status;
        TeredoOk = s?.Working == true;
        TeredoText = s is null ? "Не удалось узнать состояние Teredo."
            : s.Disabled ? "Teredo выключен. Игры Xbox / Game Pass не смогут соединяться с другими игроками."
            : s.Working ? $"Teredo работает ({s.State}). Сервер: {s.Server}." + (s.Nat is { } nat ? $" NAT: {nat}." : "")
            : s.Probing ? $"Teredo проверяет связь с сервером ({s.State}). Подождите полминуты и нажмите «Проверить»."
            : $"Teredo не работает ({s.State})." + (s.Error is { } e ? " " + e + "." : "");
        var hints = new List<string>();
        if (view.Error is { } err)
            hints.Add(err);
        if (s?.StrictNat == true)
            hints.Add("NAT строгий (symmetric): к сессиям друзей иногда не подключиться. Включите UPnP в настройках роутера (обычно 192.168.0.1 или 192.168.1.1).");
        TeredoHint = hints.Count == 0 ? null : string.Join(" ", hints);
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}
