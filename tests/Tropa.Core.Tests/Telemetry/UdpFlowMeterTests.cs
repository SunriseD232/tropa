using Tropa.Core.Telemetry;

namespace Tropa.Core.Tests.Telemetry;

public sealed class UdpFlowMeterTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private static CoreConnection Udp(string id, bool viaServer, long up, long down, string? process = "Discord.exe", int port = 50001) =>
        new(id, Udp: true, viaServer, process, port, up, down);

    private static Dictionary<string, string?> Fields(UdpFlowMeter meter) =>
        meter.Flush()!.ToDictionary(f => f.Key, f => f.Value);

    [Fact]
    public void Screen_share_via_server_is_measured_and_stalls_counted()
    {
        var meter = new UdpFlowMeter();
        meter.Add([Udp("a", true, 0, 0)], Window);                  // первый снимок — только счётчики
        meter.Add([Udp("a", true, 2_500_000, 100_000)], Window);    // 4000 кбит/с отдачи
        meter.Add([Udp("a", true, 2_500_000, 120_000)], Window);    // отдача встала
        meter.Add([Udp("a", true, 3_750_000, 140_000)], Window);    // 2000 кбит/с

        var f = Fields(meter);
        Assert.Equal("1", f["srvFlows"]);
        Assert.Equal("4000", f["srvUpPeak"]);
        Assert.Equal("1", f["srvStalls"]);
        Assert.Equal("2000", f["srvUpKbps"]); // 3,75 МБ за 15 с (первый снимок не в счёт)
        Assert.Equal("0", f["dirFlows"]);
        Assert.Equal("Discord.exe", f["app"]);
    }

    [Fact]
    public void Direct_udp_is_reported_separately()
    {
        var meter = new UdpFlowMeter();
        meter.Add([Udp("a", false, 0, 0), Udp("b", true, 0, 0, "Game.exe")], Window);
        meter.Add([Udp("a", false, 1_000_000, 0), Udp("b", true, 10_000, 10_000, "Game.exe")], Window);

        var f = Fields(meter);
        Assert.Equal("1", f["dirFlows"]);
        Assert.Equal("1600", f["dirUpKbps"]);
        Assert.Equal("Game.exe", f["app"]);
    }

    [Fact]
    public void Quiet_minute_and_dns_and_tcp_are_not_reported()
    {
        var meter = new UdpFlowMeter();
        meter.Add([Udp("dns", true, 0, 0, port: 53), new CoreConnection("t", Udp: false, true, "x.exe", 443, 0, 0)], Window);
        meter.Add([Udp("dns", true, 5_000_000, 5_000_000, port: 53), new CoreConnection("t", Udp: false, true, "x.exe", 443, 9_000_000, 9_000_000)], Window);
        Assert.Null(meter.Flush());
    }

    [Fact]
    public void Flush_resets_and_closed_connections_are_forgotten()
    {
        var meter = new UdpFlowMeter();
        meter.Add([Udp("a", true, 0, 0)], Window);
        meter.Add([Udp("a", true, 1_000_000, 0)], Window);
        Assert.NotNull(meter.Flush());

        // Соединение закрылось и появилось новое с тем же счётом — без «отрицательной» дельты и без переноса.
        meter.Add([], Window);
        meter.Add([Udp("a", true, 10, 0)], Window);
        Assert.Null(meter.Flush());
    }
}
