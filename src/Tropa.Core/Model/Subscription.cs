using Tropa.Core.Parsing;
using Tropa.Core.Security;
using Tropa.Core.Testing;

namespace Tropa.Core.Model;

/// <summary>Подписка провайдера (docs/04-domain-model.md, §2).</summary>
public sealed record Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required Secret Url { get; init; }
    public int UpdateHours { get; init; } = 12;
    public UserAgentMode UserAgent { get; init; } = UserAgentMode.Tropa;
    public string? CustomUserAgent { get; init; }
    public bool SendHwid { get; init; }
    public bool FetchViaProxy { get; init; } = true;
    public SubscriptionInfo? Info { get; init; }
    public DateTimeOffset? LastUpdated { get; init; }
    public string? LastError { get; init; }

    /// <summary>Что прислал администратор подписки в формате Тропы (ADR-048); null — обычная подписка.</summary>
    public AdminChannel? Admin { get; init; }

    /// <summary>
    /// Пользователь доверил администратору подписки выбор сервера и правила. По умолчанию нет:
    /// чужой документ без согласия не меняет маршрут.
    /// </summary>
    public bool TrustAdmin { get; init; }

    /// <summary>
    /// Последний выбор сервера администратора, который уже применён. Пока администратор не выберет
    /// другой, ручной выбор пользователя не перебивается.
    /// </summary>
    public string? AdminActiveApplied { get; init; }

    /// <summary>
    /// Как часто обновлять: частота администратора, если она задана, иначе общая настройка.
    /// Интервал провайдера (profile-update-interval) только уточняет общую: реже раза в сутки не ждём.
    /// </summary>
    public TimeSpan? RefreshInterval(SubscriptionDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        if (Admin?.RefreshMinutes is { } admin)
            return TimeSpan.FromMinutes(admin);
        return defaults.Interval;
    }
}

/// <summary>Выбор сервера, правила и частота обновления от администратора подписки.</summary>
public sealed record AdminChannel
{
    public string? Title { get; init; }
    public int? RefreshMinutes { get; init; }

    /// <summary>Имя сервера, который администратор выбрал активным.</summary>
    public string? ActiveServer { get; init; }

    public string? Revision { get; init; }

    public IReadOnlyList<AdminRule> Rules { get => _rules; init => _rules = value ?? []; }

    private readonly IReadOnlyList<AdminRule> _rules = [];
}

/// <summary>Действие правила администратора: «через конкретный сервер» — по имени сервера в этой подписке.</summary>
public sealed record AdminAction(RuleActionKind Kind, string? ServerName);

public sealed record AdminRule(string? Label, RuleMatch Match, AdminAction Tcp, AdminAction Udp);

/// <summary>Профиль внутри хранилища: сам профиль плюс служебные пометки.</summary>
public sealed record StoredProfile
{
    public required Profile Profile { get; init; }

    /// <summary>Провайдер убрал сервер из подписки; удаляется через 7 дней (docs/04-domain-model.md, §1).</summary>
    public DateTimeOffset? RemovedByProvider { get; init; }

    public bool Favorite { get; init; }

    /// <summary>
    /// Пользователь удалил сервер из подписки. Совсем стереть его нельзя — при обновлении провайдер
    /// пришлёт его снова, — поэтому он скрыт: не показывается и не выбирается.
    /// </summary>
    public bool Hidden { get; init; }

    public int Order { get; init; }

    /// <summary>Последняя проверка: показывается в списке и учитывается фоновой проверкой.</summary>
    public ServerTestResult? LastTest { get; init; }

    /// <summary>Ядро для этого сервера, выбранное пользователем. Auto — Тропа определяет сама (сначала Xray).</summary>
    public CoreChoice Core { get; init; } = CoreChoice.Auto;

    /// <summary>Ядро, на котором сервер заработал при последней автоматической проверке; null — не определено.</summary>
    public CoreChoice? DetectedCore { get; init; }

    /// <summary>Ядро, которое сейчас будет использовано: выбор пользователя, иначе найденное проверкой.</summary>
    public CoreChoice EffectiveCore => Core != CoreChoice.Auto ? Core : DetectedCore ?? CoreChoice.Auto;
}
