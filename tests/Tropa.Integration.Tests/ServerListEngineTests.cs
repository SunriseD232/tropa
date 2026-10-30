using Tropa.Core.Model;
using Tropa.Infrastructure;

namespace Tropa.Integration.Tests;

/// <summary>
/// Действия со списком серверов в движке (как в v2rayN): копия, дубликаты, выгрузка полного конфига
/// с ключами (ADR-054). Сам генератор проверяется эталонами и «sing-box check» (SingBoxCheckTests).
/// </summary>
public sealed class ServerListEngineTests : IDisposable
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private readonly DirectoryInfo _work = Directory.CreateTempSubdirectory("tropa-list-");

    public void Dispose() => _work.Delete(recursive: true);

    private TropaEngine Open() =>
        TropaEngine.Open(new EnginePaths(Path.Combine(_work.FullName, "r"), Path.Combine(_work.FullName, "l")), new FakeProxyStore());

    [Fact]
    public async Task Clone_lands_under_source_and_becomes_a_duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = Open();
        await engine.ImportTextAsync(
            $"vless://{Uuid}@nl.example.com:443?security=tls&sni=nl.example.com#NL\ntrojan://pw@fi.example.com:443?sni=fi.example.com#FI", null, ct);
        var nl = engine.State.Profiles.Single(p => p.Profile.Name == "NL");

        var copy = engine.CloneProfile(nl.Profile.Id);

        var ordered = engine.State.Profiles.OrderBy(p => p.Order).Select(p => p.Profile.Name).ToList();
        Assert.Equal(["NL", "NL (копия)", "FI"], ordered);
        Assert.Null(copy.SubscriptionId);
        // Копия совпадает с исходным во всём, кроме имени, — значит, это дубликат; остаётся выбранный (NL).
        Assert.Equal([copy.Id], engine.FindDuplicates());
        engine.RemoveProfiles(engine.FindDuplicates());
        Assert.Equal(["NL", "FI"], engine.State.Profiles.OrderBy(p => p.Order).Select(p => p.Profile.Name));
    }

    [Fact]
    public async Task Full_config_export_has_keys_and_no_system_changes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var engine = Open();
        await engine.ImportTextAsync($"vless://{Uuid}@nl.example.com:443?security=tls&sni=nl.example.com#NL", null, ct);
        var id = engine.State.Profiles.Single().Profile.Id;

        var (config, core) = engine.ExportFullConfig(id);

        Assert.Equal("sing-box", core);
        Assert.Contains(Uuid, config, StringComparison.Ordinal);
        // Режим «только порт»: без TUN и без прокси Windows.
        Assert.DoesNotContain("\"type\": \"tun\"", config, StringComparison.Ordinal);
        Assert.Contains("\"type\": \"mixed\"", config, StringComparison.Ordinal);
    }
}
