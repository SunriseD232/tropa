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
}

/// <summary>Профиль внутри хранилища: сам профиль плюс служебные пометки.</summary>
public sealed record StoredProfile
{
    public required Profile Profile { get; init; }

    /// <summary>Провайдер убрал сервер из подписки; удаляется через 7 дней (docs/04-domain-model.md, §1).</summary>
    public DateTimeOffset? RemovedByProvider { get; init; }

    public bool Favorite { get; init; }

    public int Order { get; init; }

    /// <summary>Последняя проверка: показывается в списке и учитывается авто-выбором.</summary>
    public ServerTestResult? LastTest { get; init; }

    /// <summary>Ядро для этого сервера, выбранное пользователем. Auto — Тропа определяет сама (сначала Xray).</summary>
    public CoreChoice Core { get; init; } = CoreChoice.Auto;

    /// <summary>Ядро, на котором сервер заработал при последней автоматической проверке; null — не определено.</summary>
    public CoreChoice? DetectedCore { get; init; }

    /// <summary>Ядро, которое сейчас будет использовано: выбор пользователя, иначе найденное проверкой.</summary>
    public CoreChoice EffectiveCore => Core != CoreChoice.Auto ? Core : DetectedCore ?? CoreChoice.Auto;
}
