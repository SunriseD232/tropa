using System.Security.Principal;
using Tropa.Core.Diagnostics;
using Tropa.Core.Parsing;
using Tropa.Core.Testing;
using Tropa.Infrastructure.Diagnostics;
using Tropa.Infrastructure.SystemIntegration;

namespace Tropa.Infrastructure.Tests;

internal sealed class FakeLoopbackStore(params string[] initial) : ILoopbackStore
{
    public List<string> Sids { get; } = [.. initial];

    public IReadOnlyList<string> Read() => [.. Sids];

    public void Write(IReadOnlyList<string> sids)
    {
        Sids.Clear();
        Sids.AddRange(sids);
    }
}

public sealed class LoopbackExemptionTests : IDisposable
{
    private const string Mail = "S-1-15-2-1239072475-3687740317-1842961305-3395936705-4023953123-1525404051-2779347315";
    private const string Store = "S-1-15-2-536077884-713174666-1066051701-3219990555-339840825-1966734348-1611281757";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-loopback-");

    private ChangeJournal Journal => new(Path.Combine(_dir.FullName, "journal.json"));

    [Fact]
    public void Adds_only_missing_and_removes_only_ours()
    {
        var store = new FakeLoopbackStore(Mail); // это исключение пользователь сделал сам
        var exemption = new LoopbackExemption(store, Journal);
        exemption.Apply([Mail, Store], DateTimeOffset.UnixEpoch);
        Assert.Equal([Mail, Store], store.Sids);

        exemption.Restore();
        Assert.Equal([Mail], store.Sids);
        Assert.Empty(Journal.Pending());
    }

    [Fact]
    public void Crash_recovery_uses_journal()
    {
        var store = new FakeLoopbackStore();
        new LoopbackExemption(store, Journal).Apply([Store], DateTimeOffset.UnixEpoch);
        // «Служба упала»: новый экземпляр видит запись в журнале и откатывает.
        new LoopbackExemption(store, Journal).Restore();
        Assert.Empty(store.Sids);
    }

    [Theory]
    [InlineData("S-1-5-18")] // SYSTEM — не AppContainer
    [InlineData("S-1-15-2-1; DROP")]
    [InlineData("")]
    public void Rejects_non_appcontainer_sids(string sid)
    {
        var store = new FakeLoopbackStore();
        Assert.Throws<ArgumentException>(() => new LoopbackExemption(store, Journal).Apply([sid], DateTimeOffset.UnixEpoch));
        Assert.Empty(store.Sids);
    }

    [Fact]
    public void Store_apps_can_be_listed_without_admin()
    {
        var apps = FirewallLoopbackStore.Enumerate();
        Assert.All(apps, a => Assert.True(LoopbackExemption.IsAppContainerSid(a.Sid), a.Sid));
    }

    public void Dispose() => _dir.Delete(recursive: true);
}

public sealed class DiagnosticsStepTests
{
    private static readonly Core.Model.Profile Reality = ShareLink.Parse(
        "vless://b831381d-6324-4d53-ad4f-8cda48b30811@203.0.113.7:443?security=reality&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw&sni=www.microsoft.com&fp=chrome#R").Profile!;

    private static readonly string[] AfterTcp = ["realityHs", "dataFlow", "udpTest"];

    private static Dictionary<string, StepResult> Steps(ServerTestResult r) =>
        DiagnosticsRunner.ServerSteps(Reality, r).ToDictionary(s => s.Key);

    [Fact]
    public void Dead_server_skips_the_rest()
    {
        var s = Steps(new ServerTestResult { At = DateTimeOffset.UnixEpoch, Error = "Сервер не отвечает." });
        Assert.Equal(StepStatus.Bad, s["tcping"].Status);
        foreach (var k in AfterTcp)
            Assert.Equal(StepStatus.Skipped, s[k].Status);
    }

    [Fact]
    public void Handshake_failure_points_to_clock_for_reality()
    {
        var s = Steps(new ServerTestResult { At = DateTimeOffset.UnixEpoch, TcpMs = 40, Error = "ключ" });
        Assert.Equal(StepStatus.Bad, s["realityHs"].Status);
        Assert.Contains("часы", s["realityHs"].Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Frozen_and_udp()
    {
        var s = Steps(new ServerTestResult { At = DateTimeOffset.UnixEpoch, TcpMs = 40, DelayMs = 120, Frozen = true, FrozenAfterBytes = 20480, UdpOk = false });
        Assert.Equal(StepStatus.Bad, s["dataFlow"].Status);
        Assert.Contains("20 КБ", s["dataFlow"].Details, StringComparison.Ordinal);
        Assert.Equal(StepStatus.Warn, s["udpTest"].Status);

        var ok = Steps(new ServerTestResult { At = DateTimeOffset.UnixEpoch, TcpMs = 40, DelayMs = 120, SpeedMbps = 54.3, UdpOk = true });
        Assert.All(ok.Values, v => Assert.Equal(StepStatus.Ok, v.Status));
    }
}

/// <summary>
/// Настоящий WFP. Нужны права администратора, поэтому без них тест пропускается.
/// Фильтр узкий — только порт тестового слушателя на 127.0.0.1, так что сеть машины не страдает.
/// </summary>
public sealed class WfpTests
{
    private static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            c.Connect(System.Net.IPAddress.Loopback, port);
            return true;
        }
        catch (System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    [Fact]
    public void Dynamic_filter_blocks_and_disappears_with_session()
    {
        if (!IsAdmin())
            Assert.Skip("Нужны права администратора");
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        Assert.True(CanConnect(port));

        using (var session = new WfpSession("Тропа: тест"))
        {
            session.Transaction(() => session.AddFilter("тест", WfpLayer.ConnectV4, false, 1,
                new WfpCondition.Protocol(System.Net.Sockets.ProtocolType.Tcp), new WfpCondition.RemotePort(port),
                new WfpCondition.RemoteSubnet(System.Net.IPAddress.Loopback, 32)));
            Assert.False(CanConnect(port));
        }

        // Сеанс закрыт — динамический фильтр исчез сам.
        Assert.True(CanConnect(port));
    }
}
