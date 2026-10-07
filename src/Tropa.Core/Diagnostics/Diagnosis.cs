using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Tropa.Core.Model;

namespace Tropa.Core.Diagnostics;

public enum StepStatus { Pending, Running, Ok, Warn, Bad, Skipped }

/// <summary>Результат шага диагностики (docs/07-testing-diagnostics.md, §2). Key — ключ справки в info.ru.json.</summary>
public sealed record StepResult(string Key, StepStatus Status, string Title, string? Details = null, string? Action = null);

/// <summary>Сетевой адаптер, как его видит диагностика.</summary>
public sealed record AdapterInfo(string Name, string Description, bool IsUp, IReadOnlyList<IPAddress> Addresses, bool HasDefaultGateway);

/// <summary>
/// Решения диагностики без ввода-вывода: что считать подменой DNS, расхождением часов,
/// чужим VPN и утечкой. Сбор данных — в Tropa.Infrastructure.
/// </summary>
public static partial class Diagnosis
{
    /// <summary>Адреса TUN самой Тропы (SingBoxConfigBuilder): такой адаптер — наш.</summary>
    public static readonly IPAddress OwnTunAddress = IPAddress.Parse("172.19.0.1");

    public static StepResult Clock(TimeSpan skew)
    {
        var abs = skew.Duration();
        var text = abs.TotalSeconds < 90 ? $"{abs.TotalSeconds:0} с" : $"{abs.TotalMinutes:0} мин";
        var side = skew > TimeSpan.Zero ? "спешат" : "отстают";
        if (abs < TimeSpan.FromSeconds(30))
            return new StepResult("clock", StepStatus.Ok, "Часы точные");
        const string action = "Параметры Windows → Время и язык → Дата и время → «Синхронизировать сейчас».";
        return abs < TimeSpan.FromMinutes(2)
            ? new StepResult("clock", StepStatus.Warn, $"Часы {side} на {text}", "Пока терпимо, но Reality и TLS чувствительны ко времени.", action)
            : new StepResult("clock", StepStatus.Bad, $"Часы {side} на {text}", "С таким расхождением сервер отвергает рукопожатие Reality, а сайты — сертификаты.", action);
    }

    /// <summary>Адрес, который провайдер мог вернуть вместо настоящего: пустой, локальный, частный.</summary>
    public static bool IsBogus(IPAddress a)
    {
        ArgumentNullException.ThrowIfNull(a);
        if (a.IsIPv4MappedToIPv6)
            a = a.MapToIPv4();
        if (IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any))
            return true;
        if (a.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = a.GetAddressBytes();
            return b[0] is 0 or 10 or 127
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127);
        }

        return a.IsIPv6LinkLocal || a.IsIPv6UniqueLocal || a.IsIPv6SiteLocal;
    }

    /// <summary>
    /// Сравнение ответа DNS провайдера с ответом через туннель. Разные адреса сами по себе не подмена:
    /// у CDN ответ зависит от того, откуда спрашивают. Подмена — когда провайдер отвечает
    /// «домена нет», молчит или даёт заглушку, а через туннель домен есть.
    /// </summary>
    public static DnsVerdict CompareDns(DnsAnswer? provider, DnsAnswer? tunnel)
    {
        if (tunnel is null || tunnel.Addresses.Count == 0)
            return DnsVerdict.Unknown;
        if (provider is null)
            return DnsVerdict.NoAnswer;
        if (provider.IsNxDomain || provider.Addresses.Count == 0)
            return DnsVerdict.Spoofed;
        if (provider.Addresses.All(IsBogus) && !tunnel.Addresses.All(IsBogus))
            return DnsVerdict.Spoofed;
        return provider.Addresses.Intersect(tunnel.Addresses).Any() ? DnsVerdict.Same : DnsVerdict.Different;
    }

    public static StepResult DnsPoison(IReadOnlyList<(string Domain, DnsVerdict Verdict)> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        var spoofed = checks.Where(c => c.Verdict is DnsVerdict.Spoofed or DnsVerdict.NoAnswer).Select(c => c.Domain).ToList();
        if (checks.All(c => c.Verdict == DnsVerdict.Unknown))
            return new StepResult("dnsPoison", StepStatus.Skipped, "Не удалось спросить DNS через туннель", "Сервер не пропустил DNS-запрос, сравнивать не с чем.");
        if (spoofed.Count > 0)
        {
            return new StepResult("dnsPoison", StepStatus.Warn, "Провайдер подменяет DNS: " + string.Join(", ", spoofed),
                "Это обычное дело в России. Тропа спрашивает такие домены через туннель, поэтому сайты откроются, если удалённый DNS не выключен.",
                "Не выключайте удалённый DNS и FakeIP в разделе DNS.");
        }

        return new StepResult("dnsPoison", StepStatus.Ok, "Подмены DNS не видно",
            checks.Any(c => c.Verdict == DnsVerdict.Different) ? "Адреса местами отличаются — для сетей доставки контента это нормально." : null);
    }

    [GeneratedRegex(@"wireguard|openvpn|tap-windows|tap-protonvpn|wintun|outline|warp|nordlynx|protonvpn|amnezia|hiddify|mullvad|expressvpn|forticlient|fortinet|cisco anyconnect|globalprotect|pangp|zerotier|radmin|hamachi|clash|sing-?box|sing-tun|v2ray|nekoray|happ|tun\d",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VpnAdapterPattern();

    /// <summary>Служебные «фильтры» драйверов (NDIS LWF), которые Windows показывает как отдельные адаптеры.</summary>
    [GeneratedRegex(@"-[^-]*Filter-\d{4}$|Packet Scheduler|QoS Packet", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilterPseudoAdapter();

    /// <summary>Чужие VPN-адаптеры: включены, похожи на VPN и это не наш TUN.</summary>
    public static IReadOnlyList<AdapterInfo> ForeignVpnAdapters(IEnumerable<AdapterInfo> adapters) =>
        adapters.Where(a => a.IsUp
                && !a.Addresses.Contains(OwnTunAddress)
                && !FilterPseudoAdapter().IsMatch(a.Name) && !FilterPseudoAdapter().IsMatch(a.Description)
                && (VpnAdapterPattern().IsMatch(a.Description) || VpnAdapterPattern().IsMatch(a.Name)))
            .ToList();

    /// <summary>Процессы других клиентов обхода, которые меняют системный прокси или ставят свой TUN.</summary>
    public static readonly IReadOnlySet<string> ForeignClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "v2rayN", "nekoray", "nekobox", "hiddify", "Hiddify", "clash-verge", "Clash Verge", "clash-win64", "mihomo", "Happ",
        "Throne", "Outline", "AmneziaVPN", "AmneziaWG", "wireguard", "openvpn-gui", "Psiphon3", "Warp", "Cloudflare WARP",
    };

    public static StepResult OtherVpn(IReadOnlyList<AdapterInfo> foreignAdapters, IReadOnlyList<string> foreignProcesses)
    {
        ArgumentNullException.ThrowIfNull(foreignAdapters);
        ArgumentNullException.ThrowIfNull(foreignProcesses);
        if (foreignAdapters.Count == 0 && foreignProcesses.Count == 0)
            return new StepResult("otherVpn", StepStatus.Ok, "Других VPN не видно");
        var parts = new List<string>();
        if (foreignAdapters.Count > 0)
            parts.Add("Включены адаптеры: " + string.Join(", ", foreignAdapters.Select(a => $"«{a.Name}» ({a.Description})")) + ".");
        if (foreignProcesses.Count > 0)
            parts.Add("Запущены программы: " + string.Join(", ", foreignProcesses) + ".");
        return new StepResult("otherVpn", StepStatus.Warn, "Работает другой VPN или клиент обхода", string.Join(" ", parts),
            "Отключите его (или закройте программу) на время работы Тропы: два туннеля спорят за маршрут и DNS.");
    }

    /// <summary>
    /// Утечки без сторонних сервисов (ADR-020): по режиму, настройкам и адаптерам видно, может ли
    /// DNS или IPv6 уйти мимо туннеля.
    /// </summary>
    public static StepResult Leaks(AppSettings effective, bool connected, IEnumerable<AdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(adapters);
        if (!connected)
            return new StepResult("leaks", StepStatus.Skipped, "Утечки проверяются при подключении");

        var hasIpv6 = adapters.Any(a => a.IsUp && a.HasDefaultGateway && !a.Addresses.Contains(OwnTunAddress)
            && a.Addresses.Any(x => x.AddressFamily == AddressFamily.InterNetworkV6 && !x.IsIPv6LinkLocal && !x.IsIPv6UniqueLocal && !x.IsIPv6SiteLocal));
        var notes = new List<string>();
        var status = StepStatus.Ok;

        if (effective.Connection.Mode == CaptureMode.Tun)
        {
            if (!effective.Dns.DnsHijack)
            {
                status = StepStatus.Warn;
                notes.Add("Перехват DNS выключен: программы со своим DNS-сервером спрашивают его мимо Тропы.");
            }

            if (!effective.Dns.SmartNameRes)
            {
                status = StepStatus.Warn;
                notes.Add("Windows рассылает DNS-запросы во все сетевые карты сразу, часть уходит провайдеру.");
            }

            if (!effective.Connection.StrictRoute)
            {
                status = StepStatus.Warn;
                notes.Add("Строгий маршрут выключен: часть трафика может пойти мимо туннеля.");
            }
        }
        else
        {
            status = StepStatus.Warn;
            notes.Add("В режиме прокси через Тропу идут только программы, которые используют прокси. Остальные (и их DNS) ходят напрямую — так и задумано, но это не полная защита.");
        }

        if (hasIpv6 && effective.Connection.Mode != CaptureMode.Tun)
            notes.Add("У провайдера есть IPv6: сайты по IPv6 программы без прокси открывают напрямую.");
        else if (hasIpv6 && !effective.General.Ipv6Block)
            notes.Add("IPv6 есть и не блокируется — он идёт через туннель, если сервер его поддерживает.");

        return status == StepStatus.Ok
            ? new StepResult("leaks", StepStatus.Ok, "Утечек не видно", notes.Count > 0 ? string.Join(" ", notes) : "DNS перехватывается, IPv6 идёт через туннель или заблокирован.")
            : new StepResult("leaks", status, "Возможны утечки", string.Join(" ", notes),
                effective.Connection.Mode == CaptureMode.Tun ? "Включите перехват DNS, отключение умного разрешения имён и строгий маршрут." : "Для полной защиты выберите режим «Весь компьютер».");
    }
}

public enum DnsVerdict { Unknown, Same, Different, Spoofed, NoAnswer }
