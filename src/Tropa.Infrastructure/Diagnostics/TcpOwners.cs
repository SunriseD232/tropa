using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace Tropa.Infrastructure.Diagnostics;

/// <summary>
/// Чьи TCP-соединения идут на локальный порт (таблица соединений Windows с PID владельца).
/// Нужно журналу разработчика: какая программа стучится в вход Тропы без пароля и получает 407
/// (журнал 2026-11-01: сотни таких ошибок при выключенном прокси Windows).
/// </summary>
public static partial class TcpOwners
{
    /// <summary>
    /// Имена процессов, у которых есть соединение с 127.0.0.1:<paramref name="port"/>, плюс владелец
    /// соединения с локальным портом <paramref name="clientPort"/> (если он известен из строки журнала).
    /// Слушающий сокет (само ядро) не считается.
    /// </summary>
    public static IReadOnlyList<string> LoopbackClients(int port, int? clientPort = null)
    {
        var pids = new HashSet<int>();
        foreach (var row in Rows())
        {
            if (row.State == ListenState || (!row.Local.Equals(IPAddress.Loopback) && !row.Remote.Equals(IPAddress.Loopback)))
                continue;
            if (row.RemotePort == port || clientPort is { } cp && row.LocalPort == cp && row.RemotePort != 0)
                pids.Add(row.Pid);
        }

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pid in pids)
        {
            if (pid is 0 or 4)
                continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                names.Add(p.ProcessName);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // Процесс уже завершился.
            }
        }

        return [.. names];
    }

    private const int ListenState = 2; // MIB_TCP_STATE_LISTEN

    private readonly record struct Row(int State, IPAddress Local, int LocalPort, IPAddress Remote, int RemotePort, int Pid);

    private static List<Row> Rows()
    {
        var rows = new List<Row>();
        var size = 0;
        _ = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
                if (result == ErrorInsufficientBuffer)
                    continue; // таблица выросла между вызовами
                if (result != 0)
                    return rows;
                var count = Marshal.ReadInt32(buffer);
                // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid — по 4 байта.
                for (var i = 0; i < count; i++)
                {
                    var at = buffer + 4 + i * 24;
                    rows.Add(new Row(
                        Marshal.ReadInt32(at),
                        new IPAddress((uint)Marshal.ReadInt32(at + 4)),
                        Port(Marshal.ReadInt32(at + 8)),
                        new IPAddress((uint)Marshal.ReadInt32(at + 12)),
                        Port(Marshal.ReadInt32(at + 16)),
                        Marshal.ReadInt32(at + 20)));
                }

                return rows;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return rows;
    }

    // Порт лежит в младших двух байтах в сетевом порядке.
    private static int Port(int raw) => BinaryPrimitives.ReverseEndianness((ushort)raw);

    private const int AfInet = 2;
    private const int TcpTableOwnerPidAll = 5;
    private const uint ErrorInsufficientBuffer = 122;

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int af, int tableClass, int reserved);
}
