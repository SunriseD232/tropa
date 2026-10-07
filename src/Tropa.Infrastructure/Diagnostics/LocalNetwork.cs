using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Tropa.Core.Diagnostics;
using Tropa.Infrastructure.Cores;

namespace Tropa.Infrastructure.Diagnostics;

/// <summary>Что происходит с сетью этого компьютера: настоящий адаптер и чужие VPN.</summary>
public static class LocalNetwork
{
    /// <summary>
    /// Имя физического адаптера с основным шлюзом (Ethernet или Wi‑Fi), не туннеля. null — не нашли,
    /// тогда ядро выбирает интерфейс само. Нужен, чтобы проверки не шли через TUN другого VPN.
    /// </summary>
    public static string? PhysicalInterfaceName()
    {
        try
        {
            var adapters = Adapters();
            var foreign = Diagnosis.ForeignVpnAdapters(adapters).Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                    && !foreign.Contains(ni.Name)
                    && ni.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                    && !ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(Diagnosis.OwnTunAddress)))
                .OrderBy(ni => ni.GetIPProperties().GetIPv4Properties()?.Index ?? int.MaxValue)
                .Select(ni => ni.Name)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    public static IReadOnlyList<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var props = ni.GetIPProperties();
            list.Add(new AdapterInfo(ni.Name, ni.Description, ni.OperationalStatus == OperationalStatus.Up,
                props.UnicastAddresses.Select(u => u.Address).ToList(),
                props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any))));
        }

        return list;
    }

    /// <summary>Запущенные клиенты обхода. sing-box и xray считаются чужими, только если это не наши файлы.</summary>
    public static List<string> ForeignProcesses(CoreLocations locations)
    {
        ArgumentNullException.ThrowIfNull(locations);
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var core in new[] { "sing-box", "xray" })
            ours.Add(Path.GetFullPath(CoreProcess.ExecutablePath(locations, core)));

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (Diagnosis.ForeignClients.Contains(p.ProcessName))
                {
                    found.Add(p.ProcessName);
                    continue;
                }

                if (!p.ProcessName.Equals("sing-box", StringComparison.OrdinalIgnoreCase) && !p.ProcessName.Equals("xray", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    // Ядра нашей службы работают под SYSTEM: путь к ним прочитать нельзя, их не считаем.
                    var path = p.MainModule?.FileName;
                    if (path is not null && !ours.Contains(Path.GetFullPath(path)) && !path.Contains(@"\Tropa\", StringComparison.OrdinalIgnoreCase))
                        found.Add($"{p.ProcessName} ({Path.GetFileName(Path.GetDirectoryName(path))})");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }

        return [.. found];
    }

    /// <summary>
    /// Предупреждение для пользователя, если работает другой VPN: два туннеля спорят за маршрут и DNS,
    /// а клиенты вроде v2rayN ещё и подменяют адрес соединения — Reality тогда «не проходит».
    /// null — мешающих программ нет.
    /// </summary>
    public static string? ForeignVpnWarning(CoreLocations locations)
    {
        var adapters = Diagnosis.ForeignVpnAdapters(Adapters()).Where(a => a.HasDefaultGateway || a.Addresses.Count > 0).ToList();
        var processes = ForeignProcesses(locations);
        if (adapters.Count == 0 && processes.Count == 0)
            return null;
        var what = processes.Count > 0 ? string.Join(", ", processes) : string.Join(", ", adapters.Select(a => $"«{a.Name}»"));
        return $"Работает другой VPN или клиент обхода ({what}). Закройте его (в v2rayN — «Выход» в меню значка), иначе подключение и проверки серверов могут не работать.";
    }
}
