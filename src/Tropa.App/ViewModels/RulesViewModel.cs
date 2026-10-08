using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Model;
using Tropa.Core.Routing;
using Tropa.Infrastructure;

namespace Tropa.App.ViewModels;

/// <summary>Строка правила: приложение или сайт с отдельными действиями для TCP и UDP.</summary>
internal sealed partial class RuleItemViewModel : ObservableObject
{
    private readonly Action<Rule> _changed;
    private Rule _rule;
    private bool _loading = true;

    public RuleItemViewModel(Rule rule, Action<Rule> changed, Action<Guid> remove)
    {
        _rule = rule;
        _changed = changed;
        Title = rule.Label is { Length: > 0 } label ? label
            : rule.Match.Processes.Count > 0 ? rule.Match.Processes[0]
            : rule.Match.DomainSuffixes.Count > 0 ? rule.Match.DomainSuffixes[0]
            : "?";
        TcpIndex = Index(rule.Tcp);
        UdpIndex = Index(rule.Udp);
        Enabled = rule.Enabled;
        RemoveCommand = new RelayCommand(() => remove(rule.Id));
        _loading = false;
    }

    public string Title { get; }

    public IRelayCommand RemoveCommand { get; }

    [ObservableProperty]
    public partial int TcpIndex { get; set; }

    [ObservableProperty]
    public partial int UdpIndex { get; set; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    private static int Index(RuleAction a) => a.Kind switch
    {
        RuleActionKind.Direct => 1,
        RuleActionKind.Block => 2,
        _ => 0,
    };

    private static RuleAction Action(int i) => i switch
    {
        1 => RuleAction.Direct,
        2 => RuleAction.Block,
        _ => RuleAction.Proxy,
    };

    partial void OnTcpIndexChanged(int value) => Push(_rule with { Tcp = Action(value) });

    partial void OnUdpIndexChanged(int value) => Push(_rule with { Udp = Action(value) });

    partial void OnEnabledChanged(bool value) => Push(_rule with { Enabled = value });

    private void Push(Rule rule)
    {
        _rule = rule;
        if (!_loading)
            _changed(rule);
    }
}

/// <summary>Приложение Store в списке loopback-исключений.</summary>
internal sealed partial class StoreAppViewModel(string sid, string name, bool isChecked, Action<string, bool> changed) : ObservableObject
{
    public string Sid { get; } = sid;

    public string Name { get; } = name;

    [ObservableProperty]
    public partial bool IsChecked { get; set; } = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed(Sid, value);
}

/// <summary>Экран «Правила» (docs/06-features.md, §3).</summary>
internal sealed partial class RulesViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private bool _loading;

    public RulesViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Load);
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(AppRulesNote));
            OnPropertyChanged(nameof(StoreAppsVisible));
        });
        engine.DpiStatusChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(DpiFirstStatus)));
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(DpiFirstStatus));
            if (!_engine.CoreRunning)
                NeedsApply = false;
        });
        Load();
        RefreshRunningApps();
    }

    public ObservableCollection<RuleItemViewModel> Apps { get; } = [];

    public ObservableCollection<RuleItemViewModel> Sites { get; } = [];

    public ObservableCollection<string> RunningApps { get; } = [];

    private List<StoreAppViewModel> _allStoreApps = [];

    public ObservableCollection<StoreAppViewModel> StoreApps { get; } = [];

    /// <summary>Карточка Store нужна только в режиме «Только браузеры»: в TUN приложения Store и так идут через Тропу.</summary>
    public bool StoreAppsVisible => _engine.State.Settings.Connection.Mode == CaptureMode.SystemProxy || !_engine.TunAvailable;

    public string StoreAppsSummary => _engine.State.Settings.Connection.UwpLoopback.Count switch
    {
        0 => "Не выбрано ни одного приложения.",
        var n => $"Выбрано: {n}. Исключения включаются при подключении и снимаются при отключении.",
    };

    [ObservableProperty]
    public partial string? StoreFilter { get; set; }

    partial void OnStoreFilterChanged(string? value) => FilterStoreApps();

    private void FilterStoreApps()
    {
        StoreApps.Clear();
        foreach (var a in _allStoreApps.Where(a => string.IsNullOrWhiteSpace(StoreFilter) || a.Name.Contains(StoreFilter.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            StoreApps.Add(a);
    }

    [RelayCommand]
    private async Task LoadStoreAppsAsync()
    {
        try
        {
            var chosen = _engine.State.Settings.Connection.UwpLoopback.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var apps = await Task.Run(Infrastructure.SystemIntegration.FirewallLoopbackStore.Enumerate);
            _allStoreApps = apps.Select(a => new StoreAppViewModel(a.Sid, a.DisplayName, chosen.Contains(a.Sid), OnStoreAppChanged)).ToList();
            FilterStoreApps();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Error = "Не удалось получить список приложений Store: " + ex.Message;
        }
    }

    private void OnStoreAppChanged(string sid, bool on)
    {
        _engine.UpdateSettings(s =>
        {
            var list = s.Connection.UwpLoopback.Where(x => !string.Equals(x, sid, StringComparison.OrdinalIgnoreCase)).ToList();
            if (on)
                list.Add(sid);
            return s with { Connection = s.Connection with { UwpLoopback = list } };
        });
        NeedsApply = _engine.CoreRunning;
    }

    public IReadOnlyList<string> Actions { get; } = ["Через сервер", "Напрямую", "Блокировать"];

    [ObservableProperty]
    public partial int PresetIndex { get; set; }

    [ObservableProperty]
    public partial bool BlockQuic { get; set; }

    /// <summary>0 — выключено, 1 — мягко, 2 — агрессивно, 3 — свои (info.ru.json: dpiPreset).</summary>
    [ObservableProperty]
    public partial int DpiIndex { get; set; }

    public string DpiDescription => DpiIndex switch
    {
        1 => "Фрагментация TLS-приветствия только для YouTube и связанных доменов, которые идут напрямую.",
        2 => "Фрагментация всех прямых HTTPS-соединений и UDP-шум (шум работает через ядро Xray).",
        3 => "Свои параметры (настраиваются в разделе «Настройки → Обход DPI»).",
        _ => "Приёмы обхода DPI для прямого трафика выключены. На трафик через сервер они не влияют.",
    };

    [ObservableProperty]
    public partial bool UdpProxy { get; set; }

    /// <summary>Сначала обход DPI, при неудаче — через сервер (info.ru.json: dpiFirst).</summary>
    [ObservableProperty]
    public partial bool DpiFirst { get; set; }

    public string? DpiFirstReason => Core.Compatibility.CompatRules.Evaluate(_engine.State.Settings).Disabled.GetValueOrDefault("dpiFirst");

    public bool DpiFirstEnabled => DpiFirstReason is null;

    /// <summary>Как сейчас идёт каждая группа: напрямую с обходом или через сервер.</summary>
    public string? DpiFirstStatus
    {
        get
        {
            if (!DpiFirst)
                return null;
            var status = _engine.DpiStatus;
            if (status.Count == 0)
                return _engine.Status.State == ConnectionState.Connected
                    ? "Проверяю, где обход работает…"
                    : "После подключения Тропа проверит каждую группу и для неработающих сама включит сервер.";
            return string.Join(" · ", Core.Routing.DpiGroups.All.Where(g => status.ContainsKey(g.Key))
                .Select(g => g.Title + ": " + (status[g.Key] ? "обход DPI" : "через сервер")));
        }
    }

    partial void OnDpiFirstChanged(bool value)
    {
        OnPropertyChanged(nameof(DpiFirstStatus));
        Update(r => r with { DpiFirst = value });
    }

    [ObservableProperty]
    public partial string? NewApp { get; set; }

    [ObservableProperty]
    public partial string? NewSite { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial bool NeedsApply { get; set; }

    public string? AppRulesNote => _engine.TunAvailable && _engine.State.Settings.Connection.Mode == CaptureMode.Tun
        ? null
        : "Правила для приложений работают только в режиме «Весь компьютер»: системный прокси не знает, какая программа подключается.";

    private void Load()
    {
        _loading = true;
        var routing = _engine.State.Settings.Routing;
        PresetIndex = (int)routing.Preset;
        BlockQuic = routing.BlockQuic;
        DpiIndex = (int)_engine.State.Settings.Dpi.DpiPreset;
        OnPropertyChanged(nameof(DpiDescription));
        UdpProxy = routing.UdpProxy;
        DpiFirst = routing.DpiFirst;
        OnPropertyChanged(nameof(DpiFirstReason));
        OnPropertyChanged(nameof(DpiFirstEnabled));
        OnPropertyChanged(nameof(DpiFirstStatus));
        Apps.Clear();
        Sites.Clear();
        foreach (var rule in routing.Rules)
        {
            var item = new RuleItemViewModel(rule, Change, Remove);
            if (rule.Match.HasProcessCondition)
                Apps.Add(item);
            else
                Sites.Add(item);
        }

        OnPropertyChanged(nameof(AppRulesNote));
        OnPropertyChanged(nameof(StoreAppsVisible));
        OnPropertyChanged(nameof(StoreAppsSummary));
        _loading = false;
    }

    private void Update(Func<RoutingSettings, RoutingSettings> change)
    {
        if (_loading)
            return;
        _engine.UpdateSettings(s => s with { Routing = change(s.Routing) });
        NeedsApply = _engine.CoreRunning;
    }

    private void Change(Rule rule) =>
        Update(r => r with { Rules = r.Rules.Select(x => x.Id == rule.Id ? rule : x).ToList() });

    private void Remove(Guid id) => Update(r => r with { Rules = r.Rules.Where(x => x.Id != id).ToList() });

    partial void OnPresetIndexChanged(int value) => Update(r => r with { Preset = (RoutePreset)Math.Clamp(value, 0, 2) });

    partial void OnBlockQuicChanged(bool value) => Update(r => r with { BlockQuic = value });

    partial void OnDpiIndexChanged(int value)
    {
        OnPropertyChanged(nameof(DpiDescription));
        if (_loading)
            return;
        _engine.UpdateSettings(s => s with { Dpi = s.Dpi.WithPreset((DpiPreset)Math.Clamp(value, 0, 3)) });
        NeedsApply = _engine.CoreRunning;
    }

    partial void OnUdpProxyChanged(bool value) => Update(r => r with { UdpProxy = value });

    [RelayCommand]
    private void AddApp()
    {
        var name = RuleInput.NormalizeProcess(NewApp);
        if (name is null)
        {
            Error = "Укажите имя программы, например Discord.exe.";
            return;
        }

        if (Apps.Any(a => string.Equals(a.Title, name, StringComparison.OrdinalIgnoreCase)))
        {
            Error = $"Правило для {name} уже есть.";
            return;
        }

        Error = null;
        NewApp = null;
        Update(r => r with { Rules = [.. r.Rules, new Rule { Match = new RuleMatch { Processes = [name] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Proxy }] });
    }

    [RelayCommand]
    private void AddSite()
    {
        var domain = RuleInput.NormalizeDomain(NewSite);
        if (domain is null)
        {
            Error = "Укажите адрес сайта, например youtube.com.";
            return;
        }

        if (Sites.Any(s => s.Title == domain))
        {
            Error = $"Правило для {domain} уже есть.";
            return;
        }

        Error = null;
        NewSite = null;
        Update(r => r with { Rules = [.. r.Rules, new Rule { Match = new RuleMatch { DomainSuffixes = [domain] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Proxy }] });
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
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}
