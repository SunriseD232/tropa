using Tropa.Core.Diagnostics;
using Tropa.Core.Model;

namespace Tropa.Core.Tests.Diagnostics;

public sealed class SystemProxyCheckTests
{
    [Theory]
    [InlineData("127.0.0.1:10880", new[] { 10880 })]
    [InlineData("http=127.0.0.1:10880;https=localhost:10881;socks=[::1]:10882", new[] { 10880, 10881, 10882 })]
    [InlineData("http://127.0.0.1:10880/", new[] { 10880 })]
    [InlineData("corp-proxy:3128", new int[0])]
    [InlineData("10.0.0.1:8080;127.0.0.1:bad", new int[0])]
    [InlineData("", new int[0])]
    public void Loopback_ports_are_found_in_every_format(string server, int[] expected) =>
        Assert.Equal(expected, Diagnosis.LoopbackProxyPorts(server));

    [Fact]
    public void Stale_own_proxy_requires_enabled_unjournaled_own_port()
    {
        Assert.True(Diagnosis.IsStaleOwnProxy(new ProxySnapshot(true, "127.0.0.1:10880", null, false), [10880]));
        Assert.False(Diagnosis.IsStaleOwnProxy(new ProxySnapshot(false, "127.0.0.1:10880", null, false), [10880]));
        Assert.False(Diagnosis.IsStaleOwnProxy(new ProxySnapshot(true, "127.0.0.1:10880", null, true), [10880]));
        Assert.False(Diagnosis.IsStaleOwnProxy(new ProxySnapshot(true, "127.0.0.1:10808", null, false), [10880]));
    }

    [Fact]
    public void Step_verdicts()
    {
        Assert.Equal(StepStatus.Ok, Diagnosis.SystemProxy(new ProxySnapshot(false, "127.0.0.1:10880", null, false), CaptureMode.Tun, true, 10880).Status);
        Assert.Equal(StepStatus.Ok, Diagnosis.SystemProxy(new ProxySnapshot(true, "127.0.0.1:10880", null, true), CaptureMode.SystemProxy, true, 10880).Status);
        Assert.Equal(StepStatus.Bad, Diagnosis.SystemProxy(new ProxySnapshot(true, "127.0.0.1:10880", null, false), CaptureMode.Tun, true, 10880).Status);

        var v2rayN = Diagnosis.SystemProxy(new ProxySnapshot(true, "127.0.0.1:10808", null, false), CaptureMode.Tun, true, 10880);
        Assert.Equal(StepStatus.Warn, v2rayN.Status);
        Assert.Contains("v2rayN", v2rayN.Title, StringComparison.Ordinal);
        Assert.NotNull(v2rayN.Action);

        Assert.Equal(StepStatus.Warn, Diagnosis.SystemProxy(new ProxySnapshot(false, null, "http://wpad/proxy.pac", false), CaptureMode.Tun, false, 10880).Status);
        Assert.Equal(StepStatus.Ok, Diagnosis.SystemProxy(new ProxySnapshot(true, "corp:3128", null, false), CaptureMode.SystemProxy, false, 10880).Status);
    }
}
