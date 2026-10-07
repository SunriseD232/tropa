using System.Buffers.Binary;
using Tropa.Infrastructure.SystemIntegration;
using Tropa.Ipc;

namespace Tropa.Infrastructure.Tests;

internal sealed class FakeRegistry : IRegistryValues
{
    public Dictionary<string, string?> Values { get; } = new() { ["DisableSmartNameResolution"] = null, ["DisableParallelAandAAAA"] = "0" };

    public Dictionary<string, string?> Read(IReadOnlyCollection<string> names) => names.ToDictionary(n => n, n => Values.GetValueOrDefault(n));

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var (k, v) in values)
            Values[k] = v;
    }
}

public sealed class DnsClientPolicyTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-dns-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string Journal => Path.Combine(_dir.FullName, "journal.json");

    [Fact]
    public void Apply_and_restore()
    {
        var reg = new FakeRegistry();
        var original = new Dictionary<string, string?>(reg.Values);
        var policy = new DnsClientPolicy(reg, new ChangeJournal(Journal));
        policy.Apply(DateTimeOffset.UnixEpoch);
        Assert.Equal("1", reg.Values["DisableSmartNameResolution"]);
        Assert.Equal("1", reg.Values["DisableParallelAandAAAA"]);
        policy.Restore();
        Assert.Equal(original, reg.Values);
    }

    [Fact]
    public void Service_crash_is_recovered_from_journal()
    {
        var reg = new FakeRegistry();
        var original = new Dictionary<string, string?>(reg.Values);
        new DnsClientPolicy(reg, new ChangeJournal(Journal)).Apply(DateTimeOffset.UnixEpoch);
        new DnsClientPolicy(reg, new ChangeJournal(Journal)).Restore(); // новый экземпляр службы после сбоя
        Assert.Equal(original, reg.Values);
    }
}

public sealed class FramingTests
{
    [Fact]
    public async Task Round_trip_polymorphic_messages()
    {
        using var ms = new MemoryStream();
        await Framing.WriteAsync<Request>(ms, new StartRequest("{}", CaptureModeDto.Tun, 10880, false, true) { Id = 3 }, IpcJsonContext.Default.Request, CancellationToken.None);
        await Framing.WriteAsync<ServerMessage>(ms, new HelloReply(4, 1, "0.1.0", true, @"C:\ProgramData\Tropa\geo"), IpcJsonContext.Default.ServerMessage, CancellationToken.None);
        ms.Position = 0;

        var req = Assert.IsType<StartRequest>(await Framing.ReadAsync(ms, IpcJsonContext.Default.Request, CancellationToken.None));
        Assert.Equal((3, CaptureModeDto.Tun, 10880), (req.Id, req.Mode, req.ReadinessPort));
        var hello = Assert.IsType<HelloReply>(await Framing.ReadAsync(ms, IpcJsonContext.Default.ServerMessage, CancellationToken.None));
        Assert.True(hello.TunSupported);
        Assert.Null(await Framing.ReadAsync(ms, IpcJsonContext.Default.ServerMessage, CancellationToken.None)); // конец потока
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(IpcProtocol.MaxMessageBytes + 1)]
    public async Task Bad_lengths_are_rejected_before_allocation(int length)
    {
        var frame = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(frame, length);
        using var ms = new MemoryStream(frame);
        await Assert.ThrowsAsync<ProtocolViolationException>(() => Framing.ReadAsync(ms, IpcJsonContext.Default.Request, CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"type":"format-disk","id":1}""")]
    [InlineData("""{"id":1}""")]
    [InlineData("not json")]
    public async Task Unknown_or_broken_messages_are_rejected(string body)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(body);
        var frame = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        using var ms = new MemoryStream(frame);
        await Assert.ThrowsAsync<ProtocolViolationException>(() => Framing.ReadAsync(ms, IpcJsonContext.Default.Request, CancellationToken.None));
    }

    [Fact]
    public async Task Truncated_frame_is_rejected()
    {
        var frame = new byte[4 + 3];
        BinaryPrimitives.WriteInt32LittleEndian(frame, 100);
        using var ms = new MemoryStream(frame);
        await Assert.ThrowsAsync<ProtocolViolationException>(() => Framing.ReadAsync(ms, IpcJsonContext.Default.Request, CancellationToken.None));
    }
}
