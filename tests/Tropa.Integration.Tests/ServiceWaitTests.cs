using Tropa.Infrastructure;
using Tropa.Infrastructure.Service;

namespace Tropa.Integration.Tests;

/// <summary>Автозагрузка раньше службы: Тропа ждёт установленную службу, а не уходит в «Только браузеры».</summary>
public sealed class ServiceWaitTests : IDisposable
{
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-svcwait-");

    public void Dispose() => _work.Delete(recursive: true);

    private TropaEngine Open(Func<DateTimeOffset> now, bool installed, Action onTry) =>
        TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, "r"), Path.Combine(_work.FullName, "l")), new FakeProxyStore(),
            now: now, connectService: _ => { onTry(); return Task.FromResult<ServiceClient?>(null); }, serviceInstalled: () => installed);

    [Fact]
    public async Task Installed_service_is_awaited_until_the_deadline()
    {
        var t = DateTimeOffset.UnixEpoch;
        var tries = 0;
        await using var engine = Open(() => t, installed: true, () => { tries++; t += TimeSpan.FromSeconds(30); });

        Assert.True(engine.ServiceStarting);
        Assert.Contains("не отвечает", engine.NoTunReason(), StringComparison.Ordinal);
        Assert.False(await engine.WaitForServiceAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
        Assert.True(tries >= 3, $"попыток: {tries}");
    }

    [Fact]
    public async Task Missing_service_is_not_awaited()
    {
        var tries = 0;
        await using var engine = Open(() => DateTimeOffset.UnixEpoch, installed: false, () => tries++);

        Assert.False(engine.ServiceStarting);
        Assert.Contains("не установлена", engine.NoTunReason(), StringComparison.Ordinal);
        Assert.False(await engine.WaitForServiceAsync(TimeSpan.FromSeconds(90), TestContext.Current.CancellationToken));
        Assert.Equal(1, tries);
    }
}
