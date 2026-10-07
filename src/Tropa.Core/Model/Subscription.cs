using Tropa.Core.Parsing;
using Tropa.Core.Security;

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
}
