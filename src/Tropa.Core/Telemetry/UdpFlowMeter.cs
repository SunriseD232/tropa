using System.Globalization;

namespace Tropa.Core.Telemetry;

/// <summary>Соединение из Clash API ядра: счётчики байт нарастающим итогом.</summary>
public sealed record CoreConnection(string Id, bool Udp, bool ViaServer, string? Process, int DestinationPort, long Upload, long Download);

/// <summary>
/// Сводка UDP за минуту для журнала разработчика (событие 3.7): сколько потоков шло через сервер
/// и напрямую, средняя и пиковая скорость, сколько окон отдача стояла при живом потоке.
/// Нужна, чтобы отличать «тормозит демонстрация экрана» из-за сервера от «поток ушёл мимо сервера».
/// Без адресов: только имя программы и числа.
/// </summary>
public sealed class UdpFlowMeter
{
    /// <summary>Меньше этого за минуту (≈ 34 кбит/с) — не пишем: фоновый UDP, не звонок и не видео.</summary>
    public const long MinBytesPerMinute = 256 * 1024;

    private readonly Dictionary<string, (long Up, long Down)> _last = new(StringComparer.Ordinal);
    private Group _server = new();
    private Group _direct = new();
    private readonly Dictionary<string, long> _byProcess = new(StringComparer.OrdinalIgnoreCase);
    private int _windows;
    private bool _started;

    /// <summary>
    /// Очередной снимок соединений, сделанный через <paramref name="window"/> после предыдущего.
    /// Первый снимок соединения только запоминает счётчики: что было до него, к этому окну не относится.
    /// </summary>
    public void Add(IReadOnlyList<CoreConnection> connections, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        long serverUp = 0, serverDown = 0, directUp = 0, directDown = 0;
        int serverFlows = 0, directFlows = 0;
        foreach (var c in connections)
        {
            seen.Add(c.Id);
            if (!c.Udp || c.DestinationPort == 53)
                continue;
            var known = _last.TryGetValue(c.Id, out var prev);
            _last[c.Id] = (c.Upload, c.Download);
            if (c.ViaServer)
                serverFlows++;
            else
                directFlows++;
            if (!known)
                continue;
            var up = Math.Max(0, c.Upload - prev.Up);
            var down = Math.Max(0, c.Download - prev.Down);
            if (c.ViaServer)
            {
                serverUp += up;
                serverDown += down;
                if (c.Process is { Length: > 0 } p)
                    _byProcess[p] = _byProcess.GetValueOrDefault(p) + up + down;
            }
            else
            {
                directUp += up;
                directDown += down;
            }
        }

        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList())
            _last.Remove(gone);

        // Самый первый снимок только запоминает счётчики: его окно в среднюю скорость не входит.
        if (!_started)
        {
            _started = true;
            return;
        }

        var seconds = Math.Max(window.TotalSeconds, 0.001);
        _server.Add(serverFlows, serverUp, serverDown, seconds);
        _direct.Add(directFlows, directUp, directDown, seconds);
        _windows++;
    }

    /// <summary>Поля события за прошедшую минуту и сброс; null — UDP почти не было.</summary>
    public IReadOnlyList<(string Key, string? Value)>? Flush()
    {
        var server = _server;
        var direct = _direct;
        var windows = _windows;
        var top = _byProcess.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).FirstOrDefault();
        _server = new Group();
        _direct = new Group();
        _byProcess.Clear();
        _windows = 0;
        if (windows == 0 || server.Bytes + direct.Bytes < MinBytesPerMinute)
            return null;
        return
        [
            ("srvFlows", Num(server.MaxFlows)),
            ("srvUpKbps", Num(server.AvgUpKbps)),
            ("srvUpPeak", Num(server.PeakUpKbps)),
            ("srvDownKbps", Num(server.AvgDownKbps)),
            ("srvDownPeak", Num(server.PeakDownKbps)),
            // Окна по 5 с, где поток через сервер был, а отдача стояла: так видно замирания видео.
            ("srvStalls", Num(server.Stalls)),
            ("dirFlows", Num(direct.MaxFlows)),
            ("dirUpKbps", Num(direct.AvgUpKbps)),
            ("dirDownKbps", Num(direct.AvgDownKbps)),
            ("app", top),
            ("windows", Num(windows)),
        ];
    }

    private static string Num(long v) => v.ToString(CultureInfo.InvariantCulture);

    private sealed class Group
    {
        private long _up, _down;
        private double _seconds;

        public int MaxFlows { get; private set; }
        public long PeakUpKbps { get; private set; }
        public long PeakDownKbps { get; private set; }
        public int Stalls { get; private set; }
        public long Bytes => _up + _down;
        public long AvgUpKbps => Kbps(_up, _seconds);
        public long AvgDownKbps => Kbps(_down, _seconds);

        public void Add(int flows, long up, long down, double seconds)
        {
            MaxFlows = Math.Max(MaxFlows, flows);
            _up += up;
            _down += down;
            _seconds += seconds;
            PeakUpKbps = Math.Max(PeakUpKbps, Kbps(up, seconds));
            PeakDownKbps = Math.Max(PeakDownKbps, Kbps(down, seconds));
            if (flows > 0 && up == 0 && _up > 0)
                Stalls++;
        }

        private static long Kbps(long bytes, double seconds) => seconds <= 0 ? 0 : (long)(bytes * 8 / 1000.0 / seconds);
    }
}
