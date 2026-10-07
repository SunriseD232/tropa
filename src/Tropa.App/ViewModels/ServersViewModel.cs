using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.App.Services;
using Tropa.Core.Compatibility;
using Tropa.Core.Model;
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

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

internal sealed partial class ServerItemViewModel(Profile profile) : ObservableObject
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    public Profile Profile { get; } = profile;
    public Guid Id => Profile.Id;
    public string Name => Format.NameWithoutFlag(Profile.Name);
    public string Country => Format.CountryCode(Profile.Name);
    public string Protocol => Format.Protocol(Profile);
    public string Transport => Format.Transport(Profile);

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
    public partial string Explanation { get; set; } = "Сервер ещё не проверялся.";

    [ObservableProperty]
    public partial string Checked { get; set; } = "";

    [ObservableProperty]
    public partial bool Testing { get; set; }

    public void Apply(ServerTestResult? r)
    {
        if (r is null)
            return;
        var status = ServerHealth.Classify(r);
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
        Explanation = ServerHealth.Explain(r);
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
        Tabs.Clear();
        Tabs.Add(new GroupTab($"Все · {s.Profiles.Count}", null, false));
        foreach (var sub in s.Subscriptions)
            Tabs.Add(new GroupTab($"{sub.Name} · {s.Profiles.Count(p => p.Profile.SubscriptionId == sub.Id)}", sub.Id, false));
        var manual = s.Profiles.Count(p => p.Profile.SubscriptionId is null);
        if (manual > 0)
            Tabs.Add(new GroupTab($"Мои · {manual}", null, true));
        var restore = Tabs.FirstOrDefault(t => selectedTab is not null && t.SubscriptionId == selectedTab.SubscriptionId && t.Manual == selectedTab.Manual) ?? Tabs[0];
        restore.IsSelected = true;

        RebuildItems();
        var updated = s.Subscriptions.Where(x => x.LastUpdated is not null).Select(x => x.LastUpdated!.Value).DefaultIfEmpty().Max();
        Summary = s.Profiles.Count == 0
            ? "Серверов пока нет"
            : Format.Plural(s.Profiles.Count, "сервер", "сервера", "серверов")
              + (s.Subscriptions.Count > 0 ? " · " + Format.Plural(s.Subscriptions.Count, "подписка", "подписки", "подписок") : "")
              + (updated == default ? "" : $" · обновлено {updated.LocalDateTime:dd.MM HH:mm}");
        IsEmpty = s.Profiles.Count == 0;
    }

    private void RebuildItems()
    {
        var s = _engine.State;
        var tab = CurrentTab;
        var selectedId = Selected?.Id;
        Items.Clear();
        foreach (var sp in s.Profiles.OrderBy(p => p.Order))
        {
            if (tab is { All: false } && (tab.Manual ? sp.Profile.SubscriptionId is not null : sp.Profile.SubscriptionId != tab.SubscriptionId))
                continue;
            var item = new ServerItemViewModel(sp.Profile) { IsActive = sp.Profile.Id == s.ActiveProfileId, Removed = sp.RemovedByProvider is not null };
            if (ProfileCompat.Issues(sp.Profile) is { Count: > 0 } issues)
                (item.Status, item.StatusKind, item.Explanation) = ("Несовместимые параметры", "bad", issues[0]);
            else if (sp.Profile.Security.AllowInsecure)
                (item.Status, item.StatusKind, item.Explanation) = ("Без проверки сертификата", "bad", "У сервера отключена проверка сертификата: трафик может прочитать посредник. В авто-выбор такой сервер не попадает.");
            else if (item.Removed)
                (item.Status, item.StatusKind) = ("Удалён провайдером", "warn");
            else
                item.Apply(sp.LastTest);

            if (item.IsActive && string.IsNullOrEmpty(item.Status))
                (item.Status, item.StatusKind) = ("Выбран", "ok");
            Items.Add(item);
        }

        Selected = Items.FirstOrDefault(i => i.Id == selectedId) ?? Items.FirstOrDefault(i => i.IsActive) ?? Items.FirstOrDefault();
    }

    [RelayCommand]
    private void SelectTab(GroupTab tab)
    {
        foreach (var t in Tabs)
            t.IsSelected = t == tab;
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

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is { Profile.SubscriptionId: null } item)
            _engine.RemoveProfile(item.Id);
        else
            Message = "Серверы из подписки удаляются вместе с подпиской: при обновлении они вернутся.";
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
    private Task TestSelectedAsync() => Selected is { } s
        ? RunTestsAsync([s], TestKinds.Standard, $"Проверяю «{s.Name}»…")
        : Task.CompletedTask;

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
                : $"Работают {ok} из {results.Count}." + (frozen > 0 ? $" «Замёрзли» — пинг есть, а данные не идут: {frozen}. Авто-выбор их не возьмёт." : "");
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
}
