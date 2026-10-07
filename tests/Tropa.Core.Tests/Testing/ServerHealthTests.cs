using Tropa.Core.Testing;

namespace Tropa.Core.Tests.Testing;

public sealed class ServerHealthTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static ServerTestResult R(int? tcp = 40, int? delay = 60, double? speed = 50, bool frozen = false, string? error = null) =>
        new() { At = Now, TcpMs = tcp, DelayMs = delay, SpeedMbps = speed, Frozen = frozen, Error = error };

    [Fact]
    public void Statuses()
    {
        Assert.Equal(HealthStatus.Unknown, ServerHealth.Classify(null));
        Assert.Equal(HealthStatus.Working, ServerHealth.Classify(R()));
        Assert.Equal(HealthStatus.Slow, ServerHealth.Classify(R(delay: 900)));
        Assert.Equal(HealthStatus.Slow, ServerHealth.Classify(R(speed: 2)));
        Assert.Equal(HealthStatus.Frozen, ServerHealth.Classify(R(frozen: true)));
        Assert.Equal(HealthStatus.HandshakeError, ServerHealth.Classify(R(delay: null, error: "tls")));
        Assert.Equal(HealthStatus.NoResponse, ServerHealth.Classify(R(tcp: null, delay: null, error: "timeout")));
    }

    [Fact]
    public void Frozen_wins_over_good_ping()
    {
        // Главная ловушка: TCP-пинг и задержка отличные, а данные не идут.
        var r = R(tcp: 30, delay: 45, speed: null, frozen: true) with { FrozenAfterBytes = 16 * 1024 };
        Assert.Equal(HealthStatus.Frozen, ServerHealth.Classify(r));
        Assert.Contains("16 КБ", ServerHealth.Explain(r), StringComparison.Ordinal);
    }

    [Fact]
    public void Udp_failure_is_explained_but_server_still_works()
    {
        var r = R() with { UdpOk = false };
        Assert.Equal(HealthStatus.Working, ServerHealth.Classify(r));
        Assert.Contains("Discord", ServerHealth.Explain(r), StringComparison.Ordinal);
    }

    [Fact]
    public void Exclusion_from_auto_select_expires()
    {
        var frozen = R(frozen: true);
        Assert.True(ServerHealth.ShouldExclude(frozen, Now + TimeSpan.FromMinutes(10)));
        Assert.False(ServerHealth.ShouldExclude(frozen, Now + ServerHealth.ResultLifetime));
        Assert.False(ServerHealth.ShouldExclude(R(delay: 900), Now)); // медленный, но рабочий — не исключаем
        Assert.False(ServerHealth.ShouldExclude(null, Now));
    }

    [Fact]
    public void Stability_statistics()
    {
        var (loss, jitter, median) = ServerHealth.Stability([100, 110, null, 90, 100]);
        Assert.Equal(0.2, loss, 3);
        Assert.Equal(100, median);
        Assert.Equal(7, jitter);
        Assert.Equal((1.0, (int?)null, (int?)null), ServerHealth.Stability([null, null]));
    }

    private sealed class FakeTime : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void Freeze_detector()
    {
        var time = new FakeTime();
        var d = new FreezeDetector(time);
        time.Ticks += TimeSpan.FromSeconds(30).Ticks;
        Assert.False(d.IsFrozen); // ничего не пришло — это таймаут, а не «заморозка»
        Assert.True(d.NothingReceived);

        d.OnData(16 * 1024);
        time.Ticks += TimeSpan.FromSeconds(7).Ticks;
        Assert.False(d.IsFrozen);
        time.Ticks += TimeSpan.FromSeconds(1).Ticks;
        Assert.True(d.IsFrozen);

        d.OnData((int)FreezeDetector.Threshold); // данных много — это просто медленный сервер
        time.Ticks += TimeSpan.FromSeconds(60).Ticks;
        Assert.False(d.IsFrozen);
    }
}
