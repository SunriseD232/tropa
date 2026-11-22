using System.Buffers.Binary;
using System.Text;
using Tropa.Core.Diagnostics;
using Tropa.Core.Model;

namespace Tropa.Core.Tests.Diagnostics;

public sealed class ServiceAccountProxyTests
{
    /// <summary>Как в реестре у LocalSystem после NekoBox: версия 0x46, флаги 3 (напрямую + прокси), 127.0.0.1:2080.</summary>
    internal static byte[] Blob(int flags, string? proxy, int counter = 7)
    {
        var text = proxy is null ? [] : Encoding.ASCII.GetBytes(proxy);
        var blob = new byte[16 + text.Length + 40];
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(0), 0x46);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(4), counter);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(8), flags);
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(12), text.Length);
        text.CopyTo(blob, 16);
        return blob;
    }

    [Fact]
    public void Blob_reports_proxy_flag_and_address()
    {
        Assert.Equal((true, "127.0.0.1:2080"), WinInetConnectionBlob.Parse(Blob(3, "127.0.0.1:2080")));
        Assert.Equal((false, (string?)null), WinInetConnectionBlob.Parse(Blob(1, null)));
        Assert.Null(WinInetConnectionBlob.Parse([1, 2, 3]));
        Assert.Null(WinInetConnectionBlob.Parse(null));
    }

    [Fact]
    public void Blob_with_bad_length_is_not_touched()
    {
        var blob = Blob(3, "127.0.0.1:2080");
        BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(12), 10_000);
        Assert.Null(WinInetConnectionBlob.Parse(blob));
        Assert.Null(WinInetConnectionBlob.WithoutProxy(blob));
    }

    [Fact]
    public void Disabling_clears_proxy_flag_sets_direct_and_bumps_counter()
    {
        var original = Blob(3, "127.0.0.1:2080", counter: 7);
        var fixedBlob = WinInetConnectionBlob.WithoutProxy(original)!;

        Assert.Equal((false, "127.0.0.1:2080"), WinInetConnectionBlob.Parse(fixedBlob));
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(fixedBlob.AsSpan(8)));
        Assert.Equal(8, BinaryPrimitives.ReadInt32LittleEndian(fixedBlob.AsSpan(4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(8)));
        Assert.Null(WinInetConnectionBlob.WithoutProxy(Blob(1, "127.0.0.1:2080")));
    }

    [Fact]
    public void Dead_proxy_is_loopback_only_enabled_and_unanswered()
    {
        static bool Nobody(int _) => false;
        Assert.True(Diagnosis.IsDeadServiceProxy(new("LocalSystem", true, "127.0.0.1:2080"), Nobody));
        Assert.True(Diagnosis.IsDeadServiceProxy(new("LocalSystem", true, "http=127.0.0.1:2080;https=localhost:2081"), Nobody));

        Assert.False(Diagnosis.IsDeadServiceProxy(new("LocalSystem", false, "127.0.0.1:2080"), Nobody));
        Assert.False(Diagnosis.IsDeadServiceProxy(new("LocalSystem", true, "127.0.0.1:2080"), port => port == 2080));
        // Внешний прокси (корпоративный) — не наш случай, даже если рядом есть локальный.
        Assert.False(Diagnosis.IsDeadServiceProxy(new("LocalSystem", true, "corp-proxy:3128"), Nobody));
        Assert.False(Diagnosis.IsDeadServiceProxy(new("LocalSystem", true, "http=corp-proxy:3128;https=127.0.0.1:2080"), Nobody));
        Assert.False(Diagnosis.IsDeadServiceProxy(new("LocalSystem", true, null), Nobody));
    }

    [Fact]
    public void Diagnostics_step_flags_dead_service_proxy_even_when_user_proxy_is_off()
    {
        var off = new ProxySnapshot(false, null, null, false);
        var dead = new ServiceAccountProxy[] { new("LocalSystem", true, "127.0.0.1:2080") };

        var withService = Diagnosis.SystemProxy(off, CaptureMode.Tun, true, 10880, dead, _ => false, serviceCanFix: true);
        Assert.Equal(StepStatus.Bad, withService.Status);
        Assert.Contains("127.0.0.1:2080", withService.Title, StringComparison.Ordinal);
        Assert.Contains("Переподключитесь", withService.Action, StringComparison.Ordinal);

        var noService = Diagnosis.SystemProxy(off, CaptureMode.Tun, true, 10880, dead, _ => false, serviceCanFix: false);
        Assert.Contains("службу Тропы", noService.Action, StringComparison.Ordinal);

        var alive = Diagnosis.SystemProxy(off, CaptureMode.Tun, true, 10880, dead, _ => true, serviceCanFix: true);
        Assert.Equal(StepStatus.Ok, alive.Status);
    }

    [Fact]
    public void Diagnostics_step_keeps_user_proxy_problem_in_front()
    {
        var foreign = new ProxySnapshot(true, "127.0.0.1:10808", null, false);
        var dead = new ServiceAccountProxy[] { new("LocalSystem", true, "127.0.0.1:2080") };
        var step = Diagnosis.SystemProxy(foreign, CaptureMode.Tun, true, 10880, dead, _ => false, serviceCanFix: true);

        Assert.Equal(StepStatus.Bad, step.Status);
        Assert.Contains("10808", step.Title, StringComparison.Ordinal);
        Assert.Contains("LocalSystem → 127.0.0.1:2080", step.Details, StringComparison.Ordinal);
        Assert.Contains("Параметры Windows", step.Action, StringComparison.Ordinal);
    }
}
