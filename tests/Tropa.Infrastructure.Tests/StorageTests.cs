using Tropa.Core.Model;
using Tropa.Core.Parsing;
using Tropa.Infrastructure.Storage;

namespace Tropa.Infrastructure.Tests;

public sealed class StorageTests : IDisposable
{
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("tropa-store-");
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Delete(recursive: true);

    private static AppState SampleState()
    {
        var profile = ShareLink.Parse($"trojan://super-secret-pw@fi.example.com:443?sni=fi.example.com#Helsinki").Profile!;
        var vless = ShareLink.Parse($"vless://{Uuid}@nl.example.com:443?security=none#NL").Profile!;
        var sub = new Subscription { Name = "Провайдер", Url = new Core.Security.Secret("https://panel.example.com/sub/TOKEN1234567890abcdef") };
        return new AppState
        {
            Settings = new AppSettings { Connection = new ConnectionSettings { SocksPort = 12345 } },
            Subscriptions = [sub],
            Profiles = [new StoredProfile { Profile = profile with { SubscriptionId = sub.Id }, Favorite = true }, new StoredProfile { Profile = vless, Order = 1 }],
            ActiveProfileId = vless.Id,
        };
    }

    [Fact]
    public void Round_trip_keeps_everything_and_secrets_stay_out_of_settings()
    {
        var store = new StateStore(_dir.FullName);
        var loaded = store.Load();
        var state = SampleState();
        store.Save(state, loaded.Secrets, Now);

        var settingsText = File.ReadAllText(store.SettingsPath);
        Assert.DoesNotContain("super-secret-pw", settingsText, StringComparison.Ordinal);
        Assert.DoesNotContain(Uuid, settingsText, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN1234567890abcdef", settingsText, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-pw", File.ReadAllText(store.SecretsPath), StringComparison.Ordinal); // зашифровано DPAPI

        var again = new StateStore(_dir.FullName).Load();
        Assert.Null(again.Warning);
        Assert.Equal(12345, again.State.Settings.Connection.SocksPort);
        Assert.Equal(state.ActiveProfileId, again.State.ActiveProfileId);
        Assert.Equal(state.Profiles.Select(p => p.Profile), again.State.Profiles.Select(p => p.Profile));
        Assert.True(again.State.Profiles[0].Favorite);
        Assert.Equal("https://panel.example.com/sub/TOKEN1234567890abcdef", again.State.Subscriptions[0].Url.Reveal());
    }

    [Fact]
    public void Corrupted_settings_fall_back_to_backup()
    {
        var store = new StateStore(_dir.FullName);
        var secrets = store.Load().Secrets;
        store.Save(SampleState(), secrets, Now);
        store.Save(SampleState() with { ActiveProfileId = null }, secrets, Now + TimeSpan.FromHours(2)); // создаёт резервную копию
        File.WriteAllText(store.SettingsPath, "{ обрыв записи");

        var loaded = new StateStore(_dir.FullName).Load();
        Assert.NotNull(loaded.Warning);
        Assert.Equal(2, loaded.State.Profiles.Count);
    }

    [Fact]
    public void Backups_are_rotated()
    {
        var store = new StateStore(_dir.FullName);
        var secrets = store.Load().Secrets;
        for (var i = 0; i < 10; i++)
            store.Save(SampleState(), secrets, Now + TimeSpan.FromHours(2 * i));
        Assert.Equal(StateStore.BackupsToKeep, Directory.GetFiles(store.BackupDirectory).Length);
    }

    [Fact]
    public void Newer_schema_is_never_overwritten()
    {
        var store = new StateStore(_dir.FullName);
        File.WriteAllText(store.SettingsPath, """{ "schema": 99, "settings": {} }""");
        Assert.Throws<InvalidDataException>(() => store.Load());
        Assert.Contains("99", File.ReadAllText(store.SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Secrets_live_while_any_backup_references_them()
    {
        var store = new StateStore(_dir.FullName);
        var secrets = store.Load().Secrets;
        var state = SampleState();
        store.Save(state, secrets, Now);
        var removedId = state.Profiles[0].Profile.Id;
        var reduced = state with { Profiles = state.Profiles.Skip(1).ToList() };

        store.Save(reduced, secrets, Now + TimeSpan.FromHours(2));
        Assert.NotNull(SecretStore.Open(store.SecretsPath).Get(StateStore.CredentialKey(removedId))); // копия ещё помнит сервер

        for (var i = 2; i < 2 + StateStore.BackupsToKeep + 1; i++)
            store.Save(reduced, secrets, Now + TimeSpan.FromHours(2 * i));
        Assert.Null(SecretStore.Open(store.SecretsPath).Get(StateStore.CredentialKey(removedId))); // копии вытеснены — секрет удалён
    }

    [Fact]
    public void Foreign_secrets_file_is_reported()
    {
        File.WriteAllBytes(Path.Combine(_dir.FullName, "secrets.bin"), [1, 2, 3, 4]);
        Assert.Throws<InvalidDataException>(() => new StateStore(_dir.FullName).Load());
    }

    [Fact]
    public void Atomic_write_leaves_no_temp_files()
    {
        var path = Path.Combine(_dir.FullName, "a.txt");
        AtomicFile.WriteAllText(path, "1");
        AtomicFile.WriteAllText(path, "2");
        Assert.Equal("2", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir.FullName));
    }
}
