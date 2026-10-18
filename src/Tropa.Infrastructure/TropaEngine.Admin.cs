using Tropa.Core.Compatibility;
using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Infrastructure.Net;
using Tropa.Infrastructure.Storage;

namespace Tropa.Infrastructure;

/// <summary>
/// Подписка от администратора (ADR-048). У друга: выбор сервера и правила из документа администратора
/// (только с согласия — <see cref="Subscription.TrustAdmin"/>). У администратора: публикация «Моих»
/// серверов, выбранного сервера и правил по своему адресу — сама, при каждом изменении.
/// </summary>
public sealed partial class TropaEngine
{
    private readonly Lock _publishLock = new();
    private CancellationTokenSource? _publishDebounce;
    private string? _lastPublishedFingerprint;

    /// <summary>Чем закончилась последняя публикация для друзей; null — ещё не публиковали.</summary>
    public string? AdminPublishStatus { get; private set; }

    public event EventHandler? AdminPublishStatusChanged;

    // ---------------- Администратор: адрес и ключ ----------------

    public bool HasAdminUrl => _secrets.Get(StateStore.AdminUrlKey) is not null;

    /// <summary>Адрес для показа: хост и начало пути, остальное скрыто (секрет, как адрес подписки).</summary>
    public string AdminUrlMasked => _secrets.Get(StateStore.AdminUrlKey) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u)
        ? $"https://{u.Host}/••••"
        : "";

    /// <summary>Полный адрес — только по явному действию («Скопировать ссылку для друзей»).</summary>
    public string? RevealAdminUrl() => _secrets.Get(StateStore.AdminUrlKey);

    /// <summary>Ключ публикации — только по явному действию («Скопировать ключ»).</summary>
    public string? RevealAdminKey() => _secrets.Get(StateStore.AdminKeyKey);

    /// <summary>Сохраняет адрес публикации. Маска из поля (с «••••») — значит, адрес не меняли.</summary>
    public string? SetAdminUrl(string url)
    {
        var value = (url ?? "").Trim();
        if (value.Contains('•', StringComparison.Ordinal))
            return null;
        if (value.Length == 0)
        {
            _secrets.Remove(StateStore.AdminUrlKey);
            Save();
            return null;
        }

        if (AdminPublisher.ValidateUrl(value) is { } error)
            return error;
        _secrets.Set(StateStore.AdminUrlKey, value);
        if (_secrets.Get(StateStore.AdminKeyKey) is null)
            _secrets.Set(StateStore.AdminKeyKey, Storage.SecretStore.NewRandomToken());
        Save();
        _lastPublishedFingerprint = null;
        SchedulePublish();
        return null;
    }

    /// <summary>Новый ключ: старый перестаёт работать, на сервере публикации его нужно заменить.</summary>
    public void RegenerateAdminKey()
    {
        _secrets.Set(StateStore.AdminKeyKey, Storage.SecretStore.NewRandomToken());
        Save();
    }

    // ---------------- Администратор: публикация ----------------

    /// <summary>Что получат друзья: «Мои» серверы, выбранный из них и (если включено) правила.</summary>
    private string BuildFriendsDocument(DateTimeOffset at)
    {
        var admin = State.Settings.Admin;
        var servers = State.Profiles.Where(p => p.Profile.SubscriptionId is null && !p.Hidden).OrderBy(p => p.Order).Select(p => p.Profile).ToList();
        var active = State.ActiveProfile is { SubscriptionId: null } a ? a : null;
        var rules = admin.PublishRules ? State.Settings.Routing.Rules : [];
        return AdminDocument.Build(servers, active, rules, string.IsNullOrWhiteSpace(admin.Title) ? null : admin.Title.Trim(),
            admin.RefreshMinutes <= 0 ? AdminDocument.MinRefreshMinutes : admin.RefreshMinutes, at);
    }

    /// <summary>Сколько «Моих» серверов уйдёт друзьям.</summary>
    public int FriendsServerCount => State.Profiles.Count(p => p.Profile.SubscriptionId is null && !p.Hidden);

    /// <summary>Опубликовать сейчас. null — успешно, иначе понятная причина.</summary>
    public async Task<string?> PublishForFriendsAsync(CancellationToken ct = default)
    {
        if (_secrets.Get(StateStore.AdminUrlKey) is not { } url || _secrets.Get(StateStore.AdminKeyKey) is not { } key)
            return SetPublishStatus("Укажите адрес публикации.");
        if (FriendsServerCount == 0)
            return SetPublishStatus("Нет серверов для друзей: добавьте их в «Мои» на экране «Серверы».");
        var json = BuildFriendsDocument(_now());
        try
        {
            await AdminPublisher.PublishAsync(url, key, json, LocalProxyIfConnected(), ct).ConfigureAwait(false);
            _lastPublishedFingerprint = BuildFriendsDocument(DateTimeOffset.UnixEpoch);
            return SetPublishStatus(null);
        }
        catch (AdminPublishException ex)
        {
            Log?.Invoke(this, "Публикация для друзей не удалась: " + Scrubber.Scrub(ex.Message));
            return SetPublishStatus(Scrubber.Scrub(ex.Message));
        }
    }

    private string? SetPublishStatus(string? error)
    {
        AdminPublishStatus = error is null
            ? $"Опубликовано {_now():HH:mm}: {Format(FriendsServerCount)}" + (State.Settings.Admin.PublishRules ? $", правил: {State.Settings.Routing.Rules.Count(r => r.Enabled)}" : "") + "."
            : "Не опубликовано: " + error;
        AdminPublishStatusChanged?.Invoke(this, EventArgs.Empty);
        return error;

        static string Format(int n) => n % 10 == 1 && n % 100 != 11 ? $"{n} сервер" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? $"{n} сервера" : $"{n} серверов";
    }

    /// <summary>
    /// После любого изменения состояния: если администратор включил публикацию и то, что получат друзья,
    /// изменилось, — опубликовать через 3 секунды (несколько правок подряд уходят одной публикацией).
    /// </summary>
    private void SchedulePublish()
    {
        if (!State.Settings.Admin.Enabled || !HasAdminUrl || FriendsServerCount == 0)
            return;
        if (BuildFriendsDocument(DateTimeOffset.UnixEpoch) == _lastPublishedFingerprint)
            return;
        CancellationTokenSource cts;
        lock (_publishLock)
        {
            _publishDebounce?.Cancel();
            _publishDebounce?.Dispose();
            _publishDebounce = cts = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
                await PublishForFriendsAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private LocalProxy? LocalProxyIfConnected()
    {
        if (Status.State != ConnectionState.Connected)
            return null;
        var pass = CompatRules.Evaluate(State.Settings).Effective.General.LocalPass;
        return new LocalProxy(_mixedPort, pass ? Auth.Username : null, pass ? Auth.Password : null);
    }

    // ---------------- Друг: выбор сервера и правила администратора ----------------

    /// <summary>Пользователь доверил (или больше не доверяет) администратору подписки выбор сервера и правила.</summary>
    public async Task SetTrustAdminAsync(Guid subscriptionId, bool trust, CancellationToken ct = default)
    {
        Update(s => s with
        {
            Subscriptions = s.Subscriptions.Select(x => x.Id == subscriptionId
                ? x with { TrustAdmin = trust, AdminActiveApplied = trust ? x.AdminActiveApplied : null }
                : x).ToList(),
        });
        var activeChanged = trust && ApplyAdminActive(subscriptionId);
        if (Status.State == ConnectionState.Connected)
            await ReconnectAsync(ct).ConfigureAwait(false);
        else if (activeChanged)
            StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Выбор сервера администратора: применяется, если пользователь доверяет и администратор выбрал
    /// сервер, который ещё не применяли. Ручной выбор пользователя после этого держится до следующей смены.
    /// </summary>
    private bool ApplyAdminActive(Guid subscriptionId)
    {
        var sub = State.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId);
        if (sub is not { TrustAdmin: true, Admin.ActiveServer: { } name } || name == sub.AdminActiveApplied)
            return false;
        var target = State.Profiles.FirstOrDefault(p => p.Profile.SubscriptionId == subscriptionId && !p.Hidden && p.RemovedByProvider is null
            && string.Equals(p.Profile.Name, name, StringComparison.OrdinalIgnoreCase));
        if (target is null)
            return false;
        var changed = State.ActiveProfileId != target.Profile.Id;
        Update(s => s with
        {
            ActiveProfileId = target.Profile.Id,
            Subscriptions = s.Subscriptions.Select(x => x.Id == subscriptionId ? x with { AdminActiveApplied = name } : x).ToList(),
        });
        if (changed)
            Log?.Invoke(this, $"Администратор подписки «{sub.Name}» выбрал сервер «{target.Profile.Name}».");
        return changed;
    }

    /// <summary>Правила администраторов, которым пользователь доверяет: идут после его собственных.</summary>
    private List<Rule> AdminRules()
    {
        var result = new List<Rule>();
        foreach (var sub in State.Subscriptions.Where(s => s.TrustAdmin && s.Admin is not null))
        {
            var byName = State.Profiles.Where(p => p.Profile.SubscriptionId == sub.Id && !p.Hidden && p.RemovedByProvider is null)
                .GroupBy(p => p.Profile.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Profile.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var r in sub.Admin!.Rules)
            {
                result.Add(new Rule
                {
                    Label = "Администратор «" + sub.Name + "»" + (r.Label is { Length: > 0 } l ? ": " + l : ""),
                    Match = r.Match,
                    Tcp = Resolve(r.Tcp),
                    Udp = Resolve(r.Udp),
                });
            }

            RuleAction Resolve(AdminAction a) => a.Kind switch
            {
                RuleActionKind.Direct => RuleAction.Direct,
                RuleActionKind.Block => RuleAction.Block,
                RuleActionKind.Server when a.ServerName is { } n && byName.TryGetValue(n, out var id) => new RuleAction(RuleActionKind.Server, id),
                _ => RuleAction.Proxy,
            };
        }

        return result;
    }

    /// <summary>Правила администраторов для показа на экране «Правила» (только чтение).</summary>
    public IReadOnlyList<(string Subscription, AdminRule Rule, bool Trusted)> AdminRuleList() =>
        State.Subscriptions.Where(s => s.Admin is not null)
            .SelectMany(s => s.Admin!.Rules.Select(r => (s.Name, r, s.TrustAdmin)))
            .ToList();
}
