namespace Tropa.Core.Games;

/// <summary>
/// Состояние Teredo из <c>netsh interface teredo show state</c> (ADR-065). Подписи в выводе переведены
/// на язык Windows, значения — нет, поэтому разбор идёт по порядку строк «подпись : значение» и по значениям.
/// </summary>
/// <param name="Type">disabled, client, enterpriseclient, default, server, natawareclient.</param>
/// <param name="Server">Имя сервера Teredo.</param>
/// <param name="State">offline, qualified, dormant, probe (primary server) и т. п.</param>
/// <param name="Nat">Тип NAT по Teredo (cone, restricted, symmetric…) или null, если Teredo не поднят.</param>
/// <param name="Error">Текст ошибки Windows (на языке системы) или null.</param>
public sealed record TeredoStatus(string Type, string Server, string State, string? Nat, string? Error)
{
    /// <summary>Работает: квалифицирован или спит до первого обращения (dormant).</summary>
    public bool Working => State.StartsWith("qualified", StringComparison.OrdinalIgnoreCase)
        || State.StartsWith("dormant", StringComparison.OrdinalIgnoreCase);

    public bool Disabled => Type.Equals("disabled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Проверка ещё идёт (probe) — ждать, а не чинить.</summary>
    public bool Probing => State.StartsWith("probe", StringComparison.OrdinalIgnoreCase);

    /// <summary>Симметричный NAT: подключение к другим игрокам может не проходить — совет про UPnP.</summary>
    public bool StrictNat => Nat?.StartsWith("symmetric", StringComparison.OrdinalIgnoreCase) == true;

    private static readonly string[] Types = ["disabled", "client", "enterpriseclient", "default", "server", "natawareclient"];

    private static readonly string[] NatKinds = ["cone", "restricted", "symmetric", "unknown", "none"];

    /// <summary>Разбор вывода netsh. null — вывод не похож на состояние Teredo.</summary>
    public static TeredoStatus? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;
        var values = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var colon = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colon < 0)
            {
                // Строка ошибки бывает без пробела перед двоеточием: «Ошибка: основной сервер…».
                colon = line.IndexOf(": ", StringComparison.Ordinal);
                if (colon < 0 || values.Count < 5)
                    continue;
                values.Add(line[(colon + 2)..].Trim());
                continue;
            }

            values.Add(line[(colon + 3)..].Trim());
        }

        // Порядок netsh: тип, сервер, интервал, порт клиента, состояние, затем ошибка или подробности.
        if (values.Count < 5 || !Types.Contains(values[0], StringComparer.OrdinalIgnoreCase))
            return null;

        string? nat = null;
        string? error = null;
        foreach (var v in values.Skip(5))
        {
            if (nat is null && NatKinds.Any(k => v.StartsWith(k, StringComparison.OrdinalIgnoreCase)))
                nat = v;
        }

        // Ошибка — шестое значение у offline-состояния, если это не служебное «нет»/«none».
        if (values.Count > 5 && values[4].StartsWith("offline", StringComparison.OrdinalIgnoreCase)
            && !values[5].Equals("none", StringComparison.OrdinalIgnoreCase) && !values[5].Equals("нет", StringComparison.OrdinalIgnoreCase))
        {
            error = values[5];
        }

        return new TeredoStatus(values[0], values[1].TrimEnd('.'), values[4], nat, error);
    }
}
