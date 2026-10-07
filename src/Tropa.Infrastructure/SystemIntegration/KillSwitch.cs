using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Tropa.Infrastructure.SystemIntegration;

/// <summary>
/// Аварийная блокировка (info.ru.json: killSwitch). Пока она включена, в сеть выходят только:
/// наши ядра (по пути к exe), трафик через наш TUN, петлевой интерфейс, DHCP и — если включён
/// обход локальной сети — локальные адреса. Всё остальное блокируется, даже когда ядро упало и TUN исчез.
/// Фильтры живут в динамическом сеансе WFP: закрытие службы или её падение снимает их (ADR-021).
/// </summary>
/// <summary>Блокировка, которую держит служба. Подменяется в тестах (настоящая требует прав администратора).</summary>
public interface IKillSwitch : IDisposable
{
    void AllowTunInterface(ulong luid);
}

public sealed class KillSwitch : IKillSwitch
{
    private const byte PermitWeight = 12;
    private const byte BlockWeight = 0;

    private static readonly (string Address, int Prefix)[] LanV4 =
        [("10.0.0.0", 8), ("172.16.0.0", 12), ("192.168.0.0", 16), ("169.254.0.0", 16), ("224.0.0.0", 4), ("255.255.255.255", 32)];

    private static readonly (string Address, int Prefix)[] LanV6 = [("fe80::", 10), ("fc00::", 7), ("ff00::", 8)];

    private readonly WfpSession _session;
    private readonly List<ulong> _tunFilters = [];

    public KillSwitch(IReadOnlyList<string> allowedApps, bool allowLan)
    {
        ArgumentNullException.ThrowIfNull(allowedApps);
        _session = new WfpSession("Тропа: аварийная блокировка");
        try
        {
            _session.Transaction(() =>
            {
                foreach (var (layer, v6) in new[] { (WfpLayer.ConnectV4, false), (WfpLayer.ConnectV6, true), (WfpLayer.AcceptV4, false), (WfpLayer.AcceptV6, true) })
                {
                    _session.AddFilter("Тропа: петля", layer, true, PermitWeight, new WfpCondition.Loopback());
                    foreach (var app in allowedApps)
                        _session.AddFilter("Тропа: ядро", layer, true, PermitWeight, new WfpCondition.App(app));
                    if (allowLan)
                    {
                        foreach (var (address, prefix) in v6 ? LanV6 : LanV4)
                            _session.AddFilter("Тропа: локальная сеть", layer, true, PermitWeight, new WfpCondition.RemoteSubnet(IPAddress.Parse(address), prefix));
                    }

                    _session.AddFilter("Тропа: всё остальное", layer, false, BlockWeight);
                }

                // DHCP: без него компьютер теряет адрес в сети, пока включена блокировка.
                _session.AddFilter("Тропа: DHCP", WfpLayer.ConnectV4, true, PermitWeight,
                    new WfpCondition.Protocol(ProtocolType.Udp), new WfpCondition.RemotePort(67));
                _session.AddFilter("Тропа: DHCPv6", WfpLayer.ConnectV6, true, PermitWeight,
                    new WfpCondition.Protocol(ProtocolType.Udp), new WfpCondition.RemotePort(547));
            });
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// <summary>Разрешает трафик через TUN. После перезапуска ядра у TUN новый LUID — старые фильтры заменяются.</summary>
    public void AllowTunInterface(ulong luid) => _session.Transaction(() =>
    {
        foreach (var id in _tunFilters)
            _session.DeleteFilter(id);
        _tunFilters.Clear();
        foreach (var layer in new[] { WfpLayer.ConnectV4, WfpLayer.ConnectV6, WfpLayer.AcceptV4, WfpLayer.AcceptV6 })
            _tunFilters.Add(_session.AddFilter("Тропа: туннель", layer, true, PermitWeight, new WfpCondition.LocalInterface(luid)));
    });

    /// <summary>LUID нашего TUN: интерфейс с адресом <paramref name="tunAddress"/>. Ждёт его появления.</summary>
    public static async Task<ulong?> FindTunLuidAsync(IPAddress tunAddress, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tunAddress);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(tunAddress)))
                    return WfpSession.InterfaceLuid(ni.Id);
            }

            if (DateTime.UtcNow > deadline)
                return null;
            await Task.Delay(200, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() => _session.Dispose();
}
