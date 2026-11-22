using System.Net;
using Tropa.Core.Model;
using Tropa.Core.Telemetry;

namespace Tropa.Core.Games;

/// <summary>Запущенный процесс, похожий на игру (ADR-065).</summary>
public sealed record DetectedGame(string Exe, string Path, GameLauncher Launcher, string Name);

/// <summary>
/// Готовые параметры игры от администратора подписки (ADR-065, решение 4): применяются к найденной
/// игре с тем же exe. Режим не задают — автонастройку включает только сам пользователь.
/// </summary>
public sealed record GameTemplate(string Exe, string Name, RuleAction Tcp, RuleAction Udp, IReadOnlyList<string> Domains, bool NeedsTeredo);

/// <summary>Изменения списка игр без IO: находки, обучение доменам, признак FakeIP (ADR-065).</summary>
public static class GameList
{
    /// <summary>Добавляет новые игры (режим «как было») и обновляет «видна сейчас» у известных.</summary>
    /// <returns>Новый список и только что найденные игры (для журнала и подсказки).</returns>
    public static (GamesSettings Games, IReadOnlyList<GameProfile> Added) MergeDetected(
        GamesSettings games, IEnumerable<DetectedGame> detected, DateTimeOffset now, IReadOnlyList<GameTemplate>? templates = null)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(detected);
        var list = games.List.ToList();
        var added = new List<GameProfile>();
        foreach (var d in detected.DistinctBy(d => d.Exe, StringComparer.OrdinalIgnoreCase))
        {
            if (games.Ignored.Any(x => GameClassifier.SameExe(x, d.Exe)))
                continue;
            var i = list.FindIndex(g => GameClassifier.SameExe(g.Exe, d.Exe));
            if (i >= 0)
            {
                list[i] = list[i] with
                {
                    LastSeen = now,
                    Path = d.Path,
                    Launcher = list[i].Launcher == GameLauncher.Unknown ? d.Launcher : list[i].Launcher,
                };
                continue;
            }

            var game = new GameProfile
            {
                Exe = d.Exe,
                Name = d.Name,
                Path = d.Path,
                Launcher = d.Launcher,
                Source = GameSource.Detected,
                NeedsTeredo = d.Launcher == GameLauncher.Xbox,
                LastSeen = now,
            };
            if (templates?.FirstOrDefault(t => GameClassifier.SameExe(t.Exe, d.Exe)) is { } t)
                game = ApplyTemplate(game, t);
            list.Add(game);
            added.Add(game);
        }

        return (games with { List = list }, added);
    }

    /// <summary>Параметры администратора поверх игры; режим и история игры не меняются.</summary>
    public static GameProfile ApplyTemplate(GameProfile game, GameTemplate t) => game with
    {
        Name = game.Name.Length > 0 ? game.Name : t.Name,
        Tcp = t.Tcp,
        Udp = t.Udp,
        NeedsTeredo = t.NeedsTeredo || game.NeedsTeredo,
        Domains = MergeDomains(game.Domains, t.Domains),
    };

    /// <summary>
    /// Учит домены игр по соединениям ядра и отмечает «FakeIP мешает» у игр «как было».
    /// Возвращает тот же объект, если ничего не изменилось (чтобы не сохранять настройки зря).
    /// </summary>
    public static GamesSettings Learn(GamesSettings games, IEnumerable<CoreConnection> connections)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(connections);
        if (games.List.Count == 0)
            return games;

        var byExe = new Dictionary<string, (HashSet<string> Domains, bool FakeIp)>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in connections)
        {
            if (c.Process is not { Length: > 0 } exe || !games.List.Any(g => GameClassifier.SameExe(g.Exe, exe)))
                continue;
            if (!byExe.TryGetValue(exe, out var seen))
                byExe[exe] = seen = (new HashSet<string>(StringComparer.Ordinal), false);
            if (GameDefaults.NormalizeDomain(c.Host) is { } domain)
                seen.Domains.Add(domain);
            if (IsFakeIp(c.DestinationIp))
                byExe[exe] = (seen.Domains, true);
        }

        if (byExe.Count == 0)
            return games;

        var changed = false;
        var list = games.List.Select(g =>
        {
            if (!byExe.TryGetValue(g.Exe, out var seen))
                return g;
            var next = g;
            if (!g.NoLearning)
            {
                var merged = MergeDomains(g.Domains, seen.Domains);
                if (merged.Count != g.Domains.Count)
                    next = next with { Domains = merged };
            }

            if (seen.FakeIp && g.Mode == GameMode.Original && !g.FakeIpSeen)
                next = next with { FakeIpSeen = true };
            changed |= !ReferenceEquals(next, g);
            return next;
        }).ToList();

        return changed ? games with { List = list } : games;
    }

    /// <summary>Свои домены плюс новые, без повторов и уже покрытых родителем; не больше <see cref="GameProfile.MaxDomains"/>.</summary>
    public static IReadOnlyList<string> MergeDomains(IReadOnlyList<string> current, IEnumerable<string> extra)
    {
        var result = current.ToList();
        var known = new HashSet<string>(current, StringComparer.Ordinal);
        foreach (var raw in extra)
        {
            if (result.Count >= GameProfile.MaxDomains)
                break;
            if (GameDefaults.NormalizeDomain(raw) is not { } d || known.Contains(d) || known.Any(k => d.EndsWith("." + k, StringComparison.Ordinal)))
                continue;
            known.Add(d);
            result.Add(d);
        }

        return result.Count == current.Count ? current : result;
    }

    /// <summary>Адрес из диапазона FakeIP Тропы (198.18.0.0/15).</summary>
    public static bool IsFakeIp(string? ip) =>
        IPAddress.TryParse(ip, out var a) && a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
        && a.GetAddressBytes() is [198, 18 or 19, _, _];

    /// <summary>Убрать игру из списка; найденная сама больше не вернётся (до ручного добавления).</summary>
    public static GamesSettings Remove(GamesSettings games, Guid id)
    {
        if (games.List.FirstOrDefault(g => g.Id == id) is not { } game)
            return games;
        return games with
        {
            List = games.List.Where(g => g.Id != id).ToList(),
            Ignored = games.Ignored.Any(x => GameClassifier.SameExe(x, game.Exe)) ? games.Ignored : [.. games.Ignored, game.Exe],
        };
    }

    /// <summary>Изменить одну игру по Id.</summary>
    public static GamesSettings Update(GamesSettings games, Guid id, Func<GameProfile, GameProfile> change) =>
        games with { List = games.List.Select(g => g.Id == id ? change(g) : g).ToList() };
}
