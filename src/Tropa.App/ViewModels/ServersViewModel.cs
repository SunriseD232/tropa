using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropa.App.Services;
using Tropa.Core.Compatibility;
using Tropa.Core.Model;
using Tropa.Infrastructure;

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
    public partial string Status { get; set; } = "";

    /// <summary>ok / warn / bad / "" — для цвета плашки статуса.</summary>
    [ObservableProperty]
    public partial string StatusKind { get; set; } = "";

    [ObservableProperty]
    public partial bool Removed { get; set; }
}

/// <summary>Экран «Серверы»: подписки, список, тест реальной задержки, выбор сервера.</summary>
internal sealed partial class ServersViewModel : ObservableObject
{
    private readonly TropaEngine _engine;
    private readonly InfoViewModel _info;
    private readonly Action _openImport;
    private readonly Dictionary<Guid, (string Delay, string Status, string Kind)> _results = [];

    public ServersViewModel(TropaEngine engine, InfoViewModel info, Action openImport)
    {
        _engine = engine;
        _info = info;
        _openImport = openImport;
        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    public ObservableCollection<GroupTab> Tabs { get; } = [];

    public ObservableCollection<ServerItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial ServerItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

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
            if (_results.TryGetValue(sp.Profile.Id, out var r))
            {
                item.Delay = r.Delay;
                item.Status = r.Status;
                item.StatusKind = r.Kind;
            }
            else if (ProfileCompat.Issues(sp.Profile) is { Count: > 0 })
            {
                (item.Status, item.StatusKind) = ("Несовместимые параметры", "bad");
            }
            else if (sp.Profile.Security.AllowInsecure)
            {
                (item.Status, item.StatusKind) = ("Без проверки сертификата", "bad");
            }
            else if (item.Removed)
            {
                (item.Status, item.StatusKind) = ("Удалён провайдером", "warn");
            }

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
        }
    }

    [RelayCommand]
    private async Task TestAllAsync()
    {
        IsBusy = true;
        Message = "Проверяю серверы через временный экземпляр ядра — текущее подключение не прерывается…";
        try
        {
            var profiles = Items.Select(i => i.Profile).ToList();
            var results = await _engine.TestDelayAsync(profiles);
            foreach (var r in results)
            {
                _results[r.ProfileId] = r.Milliseconds is { } ms
                    ? ($"{ms} мс", ms > 600 ? "Медленно" : "Работает", ms > 600 ? "warn" : "ok")
                    : ("—", r.Error ?? "Нет ответа", "bad");
            }

            RebuildItems();
            var ok = results.Count(r => r.Milliseconds is not null);
            Message = $"Отвечают {ok} из {results.Count}. Это проверка задержки; тест скорости, который ловит «заморозку», появится в следующей версии.";
        }
        catch (Exception ex) when (ex is Infrastructure.Cores.CoreStartException or Infrastructure.Cores.IntegrityException or InvalidOperationException)
        {
            Message = _engine.Scrubber.Scrub(ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void ShowInfo(string key) => _info.Show(key);
}
