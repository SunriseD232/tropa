using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tropa.Core.Model;
using Tropa.Core.Routing;
using Tropa.Core.Security;

namespace Tropa.Core.Parsing;

/// <summary>
/// Подписка «от администратора» (ADR-048): обычная подписка, но в формате Тропы — кроме серверов в ней
/// выбранный администратором сервер, правила и частота обновления. Документ — недоверенные данные:
/// разбирается строго, всё неизвестное и неверное пропускается, правила проходят ту же нормализацию,
/// что и ввод руками. Правила и выбор сервера применяются, только если пользователь доверил это
/// администратору подписки (<see cref="Subscription.TrustAdmin"/>).
/// </summary>
public static class AdminDocument
{
    public const string Kind = "tropa-admin";
    public const int Version = 1;
    public const int MinRefreshMinutes = 5;
    public const int MaxRules = 200;
    private const int MaxValuesPerRule = 500;

    public sealed record Parsed(SubscriptionParseResult Servers, AdminChannel Channel, IReadOnlyList<string> Warnings);

    /// <summary>Похоже ли тело подписки на документ администратора (а не на JSON sing-box/Xray).</summary>
    public static bool Looks(string body) =>
        body.TrimStart().StartsWith('{') && body.Contains("\"" + Kind + "\"", StringComparison.Ordinal);

    public static Parsed? TryParse(string body)
    {
        Dto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(body, AdminJsonContext.Default.Dto);
        }
        catch (JsonException)
        {
            return null;
        }

        if (dto is null || dto.Kind != Kind)
            return null;

        var warnings = new List<string>();
        if (dto.Version > Version)
            warnings.Add("Подписка администратора создана более новой Тропой: часть настроек может не примениться. Обновите Тропу.");

        var profiles = new List<ImportedProfile>();
        var errors = new List<ImportError>();
        var position = 0;
        foreach (var link in (dto.Servers ?? []).Take(SubscriptionParser.MaxProfiles))
        {
            position++;
            var r = ShareLink.Parse(link ?? "");
            if (r.Success)
                profiles.Add(new ImportedProfile(r.Profile!, r.Warnings, position));
            else
                errors.Add(new ImportError(position, r.Error!));
        }

        var rules = new List<AdminRule>();
        foreach (var r in (dto.Rules ?? []).Take(MaxRules))
        {
            if (ToRule(r) is { } rule)
                rules.Add(rule);
        }

        if ((dto.Rules?.Count ?? 0) > rules.Count)
            warnings.Add($"Пропущено правил администратора: {dto.Rules!.Count - rules.Count} (пустые или неверные).");

        var games = new List<Games.GameTemplate>();
        foreach (var g in (dto.Games ?? []).Take(MaxGames))
        {
            if (ToGame(g) is { } game && !games.Any(x => Tropa.Core.Games.GameClassifier.SameExe(x.Exe, game.Exe)))
                games.Add(game);
        }

        var refresh = dto.RefreshMinutes is { } m ? Math.Clamp(m, MinRefreshMinutes, 24 * 60) : (int?)null;
        var channel = new AdminChannel
        {
            Title = string.IsNullOrWhiteSpace(dto.Title) ? null : NameSanitizer.Sanitize(dto.Title, "Администратор"),
            RefreshMinutes = refresh,
            ActiveServer = string.IsNullOrWhiteSpace(dto.Active) ? null : NameSanitizer.Sanitize(dto.Active, ""),
            Revision = string.IsNullOrWhiteSpace(dto.Revision) ? null : dto.Revision.Trim()[..Math.Min(dto.Revision.Trim().Length, 64)],
            Rules = rules,
            Games = games,
        };
        return new Parsed(new SubscriptionParseResult(SubscriptionFormat.LinkList, profiles, errors, 0), channel, warnings);
    }

    /// <summary>
    /// Документ для друзей: серверы (ссылки с ключами — документ секретный, как любая подписка),
    /// выбранный сервер, правила. «Через конкретный сервер» передаётся именем: у друга свои идентификаторы.
    /// </summary>
    public static string Build(IReadOnlyList<Profile> servers, Profile? active, IEnumerable<Rule> rules, string? title, int refreshMinutes, DateTimeOffset now,
        IEnumerable<GameProfile>? games = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(rules);
        var names = servers.ToDictionary(p => p.Id, p => p.Name);
        var dto = new Dto
        {
            Kind = Kind,
            Version = Version,
            Title = title,
            Revision = now.ToUniversalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            RefreshMinutes = Math.Clamp(refreshMinutes, MinRefreshMinutes, 24 * 60),
            Active = active is not null && names.ContainsKey(active.Id) ? active.Name : null,
            Servers = servers.Select(p => (string?)ShareLink.Build(p)).ToList(),
            Rules = rules.Where(r => r.Enabled && !r.Match.IsEmpty).Take(MaxRules).Select(r => new RuleDto
            {
                Label = r.Label,
                Processes = Nullable(r.Match.Processes),
                DomainSuffixes = Nullable(r.Match.DomainSuffixes),
                Domains = Nullable(r.Match.Domains),
                IpCidrs = Nullable(r.Match.IpCidrs),
                Ports = r.Match.Ports.Count == 0 ? null : r.Match.Ports.Select(p => (string?)(p.From == p.To ? p.From.ToString(CultureInfo.InvariantCulture) : $"{p.From}-{p.To}")).ToList(),
                Tcp = ActionText(r.Tcp, names),
                Udp = ActionText(r.Udp, names),
            }).Where(r => r.Processes is not null || r.DomainSuffixes is not null || r.Domains is not null || r.IpCidrs is not null).Select(r => (RuleDto?)r).ToList(),
            // Игры (ADR-065): только на автонастройке и только то, что нужно другу, — без путей и истории.
            Games = games?.Where(g => g.Mode == GameMode.Auto && g.Exe.Length > 0).Take(MaxGames).Select(g => (GameDto?)new GameDto
            {
                Exe = g.Exe,
                Name = g.Name.Length > 0 ? g.Name : null,
                Tcp = ActionText(g.Tcp, names),
                Udp = ActionText(g.Udp, names),
                Domains = Nullable(g.Domains.Take(MaxValuesPerRule).ToList()),
                Teredo = g.NeedsTeredo ? true : null,
            }).ToList() is { Count: > 0 } list ? list : null,
        };
        return JsonSerializer.Serialize(dto, AdminJsonContext.Default.Dto);
    }

    public const int MaxGames = 100;

    /// <summary>Игра от администратора: exe обязателен, домены — как у правил; «через конкретный сервер» у игр не передаётся.</summary>
    private static Games.GameTemplate? ToGame(GameDto? g)
    {
        if (g is null || RuleInput.NormalizeProcess(g.Exe) is not { } exe)
            return null;
        var name = string.IsNullOrWhiteSpace(g.Name) ? "" : NameSanitizer.Sanitize(g.Name, "");
        static RuleAction Act(string? v) => ParseAction(v).Kind switch
        {
            RuleActionKind.Direct => RuleAction.Direct,
            RuleActionKind.Block => RuleAction.Block,
            _ => RuleAction.Proxy,
        };
        var domains = (g.Domains ?? []).Take(MaxValuesPerRule).Select(GameDefaults.NormalizeDomain).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        return new Games.GameTemplate(exe, name, Act(g.Tcp), Act(g.Udp), domains, g.Teredo == true);
    }

    private static List<string?>? Nullable(IReadOnlyList<string> values) => values.Count == 0 ? null : values.Select(v => (string?)v).ToList();

    private static string ActionText(RuleAction a, Dictionary<Guid, string> names) => a.Kind switch
    {
        RuleActionKind.Direct => "direct",
        RuleActionKind.Block => "block",
        RuleActionKind.Server when a.ServerId is { } id && names.TryGetValue(id, out var name) => "server:" + name,
        _ => "proxy",
    };

    private static AdminRule? ToRule(RuleDto? r)
    {
        if (r is null)
            return null;
        var match = new RuleMatch
        {
            Processes = Clean(r.Processes, RuleInput.NormalizeProcess),
            DomainSuffixes = Clean(r.DomainSuffixes, RuleInput.NormalizeDomain),
            Domains = Clean(r.Domains, RuleInput.NormalizeDomain),
            IpCidrs = Clean(r.IpCidrs, NormalizeCidr),
            Ports = (r.Ports ?? []).Take(MaxValuesPerRule).Select(ParsePorts).OfType<PortRange>().ToList(),
        };
        // Только порт без процесса или адреса — слишком широкое правило от чужого человека.
        if (!match.HasProcessCondition && match.Domains.Count == 0 && match.DomainSuffixes.Count == 0 && match.IpCidrs.Count == 0)
            return null;
        var label = string.IsNullOrWhiteSpace(r.Label) ? null : NameSanitizer.Sanitize(r.Label, "");
        return new AdminRule(label, match, ParseAction(r.Tcp), ParseAction(r.Udp));
    }

    private static List<string> Clean(IReadOnlyList<string?>? values, Func<string?, string?> normalize) =>
        (values ?? []).Take(MaxValuesPerRule).Select(normalize).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private static string? NormalizeCidr(string? v)
    {
        var s = v?.Trim();
        if (string.IsNullOrEmpty(s))
            return null;
        if (!s.Contains('/', StringComparison.Ordinal) && IPAddress.TryParse(s, out var ip))
            return ip + (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128");
        return IPNetwork.TryParse(s, out var net) ? net.ToString() : null;
    }

    private static PortRange? ParsePorts(string? v)
    {
        var s = v?.Trim() ?? "";
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        var from = dash < 0 ? s : s[..dash];
        var to = dash < 0 ? s : s[(dash + 1)..];
        return int.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out var a)
            && int.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out var b)
            && a is >= 1 and <= 65535 && b >= a && b <= 65535
            ? new PortRange(a, b)
            : null;
    }

    /// <summary>proxy / direct / block / server:Имя. Неизвестное — «через сервер»: так безопаснее, чем напрямую.</summary>
    private static AdminAction ParseAction(string? v)
    {
        var s = v?.Trim() ?? "";
        if (s.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return new AdminAction(RuleActionKind.Direct, null);
        if (s.Equals("block", StringComparison.OrdinalIgnoreCase))
            return new AdminAction(RuleActionKind.Block, null);
        if (s.StartsWith("server:", StringComparison.OrdinalIgnoreCase) && s.Length > 7)
            return new AdminAction(RuleActionKind.Server, NameSanitizer.Sanitize(s[7..], ""));
        return new AdminAction(RuleActionKind.Proxy, null);
    }

    internal sealed record Dto
    {
        public string? Kind { get; init; }
        public int Version { get; init; }
        public string? Title { get; init; }
        public string? Revision { get; init; }
        public int? RefreshMinutes { get; init; }
        public string? Active { get; init; }
        public List<string?>? Servers { get; init; }
        public List<RuleDto?>? Rules { get; init; }
        public List<GameDto?>? Games { get; init; }
    }

    internal sealed record GameDto
    {
        public string? Exe { get; init; }
        public string? Name { get; init; }
        public string? Tcp { get; init; }
        public string? Udp { get; init; }
        public List<string?>? Domains { get; init; }
        public bool? Teredo { get; init; }
    }

    internal sealed record RuleDto
    {
        public string? Label { get; init; }
        public List<string?>? Processes { get; init; }
        public List<string?>? DomainSuffixes { get; init; }
        public List<string?>? Domains { get; init; }
        public List<string?>? IpCidrs { get; init; }
        public List<string?>? Ports { get; init; }
        public string? Tcp { get; init; }
        public string? Udp { get; init; }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, WriteIndented = true)]
[JsonSerializable(typeof(AdminDocument.Dto))]
internal sealed partial class AdminJsonContext : JsonSerializerContext;
