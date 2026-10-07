using System.Diagnostics;

namespace Tropa.Core.Security;

/// <summary>
/// Секрет (UUID, пароль, URL подписки). ToString() никогда не раскрывает значение,
/// поэтому случайная запись в лог или интерполяция строки не приведут к утечке.
/// Значение достаётся только явным вызовом <see cref="Reveal"/>.
/// </summary>
[DebuggerDisplay("Secret(***)")]
public sealed class Secret : IEquatable<Secret>
{
    private readonly string _value;

    public Secret(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        _value = value;
    }

    public string Reveal() => _value;

    public override string ToString() => "***";

    public bool Equals(Secret? other) => other is not null && string.Equals(_value, other._value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Secret other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(_value);
}
