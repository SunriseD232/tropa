using System.Text.Json.Serialization;

namespace Tropa.Core.Model;

/// <summary>Откуда игра (ADR-065). Xbox — игры приложения Xbox / Game Pass, им нужен Teredo.</summary>
public enum GameLauncher { Unknown, Xbox, Steam, Epic, Gog, Ea, Ubisoft, Riot }

/// <summary>
/// Режим игры (ADR-065). Первое значение — «как было»: автонастройка включается только кнопкой
/// (решение владельца), и в старом settings.json отсутствующее поле читается как Original.
/// </summary>
public enum GameMode { Original, Auto }

/// <summary>Как игра попала в список.</summary>
public enum GameSource { Detected, Manual }

/// <summary>
/// Игра на вкладке «Игры» (ADR-065). Поля списков и действий переживают отсутствие в старом
/// settings.json: source-generated System.Text.Json передаёт туда null, инициализатор не применяется.
/// </summary>
public sealed record GameProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Имя exe (без пути), по нему строится правило: «ForzaHorizon6.exe».</summary>
    public string Exe { get => _exe; init => _exe = value ?? ""; }

    private readonly string _exe = "";

    /// <summary>Название для списка: из описания exe или имени папки.</summary>
    public string Name { get => _name; init => _name = value ?? ""; }

    private readonly string _name = "";

    /// <summary>Где exe лежал, когда игру видели в последний раз (только для показа).</summary>
    public string? Path { get; init; }

    public GameLauncher Launcher { get; init; }

    public GameMode Mode { get; init; }

    public GameSource Source { get; init; }

    /// <summary>TCP игры при автонастройке. По умолчанию — напрямую (пинг и сервера игры видят ваш адрес).</summary>
    public RuleAction Tcp { get => _tcp; init => _tcp = value ?? RuleAction.Direct; }

    private readonly RuleAction _tcp = RuleAction.Direct;

    public RuleAction Udp { get => _udp; init => _udp = value ?? RuleAction.Direct; }

    private readonly RuleAction _udp = RuleAction.Direct;

    /// <summary>
    /// Домены (с поддоменами), которым при автонастройке отдаются настоящие адреса, а не FakeIP.
    /// Пополняются сами, пока игра работает (если не выключено <see cref="NoLearning"/>), и вручную.
    /// </summary>
    public IReadOnlyList<string> Domains { get => _domains; init => _domains = value ?? []; }

    private readonly IReadOnlyList<string> _domains = [];

    /// <summary>Игре нужен Teredo (мультиплеер игр Xbox). Ставится при находке игры Xbox.</summary>
    public bool NeedsTeredo { get; init; }

    /// <summary>Не запоминать домены игры. Флаг «наоборот»: по умолчанию запоминать.</summary>
    public bool NoLearning { get; init; }

    /// <summary>Игра обращалась к условному адресу FakeIP, пока была «как было», — подсказка включить автонастройку.</summary>
    public bool FakeIpSeen { get; init; }

    public DateTimeOffset? LastSeen { get; init; }

    [JsonIgnore]
    public string Title => Name.Length > 0 ? Name : Exe;

    /// <summary>Сколько доменов запоминается у одной игры.</summary>
    public const int MaxDomains = 300;
}

/// <summary>Вкладка «Игры» (ADR-065). null из старого settings.json — значения по умолчанию.</summary>
public sealed record GamesSettings
{
    public IReadOnlyList<GameProfile> List { get => _list; init => _list = value ?? []; }

    private readonly IReadOnlyList<GameProfile> _list = [];

    /// <summary>
    /// Программы, которые пользователь убрал из списка (например, Wallpaper Engine из Steam — по папке
    /// его не отличить от игры). Поиск их больше не добавляет; добавить вручную можно.
    /// </summary>
    public IReadOnlyList<string> Ignored { get => _ignored; init => _ignored = value ?? []; }

    private readonly IReadOnlyList<string> _ignored = [];

    /// <summary>Тропа не трогает Teredo сама. Флаг «наоборот»: по умолчанию следит (решение владельца 2).</summary>
    public bool TeredoHandsOff { get; init; }

    [JsonIgnore]
    public IEnumerable<GameProfile> AutoGames => List.Where(g => g.Mode == GameMode.Auto && g.Exe.Length > 0);

    /// <summary>Нужен ли Teredo хотя бы одной игре на автонастройке.</summary>
    [JsonIgnore]
    public bool AnyAutoNeedsTeredo => AutoGames.Any(g => g.NeedsTeredo);
}

/// <summary>Готовые наборы для игр (ADR-065).</summary>
public static class GameDefaults
{
    /// <summary>
    /// Домены Xbox Live, PlayFab (подбор игроков, регион) и серверов Teredo. Игре Xbox и службе Teredo
    /// нужны настоящие адреса: проверка региона и квалификация Teredo с FakeIP не проходят.
    /// </summary>
    public static IReadOnlyList<string> XboxDomains { get; } =
        ["ipv6.microsoft.com", "xboxlive.com", "xboxservices.com", "playfabapi.com", "playfab.com"];

    /// <summary>Сервер Teredo, с которым у владельца квалификация прошла (у type=default Windows оставляет disabled).</summary>
    public const string TeredoServer = "win1910.ipv6.microsoft.com";

    /// <summary>Порт серверов и ретрансляторов Teredo (UDP).</summary>
    public const int TeredoPort = 3544;

    /// <summary>Нормализованный домен для списка игры или null, если это не домен.</summary>
    public static string? NormalizeDomain(string? value)
    {
        var d = value?.Trim().TrimEnd('.').ToLowerInvariant();
        if (string.IsNullOrEmpty(d) || d.Length > 253 || !d.Contains('.') || System.Net.IPAddress.TryParse(d, out _))
            return null;
        foreach (var c in d)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                return null;
        }

        return d.StartsWith('.') || d.Contains("..", StringComparison.Ordinal) ? null : d;
    }

    /// <summary>Домены, которые генератор отдаёт игре: свои плюс набор Xbox у игр с Teredo, без повторов.</summary>
    public static IReadOnlyList<string> RealDnsDomains(GamesSettings games)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var g in games.AutoGames)
        {
            foreach (var d in g.Domains)
            {
                if (NormalizeDomain(d) is { } n)
                    set.Add(n);
            }

            if (g.NeedsTeredo)
                set.UnionWith(XboxDomains);
        }

        // Домен, который уже покрыт своим родителем (a.b.com при b.com), не нужен.
        return set.Where(d => !set.Any(p => p != d && d.EndsWith("." + p, StringComparison.Ordinal))).ToList();
    }
}
