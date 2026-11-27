using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Model;
using Tropa.Core.Routing;
using Tropa.Infrastructure;

namespace Tropa.App.ViewModels;

/// <summary>Карточка маршрута в списке (ADR-067).</summary>
internal sealed partial class RouteCardViewModel(RouteChoice route, string subtitle, bool active, RoutesViewModel owner) : ObservableObject
{
    public string Key { get; } = route.Key;
    public string Name { get; } = route.Name;
    public string Subtitle { get; } = subtitle;
    public bool IsActive { get; } = active;
    public bool IsBuiltIn => route.IsBuiltIn;
    public bool IsCustom => !route.IsBuiltIn;
    public string KindLabel => route.IsBuiltIn ? "встроенный" : "свой";
    public Guid? RouteId => route.RouteId;

    [RelayCommand]
    private Task ActivateAsync() => owner.ActivateAsync(Key);

    [RelayCommand]
    private void Edit() => owner.OpenEditor(this);

    [RelayCommand]
    private void Duplicate() => owner.Duplicate(Key);

    [RelayCommand]
    private void Delete() => owner.AskDelete(this);
}

/// <summary>
/// Экран «Маршруты» (ADR-067): все маршруты карточками, действующий отмечен, свои можно создать,
/// продублировать и удалить; двойной щелчок или «⋯ → Редактировать» открывает редактор пунктов.
/// Общие настройки (QUIC, UDP, Store, гео-базы, правила администратора) — из <see cref="RulesViewModel"/>.
/// </summary>
internal sealed partial class RoutesViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private readonly GeoListEditorViewModel _geoEditor;
    private string? _shownSignature;

    public RoutesViewModel(TropaEngine engine, InfoViewModel info, GeoListEditorViewModel geoEditor, RulesViewModel shared)
    {
        _engine = engine;
        _info = info;
        _geoEditor = geoEditor;
        Shared = shared;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Load);
        Load();
    }

    /// <summary>Общие для всех маршрутов карточки (QUIC, UDP, Store, гео-базы, администратор).</summary>
    public RulesViewModel Shared { get; }

    public ObservableCollection<RouteCardViewModel> Routes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(IsList), nameof(CardColumns))]
    public partial RouteEditorViewModel? Editor { get; set; }

    public bool IsEditing => Editor is not null;
    public bool IsList => Editor is null;

    /// <summary>Карточки в две колонки, при открытом редакторе справа — в одну (ADR-072).</summary>
    public int CardColumns => IsEditing ? 1 : 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteQuestion))]
    public partial RouteCardViewModel? PendingDelete { get; set; }

    public string DeleteQuestion => PendingDelete is { } c ? $"Удалить маршрут «{c.Name}»? Его сайты, приложения и списки пропадут." : "";

    [ObservableProperty]
    public partial string? Error { get; set; }

    /// <summary>Экран открыт из меню: показываем список, а не оставленный редактор.</summary>
    public void OnShown()
    {
        Editor?.Detach();
        Editor = null;
        PendingDelete = null;
        Error = null;
        Shared.RefreshRunningAppsCommand.Execute(null);
    }

    private void Load()
    {
        var s = _engine.State.Settings;
        var active = RouteBook.ActiveKey(s);
        var signature = active + "|" + string.Join("|", s.Routing.Routes.Select(r => $"{r.Id:N}:{r.Name}:{r.Base}:{r.Rules.Count}:{r.CustomLists.Count}"))
            + $"|{s.Routing.Rules.Count}:{s.Routing.CustomLists.Count}";
        if (signature != _shownSignature)
        {
            _shownSignature = signature;
            Routes.Clear();
            foreach (var choice in RouteBook.Choices(s))
                Routes.Add(new RouteCardViewModel(choice, Subtitle(s, choice), choice.Key == active, this));
        }

        // Редактор удалённого маршрута закрываем.
        if (Editor is { RouteId: { } id } && s.Routing.Routes.All(r => r.Id != id))
        {
            Editor.Detach();
            Editor = null;
        }
    }

    private static string Subtitle(AppSettings s, RouteChoice c)
    {
        var (rules, lists) = RouteBook.Items(s.Routing, c.RouteId);
        var items = rules.Count + lists.Count;
        var what = items == 0 ? "пунктов нет" : $"пунктов: {items}";
        return c.IsBuiltIn
            ? RouteBook.BaseDescription(c.Base) + $" Свои сайты и приложения (общие для встроенных): {(items == 0 ? "нет" : items.ToString(System.Globalization.CultureInfo.InvariantCulture))}."
            : $"Основа: «{RouteBook.BaseTitle(c.Base)}» · {what}.";
    }

    internal async Task ActivateAsync(string key)
    {
        Error = null;
        try
        {
            await _engine.SelectRouteAsync(key);
            Shared.NeedsApply = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Infrastructure.Service.ServiceException or TimeoutException)
        {
            Error = _engine.Scrubber.Scrub(ex.Message);
        }
    }

    internal void OpenEditor(RouteCardViewModel card)
    {
        Editor?.Detach();
        PendingDelete = null;
        Editor = new RouteEditorViewModel(_engine, _info, _geoEditor, Shared, card.RouteId, CloseEditor, Duplicate);
    }

    [RelayCommand]
    private void CloseEditor()
    {
        Editor?.Detach();
        Editor = null;
    }

    [RelayCommand]
    private void Create()
    {
        if (_engine.State.Settings.Routing.Routes.Count >= RouteBook.MaxRoutes)
        {
            Error = $"Своих маршрутов может быть не больше {RouteBook.MaxRoutes}.";
            return;
        }

        Error = null;
        var route = new CustomRoute { Name = RouteBook.UniqueName(_engine.State.Settings.Routing, "Новый маршрут") };
        _engine.UpdateSettings(s => s with { Routing = s.Routing with { Routes = [.. s.Routing.Routes, route] } });
        Editor?.Detach();
        Editor = new RouteEditorViewModel(_engine, _info, _geoEditor, Shared, route.Id, CloseEditor, Duplicate) { IsNew = true };
    }

    /// <summary>Копия маршрута как нового своего — и сразу его редактор.</summary>
    internal void Duplicate(string key)
    {
        if (_engine.State.Settings.Routing.Routes.Count >= RouteBook.MaxRoutes)
        {
            Error = $"Своих маршрутов может быть не больше {RouteBook.MaxRoutes}.";
            return;
        }

        Error = null;
        var copy = RouteBook.Duplicate(_engine.State.Settings.Routing, key);
        _engine.UpdateSettings(s => s with { Routing = s.Routing with { Routes = [.. s.Routing.Routes, copy] } });
        Editor?.Detach();
        Editor = new RouteEditorViewModel(_engine, _info, _geoEditor, Shared, copy.Id, CloseEditor, Duplicate);
    }

    internal void AskDelete(RouteCardViewModel card)
    {
        if (card.IsBuiltIn)
            return;
        PendingDelete = card;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (PendingDelete is not { RouteId: { } id } card)
            return;
        PendingDelete = null;
        var wasActive = card.IsActive;
        _engine.UpdateSettings(s => s with { Routing = RouteBook.Remove(s.Routing, id) });
        if (Editor?.RouteId == id)
            CloseEditor();
        // Удалили действующий маршрут — действует встроенный: подключение переходит на него сразу.
        if (wasActive)
            await ActivateAsync(RouteBook.ActiveKey(_engine.State.Settings));
    }

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}

/// <summary>
/// Редактор маршрута (ADR-067): название и основа своего маршрута, его сайты, приложения, наборы гео-баз
/// и списки с масками и поддоменами. RouteId = null — общие пункты трёх встроенных маршрутов.
/// </summary>
internal sealed partial class RouteEditorViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private readonly GeoListEditorViewModel _geoEditor;
    private readonly RulesViewModel _shared;
    private readonly Action _close;
    private readonly Action<string> _duplicate;
    private readonly EventHandler _onState;
    private IReadOnlyList<Rule>? _shownRules;
    private IReadOnlyList<CustomGeoList>? _shownLists;
    private string? _shownServers;
    private string? _shownListServers;
    private bool _loading;

    /// <summary>Основы своего маршрута в порядке выпадающего списка.</summary>
    private static readonly RoutePreset[] BaseOrder = [RoutePreset.ItemsOnly, RoutePreset.BlockedOnly, RoutePreset.ExceptRu, RoutePreset.All];

    public static IReadOnlyList<string> Bases { get; } = [.. BaseOrder.Select(RouteBook.BaseTitle)];

    public RouteEditorViewModel(TropaEngine engine, InfoViewModel info, GeoListEditorViewModel geoEditor, RulesViewModel shared,
        Guid? routeId, Action close, Action<string> duplicate)
    {
        _engine = engine;
        _info = info;
        _geoEditor = geoEditor;
        _shared = shared;
        _close = close;
        _duplicate = duplicate;
        RouteId = routeId;
        GeoSets = [.. RouteBook.ImportableSets.Select(g => g.Title + (RuleSets.IsIpSet(g.Tag) ? " (IP-адреса)" : " (сайты)"))];
        _onState = (_, _) => Dispatcher.UIThread.Post(Load);
        engine.StateChanged += _onState;
        geoEditor.Saved += OnListSaved;
        Load();
    }

    public Guid? RouteId { get; }

    public bool IsBuiltIn => RouteId is null;

    public bool IsCustom => RouteId is not null;

    /// <summary>Только что созданный маршрут: подсказка, что делать дальше.</summary>
    public bool IsNew { get; init; }

    public ObservableCollection<RuleItemViewModel> Items { get; } = [];

    public ObservableCollection<GeoListRow> Lists { get; } = [];

    public ObservableCollection<string> RunningApps => _shared.RunningApps;

    public IReadOnlyList<string> GeoSets { get; }

    public string? AppRulesNote => _shared.AppRulesNote;

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string? Name { get; set; }

    [ObservableProperty]
    public partial int BaseIndex { get; set; }

    [ObservableProperty]
    public partial string BaseDescription { get; set; } = "";

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string? NewItem { get; set; }

    [ObservableProperty]
    public partial int GeoSetIndex { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public bool HasItems => Items.Count > 0;

    public static string BuiltInNote =>
        "Эти сайты, приложения и списки общие для трёх встроенных маршрутов («Всё, кроме российского», «Только заблокированное», «Всё через сервер») — как раньше на экране «Правила». "
        + "Основу встроенного маршрута не поменять: сделайте дубликат — получится свой маршрут с теми же пунктами.";

    /// <summary>Отписка от событий, когда редактор закрыт.</summary>
    public void Detach()
    {
        _engine.StateChanged -= _onState;
        _geoEditor.Saved -= OnListSaved;
        foreach (var item in Items)
            item.Detach();
    }

    private void OnListSaved(object? sender, EventArgs e) => MarkChanged();

    private void Load()
    {
        var s = _engine.State.Settings;
        var route = RouteId is { } id ? s.Routing.Routes.FirstOrDefault(r => r.Id == id) : null;
        if (RouteId is not null && route is null)
            return; // маршрут удалён — экран закроет редактор
        _loading = true;
        try
        {
            IsActive = RouteId is { } rid ? RouteBook.ActiveCustom(s)?.Id == rid : RouteBook.ActiveCustom(s) is null;
            if (route is not null)
            {
                if (Name is null)
                    Name = route.Name;
                BaseIndex = Math.Max(0, Array.IndexOf(BaseOrder, route.Base));
                BaseDescription = RouteBook.BaseDescription(route.Base);
                Title = "Маршрут «" + RouteBook.RouteName(route) + "»";
            }
            else
            {
                Title = "Встроенные маршруты";
            }

            var (rules, lists) = RouteBook.Items(s.Routing, RouteId);
            var servers = ServerOption.Signature(_engine);
            if (_shownRules is null || !_shownRules.SequenceEqual(rules) || servers != _shownServers)
            {
                foreach (var old in Items)
                    old.Detach();
                Items.Clear();
                foreach (var rule in rules)
                    Items.Add(new RuleItemViewModel(rule, ServerOption.List(_engine, RulesViewModel.ServerOf(rule)), Change, Remove));
                _shownRules = rules;
                _shownServers = servers;
                OnPropertyChanged(nameof(HasItems));
            }

            if (!ReferenceEquals(_shownLists, lists) || servers != _shownListServers)
            {
                Lists.Clear();
                var position = 0;
                foreach (var list in lists)
                    Lists.Add(new GeoListRow(list, listId => _geoEditor.OpenCustom(listId, RouteId), MoveList, ++position, lists.Count, ServerName(list.ServerId)));
                _shownLists = lists;
                _shownListServers = servers;
            }

            OnPropertyChanged(nameof(AppRulesNote));
        }
        finally
        {
            _loading = false;
        }
    }

    private string? ServerName(Guid? id) =>
        id is { } sid ? _engine.State.Profiles.FirstOrDefault(p => p.Profile.Id == sid)?.Profile.Name ?? "удалённый сервер" : null;

    /// <summary>Изменение пунктов действующего маршрута при подключении — предлагаем «Применить сейчас».</summary>
    private void MarkChanged()
    {
        if (IsActive && _engine.Status.State == ConnectionState.Connected)
            _shared.NeedsApply = true;
    }

    private void UpdateRules(Func<IReadOnlyList<Rule>, IReadOnlyList<Rule>> change)
    {
        var id = RouteId;
        _engine.UpdateSettings(s => s with { Routing = RouteBook.WithRules(s.Routing, id, change) });
        MarkChanged();
    }

    private void UpdateRoute(Func<CustomRoute, CustomRoute> change)
    {
        if (RouteId is not { } id)
            return;
        _engine.UpdateSettings(s => s with { Routing = s.Routing with { Routes = s.Routing.Routes.Select(r => r.Id == id ? change(r) : r).ToList() } });
    }

    private void Change(Rule rule)
    {
        UpdateRules(rules => rules.Select(x => x.Id == rule.Id ? rule : x).ToList());
        // Строка уже показывает новое значение — не пересоздаём список под открытым выпадающим меню.
        _shownRules = RouteBook.Items(_engine.State.Settings.Routing, RouteId).Rules;
    }

    private void Remove(Guid id) => UpdateRules(rules => rules.Where(x => x.Id != id).ToList());

    private void MoveList(Guid listId, int delta)
    {
        var id = RouteId;
        _engine.UpdateSettings(s => s with
        {
            Routing = RouteBook.WithLists(s.Routing, id, current =>
            {
                var lists = current.ToList();
                var i = lists.FindIndex(l => l.Id == listId);
                var j = i + delta;
                if (i < 0 || j < 0 || j >= lists.Count)
                    return current;
                (lists[i], lists[j]) = (lists[j], lists[i]);
                return lists;
            }),
        });
        MarkChanged();
    }

    partial void OnNameChanged(string? value)
    {
        if (_loading || RouteId is null)
            return;
        var name = Core.Security.NameSanitizer.Sanitize(value, "");
        if (name.Length == 0)
            return;
        UpdateRoute(r => r with { Name = name });
    }

    partial void OnBaseIndexChanged(int value)
    {
        if (_loading || RouteId is null)
            return;
        if (value < 0 || value >= BaseOrder.Length)
        {
            Dispatcher.UIThread.Post(Load);
            return;
        }

        var preset = BaseOrder[value];
        BaseDescription = RouteBook.BaseDescription(preset);
        UpdateRoute(r => r with { Base = preset });
        MarkChanged();
    }

    /// <summary>
    /// Одно поле для всего: «Discord.exe» или имя запущенной программы — приложение, адрес сайта — сайт
    /// с поддоменами, IP или сеть — адреса. Маски, слова и выражения — через «Список…».
    /// </summary>
    [RelayCommand]
    private void AddItem()
    {
        Error = null;
        Message = null;
        var raw = NewItem?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            Error = "Введите адрес сайта (youtube.com) или программу (Discord.exe).";
            return;
        }

        var (rules, _) = RouteBook.Items(_engine.State.Settings.Routing, RouteId);
        Rule? rule = null;
        var isApp = raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || raw.Contains('\\', StringComparison.Ordinal)
            || RunningApps.Any(a => string.Equals(a, raw, StringComparison.OrdinalIgnoreCase));
        if (isApp)
        {
            if (RuleInput.NormalizeProcess(raw) is not { } name)
            {
                Error = "Не похоже на имя программы. Пример: Discord.exe.";
                return;
            }

            if (rules.Any(r => r.Match.Processes.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                Error = $"{name} уже есть в маршруте.";
                return;
            }

            // Предупреждение о режиме и так видно над списком (AppRulesNote) — второй раз не повторяем.
            rule = new Rule { Match = new RuleMatch { Processes = [name] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Proxy };
        }
        else if (GeoEdits.ParseInput(raw, GeoEntryKind.Cidr) is { Entries: [var cidr], Bad.Count: 0 })
        {
            if (rules.Any(r => r.Match.IpCidrs.Contains(cidr.Value)))
            {
                Error = $"{cidr.Value} уже есть в маршруте.";
                return;
            }

            rule = new Rule { Match = new RuleMatch { IpCidrs = [cidr.Value] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Proxy };
        }
        else if (RuleInput.NormalizeDomain(raw) is { } domain)
        {
            if (rules.Any(r => r.Match.DomainSuffixes.Contains(domain)))
            {
                Error = $"{domain} уже есть в маршруте.";
                return;
            }

            rule = new Rule { Match = new RuleMatch { DomainSuffixes = [domain] }, Tcp = RuleAction.Proxy, Udp = RuleAction.Proxy };
            if (raw.Contains('*', StringComparison.Ordinal))
                Message = $"Добавлено {domain} — сайт и все его поддомены. Маски сложнее (слово, выражение) — кнопкой «Создать список».";
        }
        else
        {
            Error = "Не похоже ни на сайт, ни на программу, ни на IP-адрес. Маски, слова и регулярные выражения — кнопкой «Создать список».";
            return;
        }

        NewItem = null;
        UpdateRules(list => [.. list, rule]);
    }

    [RelayCommand]
    private void AddGeoSet()
    {
        Error = null;
        Message = null;
        if (GeoSetIndex < 0 || GeoSetIndex >= RouteBook.ImportableSets.Count)
            return;
        var tag = RouteBook.ImportableSets[GeoSetIndex].Tag;
        var (rules, _) = RouteBook.Items(_engine.State.Settings.Routing, RouteId);
        if (rules.Any(r => RouteBook.GeoSetTag(r) == tag))
        {
            Error = "Этот набор уже есть в маршруте.";
            return;
        }

        UpdateRules(list => [.. list, RouteBook.GeoSetRule(tag, RuleAction.Proxy)]);
    }

    [RelayCommand]
    private void CreateList() => _geoEditor.OpenCustom(null, RouteId);

    [RelayCommand]
    private void RefreshRunningApps() => _shared.RefreshRunningAppsCommand.Execute(null);

    [RelayCommand]
    private void Duplicate() => _duplicate(RouteId is { } id ? id.ToString("N") : RouteBook.BuiltIn[Math.Clamp((int)_engine.State.Settings.Routing.Preset, 0, 2)].Key);

    [RelayCommand]
    private void Close() => _close();

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}
