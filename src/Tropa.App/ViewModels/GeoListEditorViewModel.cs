using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.Core.Model;
using Tropa.Core.Routing;
using Tropa.Infrastructure;

namespace Tropa.App.ViewModels;

/// <summary>Строка гео-базы в окне редактора.</summary>
internal sealed class GeoEntryRow(GeoEntry entry, bool added, bool removed)
{
    public GeoEntry Entry { get; } = entry;
    public string Value => Entry.Value;
    public bool IsAdded { get; } = added;
    public bool IsRemoved { get; } = removed;
    public bool IsOriginal => !IsAdded && !IsRemoved;

    public string KindLabel => Entry.Kind switch
    {
        GeoEntryKind.Cidr => Entry.Value.EndsWith("/32", StringComparison.Ordinal) || Entry.Value.EndsWith("/128", StringComparison.Ordinal) ? "адрес" : "сеть",
        GeoEntryKind.Domain => "только этот адрес",
        GeoEntryKind.Suffix => "с поддоменами",
        GeoEntryKind.Keyword => "содержит слово",
        _ => "выражение",
    };

    public string ActionTip => IsRemoved ? "Вернуть в список" : IsAdded ? "Убрать добавленное" : "Убрать из списка";
}

/// <summary>Вид записи в окне «Добавить записи»: название, как работает, пример.</summary>
internal sealed record GeoKindOption(GeoEntryKind? Kind, string Title, string Rule, string Example, string Placeholder)
{
    private static readonly IReadOnlyList<GeoKindOption> Ip =
    [
        new(GeoEntryKind.Cidr, "IP-адрес или сеть",
            "Адрес (1.2.3.4) или сеть с маской (5.6.0.0/16, 2a00:1450::/32).",
            "5.6.0.0/16 → 5.6.0.1, 5.6.200.7", "1.2.3.4\n5.6.0.0/16"),
    ];

    private static readonly IReadOnlyList<GeoKindOption> Sites =
    [
        new(GeoEntryKind.Suffix, "Домен и поддомены",
            "Адрес сайта и всё, что под ним. «www.» и https:// убираются сами.",
            "media-watch.ru → media-watch.ru, www.media-watch.ru, cdn.media-watch.ru", "media-watch.ru"),
        new(GeoEntryKind.Domain, "Только этот адрес",
            "Ровно этот адрес, без поддоменов.",
            "www.example.com → только www.example.com (не example.com)", "www.example.com"),
        new(GeoEntryKind.Keyword, "Содержит слово",
            "Любой адрес, в котором есть это слово. Латиница, цифры, точка, дефис.",
            "linked → linkedin.com, www.linkedin.cn, linkedin-ads.example", "linked"),
        new(GeoEntryKind.Regex, "Регулярное выражение",
            "Для опытных: выражение RE2 (как в sing-box), ищется в адресе. Каждое — с новой строки. Без ^ и $ ищется в любом месте адреса.",
            @"^(.+\.)?linked(in)?\.(com|cn)$ → linkedin.com, www.linked.cn", @"^(.+\.)?linkedin\.(com|cn)$"),
    ];

    /// <summary>Вставка из v2rayN / Xray / Clash: вид определяется по префиксу каждой строки.</summary>
    private static GeoKindOption Auto(bool ip, bool sites) => new(null, "Список из v2ray / Clash (вид по префиксу)",
        "Вставьте список как есть. domain: — домен и поддомены, full: — только этот адрес, keyword: — содержит слово, regexp: — выражение"
        + (ip ? ", IP и сети — как есть" : "") + (sites ? "; без префикса — домен и поддомены" : "") + ". Запятые и кавычки в конце строк не мешают.",
        "domain:faceit.com,  full:otvet.mail.ru,  keyword:xsolla" + (ip ? ",  91.108.4.0/22" : ""),
        "domain:faceit.com,\ndomain:xsolla.com,\nfull:otvet.mail.ru");

    public static IReadOnlyList<GeoKindOption> For(bool ip, bool sites) =>
        [Auto(ip, sites), .. sites ? Sites : [], .. ip ? Ip : []];

    public override string ToString() => Title;
}

/// <summary>Гео-база или свой список на экране «Правила»: название и что в нём поправлено.</summary>
internal sealed class GeoListRow
{
    public GeoListRow(GeoListInfo info, GeoListEdit? edit, Action<string> open)
    {
        Title = info.Title;
        Description = info.Description;
        Kind = info.IsIp ? "IP-адреса" : "Сайты";
        IsEdited = edit is { IsEmpty: false };
        EditSummary = edit is { IsEmpty: false } e
            ? string.Join(" · ", new[] { e.Added.Count > 0 ? $"добавлено {e.Added.Count}" : null, e.Removed.Count > 0 ? $"убрано {e.Removed.Count}" : null }.OfType<string>())
            : "";
        OpenCommand = new RelayCommand(() => open(info.Tag));
    }

    public GeoListRow(CustomGeoList list, Action<Guid> open)
    {
        Title = list.Name;
        var count = list.Entries.Count.ToString("N0", CultureInfo.GetCultureInfo("ru-RU"));
        Description = (list.Disabled ? "Выключен. " : "") + $"Свой список · записей: {count}";
        Kind = ActionLabel(list.Action);
        EditSummary = "";
        IsCustom = true;
        OpenCommand = new RelayCommand(() => open(list.Id));
    }

    public static string ActionLabel(RuleActionKind action) => action switch
    {
        RuleActionKind.Direct => "Напрямую",
        RuleActionKind.Block => "Блокировать",
        _ => "Через сервер",
    };

    public string Title { get; }
    public string Description { get; }
    public string Kind { get; }
    public bool IsEdited { get; }
    public bool IsCustom { get; }
    public string EditSummary { get; }
    public IRelayCommand OpenCommand { get; }
}

/// <summary>
/// Окно «Гео-база» (ADR-039): просмотр закреплённого набора, поиск, добавление и удаление записей.
/// Правки копятся в окне и сохраняются кнопкой «Сохранить»; файл .srs не меняется.
/// </summary>
internal sealed partial class GeoListEditorViewModel : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private readonly DispatcherTimer _filterTimer;
    /// <summary>Номер открытия окна: результат чтения, пришедший после закрытия или открытия другого набора, отбрасывается.</summary>
    private int _generation;

    private IReadOnlyList<GeoEntry> _original = [];
    private HashSet<GeoEntry> _originalSet = [];
    private readonly List<GeoEntry> _added = [];
    private readonly HashSet<GeoEntry> _addedSet = [];
    private readonly HashSet<GeoEntry> _removed = [];
    private GeoListInfo? _list;
    private int _shown;

    // Свой список (ADR-040): null — открыт встроенный набор.
    private Guid? _customId;
    private bool _hasIp;
    private bool _hasSites;
    private bool _loadingCustom;

    public GeoListEditorViewModel(TropaEngine engine, InfoViewModel info)
    {
        _engine = engine;
        _info = info;
        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _filterTimer.Tick += (_, _) =>
        {
            _filterTimer.Stop();
            Refilter();
        };
    }

    /// <summary>Правки сохранены: экран «Правила» предлагает переподключиться.</summary>
    public event EventHandler? Saved;

    public static IReadOnlyList<string> Views { get; } = ["Весь список", "Добавленные мной", "Убранные мной"];

    public static IReadOnlyList<string> Actions { get; } = ["Через сервер", "Напрямую", "Блокировать"];

    /// <summary>Открыт свой список: можно менять название, действие и удалить его.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuiltIn), nameof(CheckPlaceholder))]
    public partial bool IsCustom { get; set; }

    public bool IsBuiltIn => !IsCustom;

    [ObservableProperty]
    public partial string? ListName { get; set; }

    [ObservableProperty]
    public partial int ActionIndex { get; set; }

    [ObservableProperty]
    public partial bool ListEnabled { get; set; } = true;

    /// <summary>Первое нажатие «Удалить список» спрашивает подтверждение.</summary>
    [ObservableProperty]
    public partial bool ConfirmDelete { get; set; }

    partial void OnListNameChanged(string? value) => MarkCustomDirty();

    partial void OnActionIndexChanged(int value) => MarkCustomDirty();

    partial void OnListEnabledChanged(bool value) => MarkCustomDirty();

    private void MarkCustomDirty()
    {
        if (IsCustom && !_loadingCustom)
            IsDirty = true;
    }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    public partial string Description { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSites), nameof(CheckPlaceholder))]
    public partial bool IsIp { get; set; }

    public bool IsSites => !IsIp;

    public string CheckPlaceholder => IsCustom
        ? "Сайт или IP-адрес: linkedin.com или 1.2.3.4"
        : IsIp
            ? "IP-адрес или сайт: 1.2.3.4 или linkedin.com (адрес сайта Тропа узнает у DNS)"
            : "Адрес сайта: linkedin.com или https://www.linkedin.com/feed";

    [ObservableProperty]
    public partial IReadOnlyList<GeoEntryRow> Items { get; set; } = [];

    [ObservableProperty]
    public partial string? Filter { get; set; }

    [ObservableProperty]
    public partial int ViewIndex { get; set; }

    // ---------- Окно «Добавить записи» ----------

    [ObservableProperty]
    public partial bool IsAddOpen { get; set; }

    /// <summary>Виды записей для текущего набора.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<GeoKindOption> AddKinds { get; set; } = [];

    [ObservableProperty]
    public partial GeoKindOption? AddKind { get; set; }

    [ObservableProperty]
    public partial string? NewText { get; set; }

    [ObservableProperty]
    public partial string? AddError { get; set; }

    partial void OnAddKindChanged(GeoKindOption? value) => AddError = null;

    // ---------- Проверка адреса ----------

    [ObservableProperty]
    public partial string? CheckText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckHit), nameof(CheckMiss), nameof(CheckBad))]
    public partial string? CheckResult { get; set; }

    /// <summary>true — попадает, false — не попадает, null — ошибка ввода.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckHit), nameof(CheckMiss), nameof(CheckBad))]
    public partial bool? CheckMatched { get; set; }

    public bool CheckHit => CheckMatched == true && CheckResult is not null;
    public bool CheckMiss => CheckMatched == false && CheckResult is not null;
    public bool CheckBad => CheckMatched is null && CheckResult is not null;

    [ObservableProperty]
    public partial bool IsChecking { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    public partial bool IsDirty { get; set; }

    public bool CanSave => IsDirty && !IsBusy;

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanSave));

    public string Summary
    {
        get
        {
            if (IsBusy)
                return "Читаю набор…";
            var total = _original.Count - _removed.Count(_originalSet.Contains) + _added.Count;
            var parts = new List<string> { $"Записей: {total.ToString("N0", Ru)}" };
            if (_added.Count > 0)
                parts.Add($"добавлено {_added.Count.ToString("N0", Ru)}");
            if (_removed.Count > 0)
                parts.Add($"убрано {_removed.Count.ToString("N0", Ru)}");
            if (!string.IsNullOrWhiteSpace(Filter) || ViewIndex != 0)
                parts.Add($"найдено {_shown.ToString("N0", Ru)}");
            return string.Join(" · ", parts);
        }
    }

    public string EmptyText => ViewIndex switch
    {
        1 => "Вы ещё ничего не добавили.",
        2 => "Вы ещё ничего не убрали.",
        _ => string.IsNullOrWhiteSpace(Filter) ? "Список пуст." : "Ничего не найдено.",
    };

    public bool IsEmpty => !IsBusy && Items.Count == 0;

    partial void OnItemsChanged(IReadOnlyList<GeoEntryRow> value) => OnPropertyChanged(nameof(IsEmpty));

    partial void OnFilterChanged(string? value)
    {
        _filterTimer.Stop();
        _filterTimer.Start();
    }

    partial void OnViewIndexChanged(int value)
    {
        if (value >= 0)
            Refilter();
    }

    private void Reset(string title, string description, bool ip, bool sites)
    {
        Title = title;
        Description = description;
        _hasIp = ip;
        _hasSites = sites;
        IsIp = ip && !sites;
        Filter = null;
        ViewIndex = 0;
        NewText = null;
        IsAddOpen = false;
        AddError = null;
        AddKinds = GeoKindOption.For(ip, sites);
        AddKind = AddKinds[0];
        CheckText = null;
        CheckResult = null;
        CheckMatched = null;
        Error = null;
        Message = null;
        ConfirmDelete = false;
        _original = [];
        _originalSet = [];
        _added.Clear();
        _addedSet.Clear();
        _removed.Clear();
    }

    /// <summary>Открыть свой список; null — создать новый (сохраняется кнопкой «Сохранить»).</summary>
    public void OpenCustom(Guid? id)
    {
        ++_generation;
        var existing = id is { } g ? _engine.State.Settings.Routing.CustomLists.FirstOrDefault(l => l.Id == g) : null;
        _list = null;
        _customId = existing?.Id ?? Guid.NewGuid();
        _loadingCustom = true;
        IsCustom = true;
        Reset(existing?.Name ?? "Новый список", "Свой список: сайты и адреса с одним действием. Проверяется после правил «Приложения» и «Сайты».", ip: true, sites: true);
        ListName = existing?.Name ?? "Новый список";
        ActionIndex = existing?.Action switch { RuleActionKind.Direct => 1, RuleActionKind.Block => 2, _ => 0 };
        ListEnabled = existing is not { Disabled: true };
        _loadingCustom = false;
        _original = existing?.Entries ?? [];
        _originalSet = [.. _original];
        IsDirty = existing is null;
        IsBusy = false;
        Items = [];
        IsOpen = true;
        if (existing is null)
            Message = "Дайте списку название, выберите действие и нажмите «Добавить…» — список из v2ray можно вставить как есть.";
        Refilter();
    }

    public async void Open(string tag)
    {
        if (GeoEdits.Find(tag) is not { } list)
            return;
        var generation = ++_generation;

        _list = list;
        _customId = null;
        IsCustom = false;
        Reset(list.Title, list.Description, list.IsIp, !list.IsIp);
        var edit = _engine.State.Settings.Routing.GeoEdits.FirstOrDefault(e => e.Tag == tag);
        foreach (var e in edit?.Added ?? [])
        {
            if (_addedSet.Add(e))
                _added.Add(e);
        }

        foreach (var e in edit?.Removed ?? [])
            _removed.Add(e);
        IsDirty = false;
        Items = [];
        IsOpen = true;
        IsBusy = true;
        OnPropertyChanged(nameof(Summary));
        try
        {
            var content = await _engine.ReadGeoListAsync(tag);
            if (generation != _generation)
                return;
            _original = content.Entries;
            _originalSet = [.. content.Entries];
            if (content.Skipped > 0)
                Message = $"Правил другого вида в наборе: {content.Skipped}. Их здесь не видно, но они работают.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException
                                       or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or Infrastructure.Cores.IntegrityException)
        {
            if (generation == _generation)
                Error = "Не удалось открыть набор: " + ex.Message;
        }
        finally
        {
            if (generation == _generation)
                IsBusy = false;
        }

        if (generation == _generation)
            Refilter();
    }

    private void Refilter()
    {
        var q = Filter?.Trim();
        bool Hit(GeoEntry e) => string.IsNullOrEmpty(q) || e.Value.Contains(q, StringComparison.OrdinalIgnoreCase);

        var rows = new List<GeoEntryRow>();
        switch (ViewIndex)
        {
            case 1:
                rows.AddRange(_added.Where(Hit).Select(e => new GeoEntryRow(e, added: true, removed: false)));
                break;
            case 2:
                rows.AddRange(_removed.Where(Hit).Select(e => new GeoEntryRow(e, added: false, removed: true)));
                break;
            default:
                // Добавленные — сверху: их обычно и ищут.
                rows.AddRange(_added.Where(Hit).Select(e => new GeoEntryRow(e, added: true, removed: false)));
                foreach (var e in _original)
                {
                    if (!_removed.Contains(e) && !_addedSet.Contains(e) && Hit(e))
                        rows.Add(new GeoEntryRow(e, added: false, removed: false));
                }

                break;
        }

        _shown = rows.Count;
        Items = rows;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EmptyText));
    }

    [RelayCommand]
    private void OpenAdd()
    {
        NewText = null;
        AddError = null;
        IsAddOpen = true;
    }

    [RelayCommand]
    private void CloseAdd() => IsAddOpen = false;

    [RelayCommand]
    private void Add()
    {
        if ((_list is null && !IsCustom) || AddKind is not { } option || string.IsNullOrWhiteSpace(NewText))
            return;
        List<GeoEntry> entries;
        List<string> bad;
        var skippedNotes = new List<string>();
        if (option.Kind is { } kind)
        {
            (entries, bad) = GeoEdits.ParseInput(NewText, kind);
        }
        else
        {
            var auto = GeoEdits.ParseAuto(NewText, _hasIp, _hasSites);
            (entries, bad) = (auto.Entries, auto.Bad);
            if (auto.GeoRefs.Count > 0)
                skippedNotes.Add($"не перенесены ссылки на базы ({string.Join(", ", auto.GeoRefs.Take(3))}{(auto.GeoRefs.Count > 3 ? "…" : "")}) — в Тропе они уже есть, их можно открыть в «Гео-базах»");
            if (auto.WrongPlace.Count > 0)
                skippedNotes.Add(_hasIp
                    ? $"не IP-адреса: {auto.WrongPlace.Count} — сайты добавляют в списки сайтов"
                    : $"IP-адреса пропущены: {auto.WrongPlace.Count} — их добавляют в IP-список или в свой список");
        }

        if (entries.Count == 0)
        {
            AddError = skippedNotes.Count > 0 && bad.Count == 0
                ? "Нечего добавить: " + string.Join("; ", skippedNotes) + "."
                : !_hasIp && bad.All(b => GeoEdits.Cidr(b) is not null)
                    ? "Это IP-адреса — их добавляют в IP-список, например «Заблокированные IP-адреса»."
                    : $"Не подходит для вида «{option.Title}»: {string.Join(", ", bad.Take(3))}{(bad.Count > 3 ? "…" : "")}. {option.Rule}";
            return;
        }

        var added = 0;
        var already = 0;
        foreach (var e in entries)
        {
            if (_removed.Remove(e))
            {
                added++;
                continue;
            }

            if (_addedSet.Contains(e) || _originalSet.Contains(e))
            {
                already++;
                continue;
            }

            if (_added.Count >= GeoEdits.MaxEntries)
            {
                Error = $"Добавить можно не больше {GeoEdits.MaxEntries.ToString("N0", Ru)} записей в один набор.";
                break;
            }

            _added.Add(e);
            _addedSet.Add(e);
            added++;
        }

        if (added > 0)
        {
            IsDirty = true;
            Error = null;
        }

        var notes = new List<string> { added > 0 ? (option.Kind is null ? $"Добавлено: {added}" : $"Добавлено: {added} ({option.Title.ToLowerInvariant()})") : "Ничего нового не добавлено" };
        if (already > 0)
            notes.Add($"уже были в списке: {already}");
        if (bad.Count > 0)
            notes.Add($"не распознано: {string.Join(", ", bad.Take(3))}{(bad.Count > 3 ? "…" : "")}");
        Message = string.Join(" · ", notes) + (added > 0 ? ". Не забудьте «Сохранить»." : ".");
        IsAddOpen = false;
        CheckResult = null;
        Refilter();
    }

    /// <summary>
    /// Попадает ли адрес в набор с учётом ещё не сохранённых правок — так же, как его проверит sing-box.
    /// Для IP-наборов адрес сайта узнаётся у системного DNS (только по нажатию кнопки).
    /// </summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        if ((_list is null && !IsCustom) || IsBusy || IsChecking)
            return;
        var generation = _generation;
        var title = IsCustom && !string.IsNullOrWhiteSpace(ListName) ? ListName.Trim() : Title;
        var input = CheckText?.Trim();
        CheckMatched = null;
        if (string.IsNullOrEmpty(input))
        {
            CheckResult = null;
            return;
        }

        string? host = null;
        IPAddress[] ips = [];
        if (IPAddress.TryParse(input, out var ip))
        {
            if (!_hasIp)
            {
                CheckResult = "Это IP-адрес, а в этом списке — сайты. Проверьте его в IP-списке или введите адрес сайта.";
                return;
            }

            ips = [ip];
        }
        else
        {
            host = GeoEdits.Host(input);
            if (host is null)
            {
                CheckResult = "Это не адрес сайта. Пример: linkedin.com.";
                return;
            }

            // DNS спрашиваем, только если в списке есть сети: свой список из одних доменов проверяется по имени.
            var needIp = _hasIp && (!_hasSites || _original.Concat(_added).Any(e => e.Kind == GeoEntryKind.Cidr));
            if (needIp)
            {
                IsChecking = true;
                try
                {
                    ips = await Dns.GetHostAddressesAsync(host);
                }
                catch (SocketException)
                {
                    CheckResult = $"Не удалось узнать IP-адрес {host}: DNS не ответил. Введите IP-адрес сам.";
                    return;
                }
                finally
                {
                    IsChecking = false;
                }

                if (generation != _generation)
                    return;

                // При подключённом VPN с FakeIP (Тропа, v2rayN) DNS отвечает условными адресами — по ним проверять нельзя.
                var fake = ips.Where(a => GeoEdits.Matches(new GeoEntry(GeoEntryKind.Cidr, "198.18.0.0/15"), null, a)
                                          || GeoEdits.Matches(new GeoEntry(GeoEntryKind.Cidr, "fc00::/18"), null, a)).ToList();
                ips = [.. ips.Except(fake)];
                if (ips.Length == 0 && fake.Count > 0 && !_hasSites)
                {
                    CheckResult = $"Настоящий адрес {host} сейчас не узнать: включён VPN с подменой DNS (FakeIP), и он ответил условным адресом {fake[0]}. "
                        + "Отключитесь или введите IP-адрес сами (его показывает, например, nslookup на другом устройстве).";
                    return;
                }
            }
        }

        var regexes = new Dictionary<string, Regex?>(StringComparer.Ordinal);
        Regex? Rx(string pattern)
        {
            if (!regexes.TryGetValue(pattern, out var r))
                regexes[pattern] = r = GeoEdits.CompileForMatch(pattern);
            return r;
        }

        // IP-набор — по каждому адресу сайта; набор сайтов — один проход по имени.
        List<(string? Host, IPAddress? Ip)> targets = [.. ips.Select(a => ((string?)null, (IPAddress?)a))];
        if (_hasSites && host is not null)
            targets.Insert(0, (host, null));
        const int Limit = 20;
        var hits = new List<(GeoEntry Entry, bool Added, IPAddress? Ip)>();
        var removedHits = new List<GeoEntry>();
        foreach (var (h, a) in targets)
        {
            foreach (var e in _added)
            {
                if (GeoEdits.Matches(e, h, a, Rx))
                    hits.Add((e, true, a));
            }

            foreach (var e in _original)
            {
                if (hits.Count >= Limit)
                    break;
                if (!GeoEdits.Matches(e, h, a, Rx))
                    continue;
                if (_removed.Contains(e))
                    removedHits.Add(e);
                else
                    hits.Add((e, false, a));
            }
        }

        var subject = host ?? input;
        var resolved = _hasIp && host is not null && (ips.Length > 0 || !_hasSites)
            ? ips.Length == 0 ? " (DNS не вернул адресов)" : $" ({string.Join(", ", ips.Take(4))}{(ips.Length > 4 ? "…" : "")})"
            : "";
        string Describe((GeoEntry Entry, bool Added, IPAddress? Ip) m) =>
            $"• «{m.Entry.Value}» — {new GeoEntryRow(m.Entry, m.Added, false).KindLabel}"
            + (m.Added ? ", добавлено вами" : "")
            + (m.Ip is not null && host is not null ? $", для адреса {m.Ip}" : "");

        if (hits.Count > 0)
        {
            CheckMatched = true;
            var lines = new List<string> { $"{subject}{resolved} попадает в список «{title}». Совпало:" };
            lines.AddRange(hits.Take(5).Select(Describe));
            if (hits.Count > 5)
                lines.Add(hits.Count >= Limit ? "…и ещё много." : $"…и ещё {hits.Count - 5}.");
            CheckResult = string.Join("\n", lines);
        }
        else
        {
            CheckMatched = false;
            var text = $"{subject}{resolved} не попадает в список «{title}».";
            if (removedHits.Count > 0)
                text += $"\nСовпала бы запись «{removedHits[0].Value}», но вы её убрали.";
            CheckResult = text;
        }
    }

    [RelayCommand]
    private void Toggle(GeoEntryRow? row)
    {
        if (row is null)
            return;
        if (row.IsRemoved)
        {
            _removed.Remove(row.Entry);
        }
        else if (row.IsAdded)
        {
            _added.Remove(row.Entry);
            _addedSet.Remove(row.Entry);
        }
        else
        {
            if (_removed.Count >= GeoEdits.MaxEntries)
            {
                Error = $"Убрать можно не больше {GeoEdits.MaxEntries.ToString("N0", Ru)} записей из одного набора.";
                return;
            }

            _removed.Add(row.Entry);
        }

        IsDirty = true;
        // Убираем одну строку, а не пересобираем весь список: прокрутка остаётся на месте.
        var rows = Items.Where(r => !ReferenceEquals(r, row)).ToList();
        _shown = rows.Count;
        Items = rows;
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void ResetEdits()
    {
        if (_added.Count == 0 && _removed.Count == 0)
            return;
        _added.Clear();
        _addedSet.Clear();
        _removed.Clear();
        IsDirty = true;
        Message = "Все ваши правки этого набора убраны. Нажмите «Сохранить», чтобы вернуть набор как был.";
        Refilter();
    }

    [RelayCommand]
    private void Save()
    {
        if (IsCustom && _customId is { } id)
        {
            SaveCustom(id);
            return;
        }

        if (_list is null)
            return;
        var tag = _list.Tag;
        var edit = new GeoListEdit { Tag = tag, Added = [.. _added], Removed = [.. _removed] };
        _engine.UpdateSettings(s =>
        {
            var others = s.Routing.GeoEdits.Where(e => e.Tag != tag);
            var list = edit.IsEmpty ? others.ToList() : [.. others, edit];
            return s with { Routing = s.Routing with { GeoEdits = list } };
        });
        IsDirty = false;
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void SaveCustom(Guid id)
    {
        var name = Tropa.Core.Security.NameSanitizer.Sanitize(ListName, "");
        if (name.Length == 0)
        {
            Error = "Дайте списку название, например «Игры».";
            return;
        }

        var list = new CustomGeoList
        {
            Id = id,
            Name = name,
            Disabled = !ListEnabled,
            Action = ActionIndex switch { 1 => RuleActionKind.Direct, 2 => RuleActionKind.Block, _ => RuleActionKind.Proxy },
            Entries = GeoEdits.Apply(_original, new GeoListEdit { Tag = "custom", Added = [.. _added], Removed = [.. _removed] }),
        };
        var full = false;
        _engine.UpdateSettings(s =>
        {
            var lists = s.Routing.CustomLists.ToList();
            var i = lists.FindIndex(l => l.Id == id);
            if (i >= 0)
                lists[i] = list;
            else if (lists.Count < GeoEdits.MaxCustomLists)
                lists.Add(list);
            else
                full = true;
            return s with { Routing = s.Routing with { CustomLists = lists } };
        });
        if (full)
        {
            Error = $"Своих списков может быть не больше {GeoEdits.MaxCustomLists}.";
            return;
        }

        IsDirty = false;
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void DeleteList()
    {
        if (!IsCustom || _customId is not { } id)
            return;
        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }

        _engine.UpdateSettings(s => s with { Routing = s.Routing with { CustomLists = [.. s.Routing.CustomLists.Where(l => l.Id != id)] } });
        ConfirmDelete = false;
        IsDirty = false;
        IsOpen = false;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Close()
    {
        _generation++;
        IsBusy = false;
        IsOpen = false;
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}
