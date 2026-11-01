using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.App.Services;
using Tropa.Core.Compatibility;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Core.Testing;
using Tropa.Infrastructure;
using Tropa.Infrastructure.Testing;

namespace Tropa.App.ViewModels;

/// <summary>Вкладка: все серверы, одна подписка или «Мои» (добавленные вручную).</summary>
internal sealed partial class GroupTab(string title, Guid? subscriptionId, bool manual) : ObservableObject
{
    public string Title { get; } = title;
    public Guid? SubscriptionId { get; } = subscriptionId;
    public bool Manual { get; } = manual;
    public bool All => SubscriptionId is null && !Manual;

    public bool IsSubscription => SubscriptionId is not null;

    /// <summary>Сколько серверов этой вкладки пользователь удалил (скрыл).</summary>
    public int HiddenCount { get; init; }

    public bool HasHidden => HiddenCount > 0;

    public string RestoreLabel => $"Вернуть удалённые ({HiddenCount})";

    /// <summary>Подписка от администратора (ADR-048): пояснение и кнопка доверия; null — обычная подписка.</summary>
    public string? AdminNote { get; init; }

    public bool HasAdmin => AdminNote is not null;

    public bool TrustAdmin { get; init; }

    public string TrustLabel => TrustAdmin ? "Не доверять администратору" : "Доверять администратору";

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Стрелка сортировки у заголовка колонки: « ↑», « ↓» или "".</summary>
internal sealed class SortMarks : ObservableObject
{
    private static readonly string[] Columns = ["type", "name", "address", "port", "network", "tls", "sub", "delay", "speed", "country", "status"];

    private string? _column;
    private bool _descending;

    public string Type => Mark("type");
    public string Name => Mark("name");
    public string Address => Mark("address");
    public string Port => Mark("port");
    public string Network => Mark("network");
    public string Tls => Mark("tls");
    public string Sub => Mark("sub");
    public string Delay => Mark("delay");
    public string Speed => Mark("speed");
    public string Country => Mark("country");
    public string Status => Mark("status");

    private string Mark(string column) => column == _column ? (_descending ? " ↓" : " ↑") : "";

    public void Set(string? column, bool descending)
    {
        (_column, _descending) = (column, descending);
        foreach (var c in Columns)
            OnPropertyChanged(char.ToUpperInvariant(c[0]) + c[1..]);
    }
}

/// <summary>Пункт фильтра стран: Code = null — «Все страны».</summary>
internal sealed record CountryOption(string? Code, string Title)
{
    public static CountryOption Any { get; } = new(null, "Все страны");

    public override string ToString() => Title;
}

internal static class CoreTexts
{
    public static string Name(CoreChoice c) => c switch { CoreChoice.Xray => "Xray", CoreChoice.SingBox => "sing-box", _ => "авто" };
}

internal sealed partial class ServerItemViewModel(Profile profile) : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public Profile Profile { get; } = profile;
    public Guid Id => Profile.Id;
    public string Name => Format.NameWithoutFlag(Profile.Name);

    /// <summary>
    /// Страна выхода по последней проверке (не по названию сервера: выход у провайдера может переключаться).
    /// «–» — проверки не было или страну узнать не удалось.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFlag))]
    public partial Avalonia.Media.Imaging.Bitmap? Flag { get; set; }

    [ObservableProperty]
    public partial string Country { get; set; } = "–";

    [ObservableProperty]
    public partial string? CountryTip { get; set; } = "Страна выхода появится после проверки сервера";

    public bool HasFlag => Flag is not null;
    public string Protocol => Format.Protocol(Profile);
    public string Transport => Format.Transport(Profile);

    // Отдельные колонки для вида «как в v2rayN» (ADR-051).
    public string Address => Profile.Address;
    public int Port => Profile.Port;

    public string Network => Profile.Transport.Type switch
    {
        TransportType.Tcp => "tcp",
        TransportType.Ws => "ws",
        TransportType.Grpc => "grpc",
        TransportType.HttpUpgrade => "httpupgrade",
        _ => "xhttp",
    };

    public string Tls => Profile.Security.Type switch
    {
        SecurityType.Reality => "reality",
        SecurityType.Tls => "tls",
        _ => "",
    };

    /// <summary>Название подписки сервера; «Мои» — добавленные вручную.</summary>
    public string SubscriptionName { get; init; } = "";

    /// <summary>Для сортировки: числа последней проверки (null — не проверялся или не ответил).</summary>
    public long? DelayMs { get; private set; }

    public double? SpeedMbps { get; private set; }

    /// <summary>Ядро сервера: выбранное пользователем или найденное проверкой.</summary>
    public string CoreLabel { get; init; } = "";

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string Delay { get; set; } = "—";

    [ObservableProperty]
    public partial string Speed { get; set; } = "—";

    [ObservableProperty]
    public partial string Status { get; set; } = "";

    /// <summary>ok / warn / bad / "" — для цвета плашки статуса.</summary>
    [ObservableProperty]
    public partial string StatusKind { get; set; } = "";

    [ObservableProperty]
    public partial bool Removed { get; set; }

    // Подробности для правой панели.
    [ObservableProperty]
    public partial string Tcp { get; set; } = "—";

    [ObservableProperty]
    public partial string Udp { get; set; } = "—";

    [ObservableProperty]
    public partial string Stability { get; set; } = "не проверялась";

    [ObservableProperty]
    public partial string? Explanation { get; set; } = "Сервер ещё не проверялся.";

    [ObservableProperty]
    public partial string Checked { get; set; } = "";

    [ObservableProperty]
    public partial bool Testing { get; set; }

    public void Apply(ServerTestResult? r)
    {
        if (r is null)
            return;
        var status = ServerHealth.Classify(r);
        Flag = Flags.Get(r.ExitCountry);
        Country = r.ExitCountry ?? "–";
        CountryTip = r.ExitCountry is { } code
            ? $"Выход: {new Tropa.Core.Diagnostics.ExitRegion(code, r.At).Name}. Проверено {r.At.ToLocalTime():dd.MM HH:mm}; при следующей проверке может быть другой, если сервер переключает выход."
            : "Страну выхода узнать не удалось: сервер не ответил или проверка страны выключена в настройках";
        DelayMs = r.DelayMs;
        SpeedMbps = r.Frozen ? 0 : r.SpeedMbps;
        Delay = r.DelayMs is { } ms ? $"{ms} мс" : "—";
        Speed = r.Frozen ? "замерзает" : r.SpeedMbps is { } sp ? sp.ToString("0.#", Ru) + " Мбит/с" : "—";
        Tcp = r.TcpMs is { } tcp ? $"{tcp} мс" : Profile.ChainVia is null ? "нет ответа" : "через цепочку";
        Udp = r.UdpOk switch { true => "работает", false => "не проходит", _ => "—" };
        if (r.Loss is { } loss)
            Stability = $"потери {loss:P0}, медиана {r.MedianMs} мс, разброс ±{r.JitterMs} мс";
        Status = status is HealthStatus.HandshakeError or HealthStatus.NoResponse && r.Error is { } e ? e : ServerHealth.Title(status);
        StatusKind = status switch
        {
            HealthStatus.Working => "ok",
            HealthStatus.Slow => "warn",
            HealthStatus.Unknown => "",
            _ => "bad",
        };
        // У рабочего сервера пояснения нет: null прячет и карточку, и подсказку в списке.
        Explanation = ServerHealth.Explain(r) is { Length: > 0 } text ? text : null;
        Checked = "Проверено " + r.At.ToLocalTime().ToString("dd.MM HH:mm", Ru);
    }
}

/// <summary>Экран «Серверы»: подписки, список, проверки, выбор сервера.</summary>
internal sealed partial class ServersViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private readonly Action _openImport;
    private readonly EditServerViewModel _edit;

    public ServersViewModel(TropaEngine engine, InfoViewModel info, Action openImport, EditServerViewModel edit)
    {
        _edit = edit;
        _engine = engine;
        _info = info;
        _openImport = openImport;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!IsBusy)
                Refresh();
        });
        Refresh();
    }

    public ObservableCollection<GroupTab> Tabs { get; } = [];

    public ObservableCollection<ServerItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial ServerItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotBusy))]
    public partial bool IsBusy { get; set; }

    public bool NotBusy => !IsBusy;

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    private GroupTab? CurrentTab => Tabs.FirstOrDefault(t => t.IsSelected);

    private void Refresh()
    {
        var s = _engine.State;
        var selectedTab = CurrentTab;
        var visible = s.Profiles.Where(p => !p.Hidden).ToList();
        FillCountries(visible);
        Tabs.Clear();
        Tabs.Add(new GroupTab($"Все · {visible.Count}", null, false) { HiddenCount = s.Profiles.Count(p => p.Hidden) });
        foreach (var sub in s.Subscriptions)
        {
            Tabs.Add(new GroupTab($"{sub.Name} · {visible.Count(p => p.Profile.SubscriptionId == sub.Id)}", sub.Id, false)
            {
                HiddenCount = s.Profiles.Count(p => p.Hidden && p.Profile.SubscriptionId == sub.Id),
                TrustAdmin = sub.TrustAdmin,
                AdminNote = sub.Admin is not { } admin ? null
                    : sub.TrustAdmin
                        ? $"Подписка от администратора: он выбирает сервер ({admin.ActiveServer ?? "не выбран"}) и присылает правил: {admin.Rules.Count}. Ваши правила главнее. Подписка проверяется раз в {admin.RefreshMinutes ?? 5} мин."
                        : $"Администратор подписки может выбирать вам сервер и присылать правила (сейчас правил: {admin.Rules.Count}). Пока вы ему не доверите, берутся только серверы.",
            });
        }

        var manual = visible.Count(p => p.Profile.SubscriptionId is null);
        if (manual > 0)
            Tabs.Add(new GroupTab($"Мои · {manual}", null, true));
        var restore = Tabs.FirstOrDefault(t => selectedTab is not null && t.SubscriptionId == selectedTab.SubscriptionId && t.Manual == selectedTab.Manual) ?? Tabs[0];
        restore.IsSelected = true;
        OnPropertyChanged(nameof(CurrentTabInfo));

        RebuildItems();
        var updated = s.Subscriptions.Where(x => x.LastUpdated is not null).Select(x => x.LastUpdated!.Value).DefaultIfEmpty().Max();
        Summary = visible.Count == 0 && s.Subscriptions.Count == 0
            ? "Серверов пока нет"
            : Format.Plural(visible.Count, "сервер", "сервера", "серверов")
              + (s.Subscriptions.Count > 0 ? " · " + Format.Plural(s.Subscriptions.Count, "подписка", "подписки", "подписок") : "")
              + (updated == default ? "" : $" · обновлено {updated.LocalDateTime:dd.MM HH:mm}");
        IsEmpty = visible.Count == 0 && s.Subscriptions.Count == 0;
    }

    /// <summary>Открытая вкладка: для полосы действий с подпиской и кнопки «Вернуть удалённые».</summary>
    public GroupTab? CurrentTabInfo => CurrentTab;

    private void RebuildItems()
    {
        var s = _engine.State;
        var tab = CurrentTab;
        var selectedId = Selected?.Id;
        var subNames = s.Subscriptions.ToDictionary(x => x.Id, x => x.Name);
        var rows = new List<ServerItemViewModel>();
        foreach (var sp in s.Profiles.Where(p => !p.Hidden).OrderBy(p => p.Order))
        {
            if (tab is { All: false } && (tab.Manual ? sp.Profile.SubscriptionId is not null : sp.Profile.SubscriptionId != tab.SubscriptionId))
                continue;
            var item = new ServerItemViewModel(sp.Profile)
            {
                IsActive = sp.Profile.Id == s.ActiveProfileId,
                Removed = sp.RemovedByProvider is not null,
                CoreLabel = CoreText(sp),
                SubscriptionName = sp.Profile.SubscriptionId is { } subId ? subNames.GetValueOrDefault(subId, "") : "Мои",
            };
            var issues = ProfileCompat.Issues(sp.Profile);
            if (issues.Count > 0)
                (item.Status, item.StatusKind, item.Explanation) = ("Несовместимые параметры", "bad", issues[0]);
            else if (item.Removed)
                (item.Status, item.StatusKind) = ("Удалён провайдером", "warn");
            else
                item.Apply(sp.LastTest);

            // Без проверки сертификата — предупреждение рядом с результатом теста, а не вместо него:
            // иначе у рабочего сервера не видно ни задержки, ни скорости.
            if (sp.Profile.Security.AllowInsecure && issues.Count == 0 && s.Settings.Dpi.AllowInsecureWarn)
            {
                const string insecure = "У сервера отключена проверка сертификата: трафик может прочитать посредник.";
                if (sp.LastTest is null || item.Removed)
                    (item.Status, item.StatusKind, item.Explanation) = ("Без проверки сертификата", "bad", insecure);
                else
                {
                    item.Explanation = (insecure + " " + item.Explanation).TrimEnd();
                    if (item.StatusKind == "ok")
                        item.StatusKind = "warn";
                }
            }

            if (item.IsActive && string.IsNullOrEmpty(item.Status))
                (item.Status, item.StatusKind) = ("Выбран", "ok");
            if (Matches(item, Filter) && PassesFilters(item))
                rows.Add(item);
        }

        Items.Clear();
        foreach (var item in Sorted(rows))
            Items.Add(item);
        Selected = Items.FirstOrDefault(i => i.Id == selectedId) ?? Items.FirstOrDefault(i => i.IsActive) ?? Items.FirstOrDefault();
    }

    /// <summary>Поиск по имени, адресу, протоколу, транспорту и стране. Пусто — все серверы вкладки.</summary>
    [ObservableProperty]
    public partial string? Filter { get; set; }

    partial void OnFilterChanged(string? value) => FiltersChanged();

    // ---------------- Фильтры (оба вида окна): протокол, скорость, страна выхода, название ----------------

    /// <summary>Варианты фильтра протокола: «Все протоколы» и протоколы, которые есть у Тропы.</summary>
    public static IReadOnlyList<string> ProtocolOptions { get; } = ["Все протоколы", "VLESS", "VMess", "Trojan", "Shadowsocks", "Hysteria2"];

    /// <summary>Варианты фильтра скорости по последней проверке.</summary>
    public static IReadOnlyList<string> SpeedOptions { get; } = ["Любая скорость", "от 5 Мбит/с", "от 20 Мбит/с", "от 50 Мбит/с", "от 100 Мбит/с", "Проверенные и рабочие"];

    private static readonly double[] SpeedThresholds = [0, 5, 20, 50, 100];

    [ObservableProperty]
    public partial int FilterProtocolIndex { get; set; }

    partial void OnFilterProtocolIndexChanged(int value) => FiltersChanged();

    [ObservableProperty]
    public partial int FilterSpeedIndex { get; set; }

    partial void OnFilterSpeedIndexChanged(int value) => FiltersChanged();

    /// <summary>Страны выхода, которые есть среди серверов (по последней проверке), и «Все страны».</summary>
    public ObservableCollection<CountryOption> CountryOptions { get; } = [CountryOption.Any];

    [ObservableProperty]
    public partial CountryOption? FilterCountry { get; set; } = CountryOption.Any;

    partial void OnFilterCountryChanged(CountryOption? value)
    {
        if (!_fillingCountries)
            FiltersChanged();
    }

    private bool _fillingCountries;

    public bool HasFilters => !string.IsNullOrWhiteSpace(Filter) || FilterProtocolIndex > 0 || FilterSpeedIndex > 0 || FilterCountry is { Code: not null };

    private void FiltersChanged()
    {
        OnPropertyChanged(nameof(HasFilters));
        RebuildItems();
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Filter = null;
        FilterProtocolIndex = 0;
        FilterSpeedIndex = 0;
        FilterCountry = CountryOption.Any;
        FiltersChanged();
    }

    private bool PassesFilters(ServerItemViewModel item)
    {
        if (FilterProtocolIndex > 0 && !string.Equals(item.Protocol, ProtocolOptions[FilterProtocolIndex], StringComparison.Ordinal))
            return false;
        if (FilterCountry is { Code: { } code } && !string.Equals(item.Country, code, StringComparison.OrdinalIgnoreCase))
            return false;
        return FilterSpeedIndex switch
        {
            0 => true,
            5 => item.StatusKind is "ok" or "warn",
            var i => item.SpeedMbps is { } sp && sp >= SpeedThresholds[i],
        };
    }

    /// <summary>Список стран для фильтра — из последних проверок; выбранная страна сохраняется.</summary>
    private void FillCountries(IEnumerable<StoredProfile> visible)
    {
        var codes = visible.Select(p => p.LastTest?.ExitCountry).OfType<string>()
            .Select(c => c.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();
        var keep = FilterCountry;
        _fillingCountries = true;
        try
        {
            CountryOptions.Clear();
            CountryOptions.Add(CountryOption.Any);
            foreach (var code in codes)
                CountryOptions.Add(new CountryOption(code, $"{code} — {new Tropa.Core.Diagnostics.ExitRegion(code, DateTimeOffset.Now).Name}"));
            FilterCountry = CountryOptions.FirstOrDefault(o => o.Code == keep?.Code) ?? CountryOption.Any;
        }
        finally
        {
            _fillingCountries = false;
        }
    }

    /// <summary>Колонка сортировки показа (type, name, address, port, network, tls, sub, delay, speed, country, status); null — свой порядок.</summary>
    [ObservableProperty]
    public partial string? SortColumn { get; private set; }

    [ObservableProperty]
    public partial bool SortDescending { get; private set; }

    /// <summary>Стрелки у заголовков колонок: Marks.Delay → « ↑», « ↓» или "".</summary>
    public SortMarks Marks { get; } = new();

    /// <summary>Щелчок по заголовку: по возрастанию → по убыванию → свой порядок. Сохранённый порядок не меняется.</summary>
    [RelayCommand]
    private void SortBy(string column)
    {
        if (SortColumn != column)
            (SortColumn, SortDescending) = (column, false);
        else if (!SortDescending)
            SortDescending = true;
        else
            (SortColumn, SortDescending) = (null, false);
        Marks.Set(SortColumn, SortDescending);
        RebuildItems();
    }

    /// <summary>Вид «Тропа» без сортировки по колонкам: показ возвращается к своему порядку (фильтры есть в обоих видах).</summary>
    public void ResetView()
    {
        (SortColumn, SortDescending) = (null, false);
        Marks.Set(null, false);
        RebuildItems();
    }

    private static bool Matches(ServerItemViewModel item, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        var f = filter.Trim();
        return item.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Address.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Protocol.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Transport.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.SubscriptionName.Contains(f, StringComparison.OrdinalIgnoreCase)
            || item.Country.Equals(f, StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<ServerItemViewModel> Sorted(List<ServerItemViewModel> rows)
    {
        if (SortColumn is null)
            return rows;
        var ru = StringComparer.Create(CultureInfo.GetCultureInfo("ru-RU"), ignoreCase: true);
        // Числа без результата (не проверялся, нет ответа) всегда в конце, в какую сторону ни сортируй.
        IOrderedEnumerable<ServerItemViewModel> ByNumber(Func<ServerItemViewModel, double?> key) => SortDescending
            ? rows.OrderBy(r => key(r) is null).ThenByDescending(r => key(r))
            : rows.OrderBy(r => key(r) is null).ThenBy(r => key(r));
        IEnumerable<ServerItemViewModel> ByText(Func<ServerItemViewModel, string> key) => SortDescending
            ? rows.OrderByDescending(key, ru)
            : rows.OrderBy(key, ru);
        return SortColumn switch
        {
            "type" => ByText(r => r.Protocol),
            "address" => ByText(r => r.Address),
            "port" => ByNumber(r => r.Port),
            "network" => ByText(r => r.Network),
            "tls" => ByText(r => r.Tls),
            "sub" => ByText(r => r.SubscriptionName),
            "delay" => ByNumber(r => r.DelayMs),
            "speed" => ByNumber(r => r.SpeedMbps),
            "country" => ByText(r => r.Country),
            "status" => ByNumber(r => r.StatusKind switch { "ok" => 0, "warn" => 1, "bad" => 2, _ => null }),
            _ => ByText(r => r.Name),
        };
    }

    [RelayCommand]
    private async Task ToggleTrustAdmin()
    {
        if (CurrentTab is not { SubscriptionId: { } id } tab)
            return;
        await _engine.SetTrustAdminAsync(id, !tab.TrustAdmin);
        Message = tab.TrustAdmin
            ? "Администратор больше не выбирает сервер и не присылает правила. Серверы подписки остаются."
            : "Готово: сервер и правила администратора применяются, изменения доходят сами.";
    }

    [RelayCommand]
    private void SelectTab(GroupTab tab)
    {
        foreach (var t in Tabs)
            t.IsSelected = t == tab;
        PendingSubscriptionDelete = null;
        PendingSubscriptionName = null;
        OnPropertyChanged(nameof(CurrentTabInfo));
        RebuildItems();
    }

    [RelayCommand]
    private void Add() => _openImport();

    [RelayCommand]
    private void AddManual() => _edit.OpenNew();

    [RelayCommand]
    private void EditSelected()
    {
        if (Selected is { } s)
            _edit.OpenEdit(s.Profile);
    }

    [RelayCommand]
    private async Task UseSelectedAsync()
    {
        if (Selected is { } item)
            await _engine.SetActiveAsync(item.Id);
    }

    // ---------------- Выделение нескольких серверов (как в v2rayN) ----------------

    private List<ServerItemViewModel> _picked = [];

    /// <summary>Выделенные строки (Ctrl/Shift + щелчок, Ctrl+A). Пусто — действует строка под курсором.</summary>
    public void SetPicked(IEnumerable<ServerItemViewModel> items)
    {
        _picked = items.ToList();
        OnPropertyChanged(nameof(PickedCount));
    }

    public int PickedCount => _picked.Count;

    /// <summary>С чем работает действие: все выделенные, а если выделена одна строка или ни одной — текущая.</summary>
    private List<ServerItemViewModel> Targets() =>
        _picked.Count > 1 ? _picked.Where(Items.Contains).ToList() : Selected is { } s ? [s] : [];

    // ---------------- Подтверждение действий над многими серверами ----------------

    private Action? _pendingAction;

    /// <summary>Вопрос перед удалением нескольких серверов или выгрузкой ключей; null — спрашивать нечего.</summary>
    [ObservableProperty]
    public partial string? PendingConfirm { get; set; }

    [ObservableProperty]
    public partial string PendingConfirmLabel { get; set; } = "Удалить";

    private void Ask(string question, string confirmLabel, Action action)
    {
        PendingSubscriptionDelete = null;
        (_pendingAction, PendingConfirmLabel, PendingConfirm) = (action, confirmLabel, question);
    }

    [RelayCommand]
    private void CancelPending() => (_pendingAction, PendingConfirm) = (null, null);

    [RelayCommand]
    private void ConfirmPending()
    {
        var action = _pendingAction;
        (_pendingAction, PendingConfirm) = (null, null);
        action?.Invoke();
    }

    /// <summary>Сколько из удаляемых — свои (стираются насовсем), остальные из подписок только скрываются.</summary>
    private string RemovalNote(IReadOnlyCollection<Guid> ids)
    {
        var own = _engine.State.Profiles.Count(p => ids.Contains(p.Profile.Id) && p.Profile.SubscriptionId is null);
        return own == 0
            ? "Серверы подписок только скрыты — вернуть: «Вернуть удалённые»."
            : $"Ваши серверы ({own}) удалены насовсем, серверы подписок только скрыты — вернуть: «Вернуть удалённые».";
    }

    private void RemoveMany(IReadOnlyCollection<Guid> ids, string done)
    {
        _engine.RemoveProfiles(ids);
        Refresh();
        Message = done;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        var targets = Targets();
        if (targets.Count > 1)
        {
            var ids = targets.Select(t => t.Id).ToList();
            RemoveMany(ids, $"Удалено серверов: {ids.Count}. {RemovalNote(ids)}");
            return;
        }

        if (Selected is not { } item)
            return;
        var index = Items.IndexOf(item);
        // Соседний сервер становится выбранным, чтобы можно было удалять подряд.
        var next = Items.ElementAtOrDefault(index + 1) ?? Items.ElementAtOrDefault(index - 1);
        _engine.RemoveProfile(item.Id);
        Refresh();
        Selected = Items.FirstOrDefault(i => i.Id == next?.Id) ?? Items.FirstOrDefault();
        Message = item.Profile.SubscriptionId is null
            ? $"Сервер «{item.Name}» удалён."
            : $"Сервер «{item.Name}» убран из списка и не вернётся при обновлении подписки. Вернуть: «Вернуть удалённые» на вкладке подписки.";
    }

    [RelayCommand]
    private void MoveUp(ServerItemViewModel? item) => Move(item ?? Selected, -1);

    [RelayCommand]
    private void MoveDown(ServerItemViewModel? item) => Move(item ?? Selected, +1);

    private void Move(ServerItemViewModel? item, int delta)
    {
        if (item is null)
            return;
        // Двигаем в своём порядке: при сортировке по колонке «выше/ниже» иначе ничего бы не значило.
        if (SortColumn is not null)
        {
            (SortColumn, SortDescending) = (null, false);
            Marks.Set(null, false);
            RebuildItems();
        }

        _engine.MoveProfile(item.Id, delta, Items.Select(i => i.Id).ToList());
        Refresh();
        Selected = Items.FirstOrDefault(i => i.Id == item.Id);
    }

    [RelayCommand]
    private void RestoreHidden()
    {
        _engine.RestoreHidden(CurrentTab?.SubscriptionId);
        Message = "Удалённые серверы возвращены в список.";
    }

    /// <summary>Вопрос «Удалить подписку…?», пока пользователь не подтвердил.</summary>
    [ObservableProperty]
    public partial string? PendingSubscriptionDelete { get; set; }

    [RelayCommand]
    private void AskRemoveSubscription()
    {
        if (CurrentTab is not { SubscriptionId: { } id } || _engine.State.Subscriptions.FirstOrDefault(x => x.Id == id) is not { } sub)
            return;
        var count = _engine.State.Profiles.Count(p => p.Profile.SubscriptionId == id);
        PendingSubscriptionDelete = $"Удалить подписку «{sub.Name}» вместе с серверами ({count})? Адрес подписки тоже удалится — чтобы вернуть её, понадобится ссылка от продавца.";
    }

    [RelayCommand]
    private void CancelRemoveSubscription() => PendingSubscriptionDelete = null;

    [RelayCommand]
    private void ConfirmRemoveSubscription()
    {
        PendingSubscriptionDelete = null;
        if (CurrentTab is not { SubscriptionId: { } id })
            return;
        var name = _engine.State.Subscriptions.FirstOrDefault(x => x.Id == id)?.Name;
        _engine.RemoveSubscription(id);
        foreach (var t in Tabs)
            t.IsSelected = false;
        Refresh();
        Message = $"Подписка «{name}» удалена.";
    }

    /// <summary>Новое имя открытой подписки, пока поле переименования открыто; null — поле закрыто.</summary>
    [ObservableProperty]
    public partial string? PendingSubscriptionName { get; set; }

    [RelayCommand]
    private void AskRenameSubscription()
    {
        if (CurrentTab is { SubscriptionId: { } id } && _engine.State.Subscriptions.FirstOrDefault(x => x.Id == id) is { } sub)
            PendingSubscriptionName = sub.Name;
    }

    [RelayCommand]
    private void CancelRenameSubscription() => PendingSubscriptionName = null;

    [RelayCommand]
    private void ConfirmRenameSubscription()
    {
        var name = PendingSubscriptionName;
        PendingSubscriptionName = null;
        if (CurrentTab is not { SubscriptionId: { } id })
            return;
        _engine.RenameSubscription(id, name);
        Refresh();
        Message = string.IsNullOrWhiteSpace(name)
            ? "Подписке возвращено имя от провайдера: оно появится после обновления подписки."
            : $"Подписка переименована: «{_engine.State.Subscriptions.FirstOrDefault(x => x.Id == id)?.Name}».";
    }

    [RelayCommand]
    private void MoveSubscriptionLeft() => MoveSubscription(-1);

    [RelayCommand]
    private void MoveSubscriptionRight() => MoveSubscription(+1);

    private void MoveSubscription(int delta)
    {
        if (CurrentTab is { SubscriptionId: { } id })
            _engine.MoveSubscription(id, delta);
    }

    [RelayCommand]
    private async Task UpdateCurrentSubscriptionAsync()
    {
        if (CurrentTab is not { SubscriptionId: { } id } || _engine.State.Subscriptions.FirstOrDefault(x => x.Id == id) is not { } sub)
            return;
        IsBusy = true;
        Message = null;
        try
        {
            var r = await _engine.UpdateSubscriptionAsync(id);
            Message = $"{sub.Name}: +{r.Added}, обновлено {r.Updated}" + (r.Messages.Count > 0 ? " — " + r.Messages[0] : "");
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task UpdateSubscriptionsAsync()
    {
        IsBusy = true;
        Message = null;
        try
        {
            var notes = new List<string>();
            foreach (var sub in _engine.State.Subscriptions.ToList())
            {
                var r = await _engine.UpdateSubscriptionAsync(sub.Id);
                notes.Add($"{sub.Name}: +{r.Added}, обновлено {r.Updated}" + (r.Messages.Count > 0 ? " — " + r.Messages[0] : ""));
            }

            Message = notes.Count == 0 ? "Подписок пока нет." : string.Join("\n", notes);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private Task TestAllAsync() => RunTestsAsync(Items.ToList(), TestKinds.Standard,
        "Проверяю серверы: пинг, задержка, скорость и UDP. Текущее подключение не прерывается…");

    [RelayCommand]
    private Task TestSelectedAsync() => Targets() switch
    {
        [] => Task.CompletedTask,
        [var one] => RunTestsAsync([one], TestKinds.Standard, $"Проверяю «{one.Name}»…"),
        var many => RunTestsAsync(many, TestKinds.Standard, $"Проверяю выделенные серверы ({many.Count})…"),
    };

    [RelayCommand]
    private void Clone()
    {
        if (Selected is not { } item)
            return;
        var copy = _engine.CloneProfile(item.Id);
        Refresh();
        Selected = Items.FirstOrDefault(i => i.Id == copy.Id) ?? Selected;
        Message = $"Создана копия «{copy.Name}» в «Мои». Её можно менять, не трогая исходный сервер.";
    }

    [RelayCommand]
    private void RemoveDuplicates()
    {
        var ids = _engine.FindDuplicates();
        if (ids.Count == 0)
        {
            Message = "Дубликатов нет: все серверы отличаются хотя бы одним параметром, кроме названия.";
            return;
        }

        RemoveMany(ids, $"Удалено дубликатов: {ids.Count} (совпадало всё, кроме названия; остался выбранный или верхний). {RemovalNote(ids)}");
    }

    [RelayCommand]
    private void RemoveDead()
    {
        var ids = _engine.FindDead();
        if (ids.Count == 0)
        {
            Message = "Нерабочих по последней проверке нет. Сначала нажмите «Тест всех» — удаляются только те, что на ней не ответили или «замёрзли». Выбранный сервер не трогается.";
            return;
        }

        RemoveMany(ids, $"Удалено нерабочих по последней проверке: {ids.Count} (выбранный сервер остался). {RemovalNote(ids)}");
    }

    // ---------------- Поделиться: ссылка, QR, полный конфиг ----------------

    /// <summary>Ссылка выбранного сервера — с ключом, поэтому только по кнопке и с очисткой буфера через 60 с.</summary>
    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        if (Selected is not { } item)
            return;
        Message = await Services.UiHost.CopySecretAsync(ShareLink.Build(item.Profile))
            ? $"Ссылка «{item.Name}» скопирована. В ней ключ доступа — через 60 секунд Тропа уберёт её из буфера обмена."
            : "Буфер обмена недоступен.";
    }

    [ObservableProperty]
    public partial Avalonia.Media.Imaging.Bitmap? QrImage { get; set; }

    [ObservableProperty]
    public partial string QrTitle { get; set; } = "";

    [ObservableProperty]
    public partial string? QrNote { get; set; }

    [ObservableProperty]
    public partial bool IsQrOpen { get; set; }

    private ServerItemViewModel? _qrItem;

    [RelayCommand]
    private void ShowQr()
    {
        if (Selected is not { } item)
            return;
        _qrItem = item;
        QrTitle = item.Name;
        QrNote = null;
        QrImage = Services.QrCodes.Render(ShareLink.Build(item.Profile));
        IsQrOpen = true;
    }

    [RelayCommand]
    private void CloseQr()
    {
        IsQrOpen = false;
        QrImage = null;
        _qrItem = null;
    }

    [RelayCommand]
    private async Task CopyQrLinkAsync()
    {
        if (_qrItem is { } item && await Services.UiHost.CopySecretAsync(ShareLink.Build(item.Profile)))
            QrNote = "Ссылка скопирована — через 60 секунд Тропа уберёт её из буфера обмена.";
    }

    /// <summary>Полный конфиг ядра с ключами (ADR-054): сначала предупреждение, потом файл через диалог.</summary>
    [RelayCommand]
    private void ExportConfig()
    {
        if (Selected is not { } item)
            return;
        Ask($"Выгрузить полный конфиг «{item.Name}» в файл? В нём будут ключ доступа к серверу и пароль локального прокси — "
            + "кто получит файл, сможет пользоваться сервером. Не пересылайте его и не выкладывайте.",
            "Понимаю, выгрузить", () => _ = ExportConfigAsync(item));
    }

    private async Task ExportConfigAsync(ServerItemViewModel item)
    {
        try
        {
            var (config, core) = _engine.ExportFullConfig(item.Id);
            var safeName = string.Concat(item.Name.Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
            var file = await Services.UiHost.SaveTextAsync($"Полный конфиг {core} с ключами", $"tropa-{core.ToLowerInvariant()}-{safeName}.json", config);
            Message = file is null ? null : $"Сохранено: {file} (конфиг {core}, с ключами — храните как пароль).";
        }
        catch (Exception ex) when (ex is UnsupportedProfileException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Message = _engine.Scrubber.Scrub(ex.Message);
        }
    }

    [RelayCommand]
    private Task StabilitySelectedAsync() => Selected is { } s
        ? RunTestsAsync([s], TestKinds.Stability, $"Проверяю стабильность «{s.Name}»: 30 запросов раз в секунду…")
        : Task.CompletedTask;

    private async Task RunTestsAsync(List<ServerItemViewModel> items, TestKinds kinds, string startMessage)
    {
        if (items.Count == 0)
            return;
        IsBusy = true;
        Message = startMessage;
        foreach (var i in items)
            i.Testing = true;
        // Строки обновляются по мере готовности, не дожидаясь конца проверки всех серверов.
        var progress = new Progress<TestProgress>(p =>
        {
            var item = items.FirstOrDefault(i => i.Id == p.ProfileId);
            if (item is null)
                return;
            item.Testing = false;
            if (kinds != TestKinds.Stability)
                item.Apply(p.Result);
        });
        try
        {
            var results = await _engine.TestAsync(items.Select(i => i.Profile).ToList(), kinds, progress);
            var ok = results.Values.Count(r => ServerHealth.Classify(r) is HealthStatus.Working or HealthStatus.Slow);
            var frozen = results.Values.Count(r => r.Frozen);
            Message = kinds == TestKinds.Stability
                ? null
                : $"Работают {ok} из {results.Count}." + (frozen > 0 ? $" «Замёрзли» — пинг есть, а данные не идут: {frozen}." : "");
            if (ok < results.Count && _engine.ForeignVpnWarning() is { } foreign)
                Message = (Message is null ? "" : Message + " ") + foreign;
        }
        catch (Exception ex) when (ex is Infrastructure.Cores.CoreStartException or Infrastructure.Cores.IntegrityException or InvalidOperationException)
        {
            Message = _engine.Scrubber.Scrub(ex.Message);
        }
        finally
        {
            foreach (var i in items)
                i.Testing = false;
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);

    /// <summary>«Ядро: Xray (выбрано)», «Ядро: sing-box (найдено проверкой)», «Ядро: определится при проверке».</summary>
    private static string CoreText(StoredProfile sp)
    {
        if (sp.Profile.Protocol == Core.Model.Protocol.Hysteria2)
            return "Ядро: sing-box (Hysteria2 есть только в нём)";
        if (sp.Profile.Transport.Type == TransportType.Xhttp)
            return "Ядро: Xray (XHTTP есть только в нём)";
        if (sp.Core != CoreChoice.Auto)
            return "Ядро: " + CoreTexts.Name(sp.Core) + " (выбрано вами)";
        return sp.DetectedCore is { } d ? "Ядро: " + CoreTexts.Name(d) + " (найдено проверкой)" : "Ядро: определится при проверке (сначала Xray)";
    }
}
