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

    /// <summary>
    /// Варианты действия. Статический список: привязка к родителю пропадала, когда вкладку закрывали,
    /// выпадающий список терял варианты и показывал пустое поле при возврате.
    /// </summary>
    public static IReadOnlyList<string> Actions { get; } = ["Через сервер", "Напрямую", "Блокировать"];

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

    // -1 приходит, когда список пересоздаётся (ItemsSource ещё не привязан или уже отцеплен), —
    // раньше это сохранялось как «Через сервер» и сбрасывало выбранное «Напрямую».
    partial void OnTcpIndexChanged(int value)
    {
        if (value >= 0)
            Push(_rule with { Tcp = Action(value) });
        else
            RestoreLater(() => TcpIndex = Index(_rule.Tcp));
    }

    partial void OnUdpIndexChanged(int value)
    {
        if (value >= 0)
            Push(_rule with { Udp = Action(value) });
        else
            RestoreLater(() => UdpIndex = Index(_rule.Udp));
    }

    // Список на миг потерял выбор — возвращаем сохранённое значение, чтобы поле не осталось пустым.
    private static void RestoreLater(Action restore) => Avalonia.Threading.Dispatcher.UIThread.Post(restore);

    partial void OnEnabledChanged(bool value) => Push(_rule with { Enabled = value });

    /// <summary>Строку убрали с экрана: её выпадающие списки при отцеплении сбрасываются, это не выбор пользователя.</summary>
    public void Detach() => _loading = true;

    private void Push(Rule rule)
    {
        if (_loading)
            return;
        _rule = rule;
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
    private readonly GeoListEditorViewModel _geoEditor;
    private bool _loading;
    private IReadOnlyList<GeoListEdit>? _shownGeoEdits;

    /// <summary>Гео-базы, которые можно открыть и поправить (ADR-039).</summary>
    public ObservableCollection<GeoListRow> GeoLists { get; } = [];

    private IReadOnlyList<CustomGeoList>? _shownCustom;

    /// <summary>Свои списки (ADR-040).</summary>
    public ObservableCollection<GeoListRow> CustomLists { get; } = [];

    [RelayCommand]
    private void CreateList() => _geoEditor.OpenCustom(null);

    /// <summary>Своё место в очереди: список выше проверяется раньше и главнее нижнего.</summary>
    private void MoveList(Guid id, int delta)
    {
        Update(r =>
        {
            var lists = r.CustomLists.ToList();
            var i = lists.FindIndex(l => l.Id == id);
            var j = i + delta;
            if (i < 0 || j < 0 || j >= lists.Count)
                return r;
            (lists[i], lists[j]) = (lists[j], lists[i]);
            return r with { CustomLists = lists };
        });
    }

    public RulesViewModel(TropaEngine engine, InfoViewModel info, GeoListEditorViewModel geoEditor)
    {
        _engine = engine;
        _info = info;
        _geoEditor = geoEditor;
        geoEditor.Saved += (_, _) => NeedsApply = _engine.Status.State == ConnectionState.Connected;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Load);
        engine.ServiceChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(AppRulesNote));
            OnPropertyChanged(nameof(StoreAppsVisible));
        });
        engine.StatusChanged += (_, s) => Dispatcher.UIThread.Post(() =>
        {
            if (s.State != ConnectionState.Connected)
                NeedsApply = false;
        });
        Load();
        RefreshRunningApps();
    }

    public ObservableCollection<RuleItemViewModel> Apps { get; } = [];

    public ObservableCollection<RuleItemViewModel> Sites { get; } = [];

    /// <summary>Правила от администраторов подписок (ADR-048): только показ, менять их может администратор.</summary>
    public ObservableCollection<string> AdminRules { get; } = [];

    [ObservableProperty]
    public partial bool HasAdminRules { get; set; }

    [ObservableProperty]
    public partial string AdminRulesNote { get; set; } = "";

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
        NeedsApply = _engine.Status.State == ConnectionState.Connected;
    }


    [ObservableProperty]
    public partial int PresetIndex { get; set; }

    [ObservableProperty]
    public partial bool BlockQuic { get; set; }

    [ObservableProperty]
    public partial bool UdpProxy { get; set; }

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

    private IReadOnlyList<Rule>? _shownRules;

    private void LoadAdminRules()
    {
        var list = _engine.AdminRuleList();
        var lines = list.Select(x => (x.Trusted ? "" : "(не применяется) ") + Describe(x.Subscription, x.Rule)).ToList();
        if (!lines.SequenceEqual(AdminRules))
        {
            AdminRules.Clear();
            foreach (var line in lines)
                AdminRules.Add(line);
        }

        HasAdminRules = lines.Count > 0;
        AdminRulesNote = list.Any(x => !x.Trusted)
            ? "Правила с пометкой «не применяется» начнут работать, когда вы доверите администратору подписку на экране «Серверы». Ваши правила выше всегда главнее."
            : "Их меняет администратор подписки. Ваши правила выше главнее: чтобы сделать иначе, добавьте своё.";

        static string Describe(string sub, AdminRule r)
        {
            var what = string.Join(", ", r.Match.Processes.Concat(r.Match.DomainSuffixes).Concat(r.Match.Domains).Concat(r.Match.IpCidrs).Take(4))
                + (r.Match.Processes.Count + r.Match.DomainSuffixes.Count + r.Match.Domains.Count + r.Match.IpCidrs.Count > 4 ? " и ещё" : "");
            return $"«{sub}»{(r.Label is { Length: > 0 } l ? " · " + l : "")}: {what} — TCP {Act(r.Tcp)}, UDP {Act(r.Udp)}";
        }

        static string Act(AdminAction a) => a.Kind switch
        {
            RuleActionKind.Direct => "напрямую",
            RuleActionKind.Block => "блокировать",
            RuleActionKind.Server => "через «" + a.ServerName + "»",
            _ => "через сервер",
        };
    }

    private void Load()
    {
        LoadAdminRules();
        _loading = true;
        var routing = _engine.State.Settings.Routing;
        PresetIndex = (int)routing.Preset;
        BlockQuic = routing.BlockQuic;
        UdpProxy = routing.UdpProxy;
        // Состояние меняется часто (проверки серверов, подключение) — строки пересоздаём, только если правила другие.
        if (_shownRules is null || !_shownRules.SequenceEqual(routing.Rules))
        {
            foreach (var old in Apps.Concat(Sites))
                old.Detach();
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

            _shownRules = routing.Rules;
        }

        if (!ReferenceEquals(_shownGeoEdits, routing.GeoEdits) || !ReferenceEquals(_shownCustom, routing.CustomLists))
        {
            GeoLists.Clear();
            CustomLists.Clear();
            foreach (var list in GeoEdits.Catalog)
                GeoLists.Add(new GeoListRow(list, routing.GeoEdits.FirstOrDefault(e => e.Tag == list.Tag), _geoEditor.Open));
            var position = 0;
            foreach (var list in routing.CustomLists)
                CustomLists.Add(new GeoListRow(list, id => _geoEditor.OpenCustom(id), MoveList, ++position, routing.CustomLists.Count));
            _shownGeoEdits = routing.GeoEdits;
            _shownCustom = routing.CustomLists;
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
        NeedsApply = _engine.Status.State == ConnectionState.Connected;
    }

    private void Change(Rule rule)
    {
        Update(r => r with { Rules = r.Rules.Select(x => x.Id == rule.Id ? rule : x).ToList() });
        // Строка уже показывает новое значение — не пересоздаём список под открытым выпадающим меню.
        _shownRules = _engine.State.Settings.Routing.Rules;
    }

    private void Remove(Guid id) => Update(r => r with { Rules = r.Rules.Where(x => x.Id != id).ToList() });

    partial void OnPresetIndexChanged(int value) => Update(r => r with { Preset = (RoutePreset)Math.Clamp(value, 0, 2) });

    partial void OnBlockQuicChanged(bool value) => Update(r => r with { BlockQuic = value });

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
