using Tropa.Core.Model;

namespace Tropa.Core.Parsing;

/// <summary>Результат разбора ссылки: профиль или понятная причина отказа, плюс предупреждения.</summary>
public sealed record ParseResult
{
    public Profile? Profile { get; private init; }

    public string? Error { get; private init; }

    public IReadOnlyList<string> Warnings { get; private init; } = [];

    public bool Success => Profile is not null;

    public static ParseResult Ok(Profile profile, IReadOnlyList<string> warnings) =>
        new() { Profile = profile, Warnings = warnings };

    public static ParseResult Fail(string error) => new() { Error = error };
}

/// <summary>Ошибка разбора с понятным пользователю текстом.</summary>
internal sealed class LinkFormatException(string message) : Exception(message);
