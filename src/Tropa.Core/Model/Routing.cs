using System.Text.Json.Serialization;

namespace Tropa.Core.Model;

public enum RoutePreset { ExceptRu, BlockedOnly, All }

public enum RuleActionKind { Proxy, Direct, Block, Server }

/// <summary>Действие правила. Server — отправить через конкретный профиль.</summary>
public sealed record RuleAction(RuleActionKind Kind, Guid? ServerId = null)
{
    public static RuleAction Proxy { get; } = new(RuleActionKind.Proxy);
    public static RuleAction Direct { get; } = new(RuleActionKind.Direct);
    public static RuleAction Block { get; } = new(RuleActionKind.Block);
}

public sealed record PortRange(int From, int To)
{
    public static PortRange Of(int port) => new(port, port);
}

/// <summary>
/// Условия правила, семантика как в sing-box: значения внутри одной группы объединяются через «ИЛИ»,
/// разные группы — через «И». Группы: процесс (Processes, ProcessPaths), адрес назначения
/// (Domains, DomainSuffixes, DomainKeywords, DomainRegexes, GeoSite, GeoIp, IpCidrs), порт (Ports).
/// Пример: Processes=[game.exe], Ports=[443] — «game.exe И порт 443».
/// </summary>
public sealed record RuleMatch
{
    public IReadOnlyList<string> Processes { get; init; } = [];
    public IReadOnlyList<string> ProcessPaths { get; init; } = [];
    public IReadOnlyList<string> Domains { get; init; } = [];
    public IReadOnlyList<string> DomainSuffixes { get; init; } = [];
    public IReadOnlyList<string> DomainKeywords { get; init; } = [];
    public IReadOnlyList<string> DomainRegexes { get; init; } = [];
    public IReadOnlyList<string> GeoSite { get; init; } = [];
    public IReadOnlyList<string> GeoIp { get; init; } = [];
    public IReadOnlyList<string> IpCidrs { get; init; } = [];
    public IReadOnlyList<PortRange> Ports { get; init; } = [];

    [JsonIgnore]
    public bool HasProcessCondition => Processes.Count > 0 || ProcessPaths.Count > 0;

    /// <summary>Есть условия по IP: для соединений по домену ядру нужно сначала узнать адрес.</summary>
    public bool HasIpCondition => GeoIp.Count > 0 || IpCidrs.Count > 0;

    [JsonIgnore]
    public bool IsEmpty =>
        !HasProcessCondition && Domains.Count == 0 && DomainSuffixes.Count == 0 && DomainKeywords.Count == 0
        && DomainRegexes.Count == 0 && GeoSite.Count == 0 && GeoIp.Count == 0 && IpCidrs.Count == 0 && Ports.Count == 0;
}

/// <summary>Правило с раздельными действиями для TCP и UDP (docs/04-domain-model.md, §4).</summary>
public sealed record Rule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public bool Enabled { get; init; } = true;
    public string? Label { get; init; }
    public required RuleMatch Match { get; init; }
    public required RuleAction Tcp { get; init; }
    public required RuleAction Udp { get; init; }
}

public sealed record RoutingSettings
{
    public RoutePreset Preset { get; init; } = RoutePreset.ExceptRu;

    /// <summary>
    /// Сначала обход DPI (info.ru.json: dpiFirst): заблокированное идёт напрямую с фрагментацией,
    /// а если обход перестал открывать сервис — Тропа сама переключает его группу на сервер.
    /// Явные правила пользователя важнее.
    /// </summary>
    public bool DpiFirst { get; init; }
    public bool BlockQuic { get; init; } = true;
    public bool UdpProxy { get; init; } = true;
    public IReadOnlyList<Rule> Rules { get; init; } = [];
}
