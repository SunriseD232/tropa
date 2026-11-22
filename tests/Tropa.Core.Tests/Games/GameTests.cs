using Tropa.Core.Games;
using Tropa.Core.Model;
using Tropa.Core.Telemetry;

namespace Tropa.Core.Tests.Games;

public sealed class GameClassifierTests
{
    private static readonly Func<string, bool> NoFiles = _ => false;

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", GameLauncher.Steam)]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", GameLauncher.Epic)]
    [InlineData(@"C:\Riot Games\VALORANT\live\VALORANT.exe", GameLauncher.Riot)]
    [InlineData(@"C:\GOG Games\Witcher 3\bin\x64\witcher3.exe", GameLauncher.Gog)]
    public void Recognizes_games_by_launcher_folder(string path, GameLauncher expected) =>
        Assert.Equal(expected, GameClassifier.Classify(path, NoFiles));

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\bin\cef\cef.win7x64\steamwebhelper.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Game\UnityCrashHandler64.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Fortnite\EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe")]
    [InlineData(@"C:\Program Files\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe")]
    [InlineData(@"C:\Program Files\Discord\Discord.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_launchers_helpers_and_ordinary_programs(string? path) =>
        Assert.Null(GameClassifier.Classify(path, NoFiles));

    [Fact]
    public void Xbox_game_needs_MicrosoftGame_config_next_to_exe()
    {
        const string forza = @"C:\Program Files\WindowsApps\Microsoft.ForteBaseGame_3.483.970.0_x64__8wekyb3d8bbwe\forzahorizon6.exe";
        Assert.Equal(GameLauncher.Xbox, GameClassifier.Classify(forza,
            f => f == @"C:\Program Files\WindowsApps\Microsoft.ForteBaseGame_3.483.970.0_x64__8wekyb3d8bbwe\MicrosoftGame.config"));
        // Обычное приложение Store — не игра.
        Assert.Null(GameClassifier.Classify(@"C:\Program Files\WindowsApps\Microsoft.WindowsCalculator_11\CalculatorApp.exe", NoFiles));
        // Папка XboxGames: config на уровень выше Content.
        Assert.Equal(GameLauncher.Xbox, GameClassifier.Classify(@"C:\XboxGames\Halo Infinite\Content\HaloInfinite.exe",
            f => f == @"C:\XboxGames\Halo Infinite\Content\MicrosoftGame.config"));
    }

    [Fact]
    public void Name_from_exe_description_then_folder_then_file()
    {
        Assert.Equal("Forza Horizon 6", GameClassifier.GuessName(@"C:\x\forzahorizon6.exe", null, "Forza Horizon 6"));
        Assert.Equal("Counter-Strike Global Offensive",
            GameClassifier.GuessName(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", "cs2.exe", ""));
        Assert.Equal("game", GameClassifier.GuessName(@"C:\Other\game.exe", null, null));
    }
}

public sealed class GameListTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 18, 0, 0, TimeSpan.Zero);

    private static DetectedGame Forza => new("forzahorizon6.exe", @"C:\Program Files\WindowsApps\F\forzahorizon6.exe", GameLauncher.Xbox, "Forza Horizon 6");

    [Fact]
    public void New_game_is_added_as_original_and_xbox_needs_teredo()
    {
        var (games, added) = GameList.MergeDetected(new GamesSettings(), [Forza, Forza with { Exe = "ForzaHorizon6.EXE" }], Now);
        var g = Assert.Single(games.List);
        Assert.Single(added);
        Assert.Equal(GameMode.Original, g.Mode);
        Assert.True(g.NeedsTeredo);
        Assert.Equal(RuleAction.Direct, g.Tcp);
        Assert.Equal(GameSource.Detected, g.Source);
        Assert.Empty(GameDefaults.RealDnsDomains(games)); // «как было» — в конфиг ничего не идёт
    }

    [Fact]
    public void Known_game_only_refreshes_last_seen_and_keeps_user_choices()
    {
        var mine = new GameProfile { Exe = "ForzaHorizon6.exe", Mode = GameMode.Auto, Tcp = RuleAction.Proxy, Name = "Моя Forza" };
        var (games, added) = GameList.MergeDetected(new GamesSettings { List = [mine] }, [Forza], Now);
        Assert.Empty(added);
        var g = Assert.Single(games.List);
        Assert.Equal(GameMode.Auto, g.Mode);
        Assert.Equal(RuleAction.Proxy, g.Tcp);
        Assert.Equal("Моя Forza", g.Name);
        Assert.Equal(Now, g.LastSeen);
    }

    [Fact]
    public void Admin_template_fills_new_game_but_not_its_mode()
    {
        var t = new GameTemplate("forzahorizon6.exe", "Forza", RuleAction.Proxy, RuleAction.Direct, ["forza.net"], NeedsTeredo: true);
        var (games, _) = GameList.MergeDetected(new GamesSettings(), [Forza], Now, [t]);
        var g = Assert.Single(games.List);
        Assert.Equal(GameMode.Original, g.Mode);
        Assert.Equal(RuleAction.Proxy, g.Tcp);
        Assert.Equal(["forza.net"], g.Domains);
    }

    [Fact]
    public void Removed_game_is_not_found_again()
    {
        var (games, _) = GameList.MergeDetected(new GamesSettings(), [Forza], Now);
        var removed = GameList.Remove(games, games.List[0].Id);
        Assert.Empty(removed.List);
        Assert.Equal(["forzahorizon6.exe"], removed.Ignored);
        var (again, added) = GameList.MergeDetected(removed, [Forza], Now);
        Assert.Empty(again.List);
        Assert.Empty(added);
    }

    [Fact]
    public void Xbox_display_name_from_config()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <ExecutableList>
                <Executable Name="forzahorizon6.exe" Id="Forzahorizon6" OverrideDisplayName="Forza Horizon 6" />
                <Executable Name="other.exe" OverrideDisplayName="ms-resource:Name" />
              </ExecutableList>
              <ShellVisuals DefaultDisplayName="Forza Pack" />
            </Game>
            """;
        Assert.Equal("Forza Horizon 6", GameClassifier.XboxDisplayName(xml, "ForzaHorizon6.exe"));
        Assert.Equal("Forza Pack", GameClassifier.XboxDisplayName(xml, "other.exe"));
        Assert.Null(GameClassifier.XboxDisplayName("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/windows/win.ini'>]><x>&e;</x>", "a.exe"));
    }

    [Fact]
    public void Learns_domains_of_listed_games_and_marks_fakeip()
    {
        var games = new GamesSettings
        {
            List =
            [
                new GameProfile { Exe = "ForzaHorizon6.exe", Domains = ["forza.net"] },
                new GameProfile { Exe = "quiet.exe", NoLearning = true },
            ],
        };
        var conns = new[]
        {
            Conn("forzahorizon6.exe", "api.forza.net", "198.18.0.5"),   // покрыт forza.net
            Conn("forzahorizon6.exe", "PlayFabAPI.com.", "198.18.0.7"), // нормализуется
            Conn("forzahorizon6.exe", "20.1.2.3", "20.1.2.3"),          // IP — не домен
            Conn("quiet.exe", "secret.example", "198.18.0.9"),          // запоминание выключено
            Conn("chrome.exe", "youtube.com", "198.18.0.10"),           // не игра
        };
        var learned = GameList.Learn(games, conns);
        Assert.Equal(["forza.net", "playfabapi.com"], learned.List[0].Domains);
        Assert.True(learned.List[0].FakeIpSeen);
        Assert.Empty(learned.List[1].Domains);
        Assert.True(learned.List[1].FakeIpSeen);

        // Ничего нового — тот же объект (настройки не сохраняются зря).
        Assert.Same(learned, GameList.Learn(learned, conns));
    }

    [Fact]
    public void Auto_game_is_not_marked_fakeip()
    {
        var games = new GamesSettings { List = [new GameProfile { Exe = "a.exe", Mode = GameMode.Auto }] };
        Assert.False(GameList.Learn(games, [Conn("a.exe", null, "198.18.3.4")]).List[0].FakeIpSeen);
    }

    [Fact]
    public void Real_dns_domains_include_xbox_set_and_drop_covered()
    {
        var games = new GamesSettings
        {
            List =
            [
                new GameProfile { Exe = "a.exe", Mode = GameMode.Auto, NeedsTeredo = true, Domains = ["eu.xboxlive.com", "x.example"] },
                new GameProfile { Exe = "b.exe", Mode = GameMode.Original, Domains = ["off.example"] },
            ],
        };
        var domains = GameDefaults.RealDnsDomains(games);
        Assert.Contains("xboxlive.com", domains);
        Assert.Contains("ipv6.microsoft.com", domains);
        Assert.Contains("x.example", domains);
        Assert.DoesNotContain("eu.xboxlive.com", domains);
        Assert.DoesNotContain("off.example", domains);
    }

    [Theory]
    [InlineData("Example.COM.", "example.com")]
    [InlineData("1.2.3.4", null)]
    [InlineData("localhost", null)]
    [InlineData("a b.com", null)]
    [InlineData("..com", null)]
    public void Normalizes_domains(string input, string? expected) => Assert.Equal(expected, GameDefaults.NormalizeDomain(input));

    [Fact]
    public void Old_settings_without_games_read_as_empty()
    {
        var json = """{"schemaVersion":1,"games":{"list":[{"exe":"a.exe","tcp":null,"domains":null}]}}""";
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, CamelCase)!;
        var g = Assert.Single(s.Games.List);
        Assert.Equal(RuleAction.Direct, g.Tcp);
        Assert.Empty(g.Domains);
        Assert.Equal(GameMode.Original, g.Mode);
        Assert.Empty(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}")!.Games.List);
    }

    private static readonly System.Text.Json.JsonSerializerOptions CamelCase = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };

    private static CoreConnection Conn(string process, string? host, string? ip) =>
        new("id", Udp: true, ViaServer: false, process, 443, 0, 0) { Host = host, DestinationIp = ip };
}

public sealed class TeredoStatusTests
{
    // Настоящие выводы netsh с компьютера владельца (русская Windows), 2026-11-22.
    private const string Disabled = """
        Параметры Teredo
        ---------------------------------------------
        Тип                         : disabled
        Имя сервера                 : win1910.ipv6.microsoft.com.
        Интервал обновления клиента : 30 секунд
        Порт клиента            : unspecified
        Состояние               : offline
        Ошибка                  : нет
        """;

    private const string Qualified = """
        Параметры Teredo
        ---------------------------------------------
        Тип                         : enterpriseclient
        Имя сервера                 : win1910.ipv6.microsoft.com
        Интервал обновления клиента : 30 секунд
        Порт клиента            : unspecified
        Состояние               : qualified
        Тип клиента             : teredo host-specific relay
        Сеть                 : managed
        NAT                     : symmetric (port)
        Особое поведение NAT : UPNP: Нет, Сохранение портов: Нет
        Локальное сопоставление : 192.168.1.232:57059
        Внешнее сопоставление NAT : 176.97.128.244:59265
        """;

    private const string Unreachable = """
        Параметры Teredo
        ---------------------------------------------
        Тип                         : enterpriseclient
        Имя сервера                 : win1910.ipv6.microsoft.com
        Интервал обновления клиента : 30 секунд
        Порт клиента            : unspecified
        Состояние               : offline
        Ошибка: основной сервер teredo недостижим через UDP
        """;

    private const string Probe = """
        Teredo Parameters
        ---------------------------------------------
        Type                    : enterpriseclient
        Server Name             : teredo.remlab.net
        Client Refresh Interval : 30 seconds
        Client Port             : unspecified
        State                   : probe (primary server)
        Client Type             : teredo host-specific relay
        Network                 : managed
        """;

    [Fact]
    public void Parses_disabled_without_error()
    {
        var s = TeredoStatus.Parse(Disabled)!;
        Assert.True(s.Disabled);
        Assert.False(s.Working);
        Assert.Equal("win1910.ipv6.microsoft.com", s.Server);
        Assert.Null(s.Error);
        Assert.Null(s.Nat);
    }

    [Fact]
    public void Parses_qualified_with_nat()
    {
        var s = TeredoStatus.Parse(Qualified)!;
        Assert.True(s.Working);
        Assert.Equal("enterpriseclient", s.Type);
        Assert.Equal("symmetric (port)", s.Nat);
        Assert.True(s.StrictNat);
    }

    [Fact]
    public void Parses_offline_error_and_probe()
    {
        var u = TeredoStatus.Parse(Unreachable)!;
        Assert.False(u.Working);
        Assert.Equal("основной сервер teredo недостижим через UDP", u.Error);
        var p = TeredoStatus.Parse(Probe)!;
        Assert.True(p.Probing);
        Assert.False(p.Working);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Не найдена команда")]
    public void Garbage_is_null(string text) => Assert.Null(TeredoStatus.Parse(text));
}

public sealed class GamesCheckTests
{
    private static TeredoStatus Teredo(string type, string state, string? nat = null) => new(type, "win1910.ipv6.microsoft.com", state, nat, null);

    [Fact]
    public void No_games_is_skipped() =>
        Assert.Equal(Tropa.Core.Diagnostics.StepStatus.Skipped, Tropa.Core.Diagnostics.GamesCheck.Evaluate(new GamesSettings(), null, true).Status);

    [Fact]
    public void Xbox_game_without_teredo_is_bad_with_command_when_no_service()
    {
        var games = new GamesSettings { List = [new GameProfile { Exe = "f.exe", NeedsTeredo = true }] };
        var r = Tropa.Core.Diagnostics.GamesCheck.Evaluate(games, Teredo("disabled", "offline"), serviceCanManage: false);
        Assert.Equal(Tropa.Core.Diagnostics.StepStatus.Bad, r.Status);
        Assert.Contains("netsh interface teredo set state type=enterpriseclient", r.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Fakeip_and_strict_nat_are_warnings()
    {
        var games = new GamesSettings { List = [new GameProfile { Exe = "cs2.exe", Name = "CS2", FakeIpSeen = true }] };
        var r = Tropa.Core.Diagnostics.GamesCheck.Evaluate(games, Teredo("enterpriseclient", "qualified", "symmetric (port)"), true);
        Assert.Equal(Tropa.Core.Diagnostics.StepStatus.Warn, r.Status);
        Assert.Contains("CS2", r.Details, StringComparison.Ordinal);
        Assert.Contains("UPnP", r.Details + r.Action, StringComparison.Ordinal);
    }

    [Fact]
    public void Working_teredo_is_ok()
    {
        var games = new GamesSettings { List = [new GameProfile { Exe = "f.exe", NeedsTeredo = true, Mode = GameMode.Auto }] };
        Assert.Equal(Tropa.Core.Diagnostics.StepStatus.Ok, Tropa.Core.Diagnostics.GamesCheck.Evaluate(games, Teredo("enterpriseclient", "qualified", "cone"), true).Status);
    }
}

public sealed class AdminGamesTests
{
    [Fact]
    public void Auto_games_go_to_friends_without_paths_and_come_back_as_templates()
    {
        var games = new[]
        {
            new GameProfile { Exe = "ForzaHorizon6.exe", Name = "Forza", Path = @"C:\secret\path\f.exe", Mode = GameMode.Auto, NeedsTeredo = true, Domains = ["forza.net"] },
            new GameProfile { Exe = "hidden.exe", Mode = GameMode.Original, Domains = ["private.example"] },
        };
        var json = Tropa.Core.Parsing.AdminDocument.Build([], null, [], "t", 5, DateTimeOffset.UnixEpoch, games);
        Assert.DoesNotContain("secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden.exe", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", json, StringComparison.Ordinal);

        var parsed = Tropa.Core.Parsing.AdminDocument.TryParse(json)!;
        var t = Assert.Single(parsed.Channel.Games);
        Assert.Equal("ForzaHorizon6.exe", t.Exe);
        Assert.True(t.NeedsTeredo);
        Assert.Equal(RuleAction.Direct, t.Udp);
        Assert.Equal(["forza.net"], t.Domains);
    }

    [Fact]
    public void Bad_games_from_admin_are_dropped()
    {
        const string json = """
            {"kind":"tropa-admin","version":1,"servers":[],"games":[
              {"exe":"   ","domains":["x.com"]},
              {"exe":"a<b>|c","domains":["x.com"]},
              {"exe":"ok.exe","tcp":"server:Чужой","domains":["1.2.3.4","good.example","bad domain"]},
              null
            ]}
            """;
        var t = Assert.Single(Tropa.Core.Parsing.AdminDocument.TryParse(json)!.Channel.Games);
        Assert.Equal("ok.exe", t.Exe);
        Assert.Equal(RuleAction.Proxy, t.Tcp); // «через конкретный сервер» у игр — просто через сервер
        Assert.Equal(["good.example"], t.Domains);
    }
}
