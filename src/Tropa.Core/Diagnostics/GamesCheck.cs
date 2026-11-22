using Tropa.Core.Games;
using Tropa.Core.Model;

namespace Tropa.Core.Diagnostics;

/// <summary>Шаг диагностики «Игры» (ADR-065): Teredo, тип NAT и игры, которым мешает FakeIP.</summary>
public static class GamesCheck
{
    public const string Key = "games";

    public static StepResult Evaluate(GamesSettings games, TeredoStatus? teredo, bool serviceCanManage)
    {
        ArgumentNullException.ThrowIfNull(games);
        if (games.List.Count == 0)
            return new StepResult(Key, StepStatus.Skipped, "Игр в списке нет", "Тропа добавляет игры сама, когда они запущены (вкладка «Игры»).");

        var needsTeredo = games.List.Any(g => g.NeedsTeredo);
        var fakeIp = games.List.Where(g => g.FakeIpSeen && g.Mode == GameMode.Original).Select(g => g.Title).ToList();
        var details = new List<string>();
        var status = StepStatus.Ok;
        string? action = null;

        if (fakeIp.Count > 0)
        {
            status = StepStatus.Warn;
            details.Add("FakeIP мешает играм: " + string.Join(", ", fakeIp) + ".");
            action = "На вкладке «Игры» включите для них «Автонастройку Тропы», нажмите «Применить сейчас» и перезапустите игру.";
        }

        if (teredo is null)
        {
            if (needsTeredo)
                details.Add("Состояние Teredo узнать не удалось.");
        }
        else if (needsTeredo && !teredo.Working)
        {
            status = StepStatus.Bad;
            details.Add(teredo.Disabled ? "Teredo выключен — игры Xbox не соединятся с другими игроками."
                : $"Teredo не работает ({teredo.State}){(teredo.Error is { } e ? ": " + e : "")}.");
            action ??= serviceCanManage
                ? "На вкладке «Игры» нажмите «Включить» или «Перезапустить» в карточке Teredo."
                : "Выполните в PowerShell от имени администратора: netsh interface teredo set state type=enterpriseclient servername="
                    + GameDefaults.TeredoServer + ", затем Restart-Service iphlpsvc -Force.";
        }
        else if (teredo.Working)
        {
            details.Add($"Teredo работает ({teredo.State}).");
        }

        if (teredo?.StrictNat == true)
        {
            if (status == StepStatus.Ok)
                status = StepStatus.Warn;
            details.Add("NAT строгий (" + teredo.Nat + "): к сессиям друзей иногда не подключиться — включите UPnP в настройках роутера.");
        }

        var title = status switch
        {
            StepStatus.Ok => "С играми всё в порядке",
            StepStatus.Bad => "Играм Xbox не хватает Teredo",
            _ => "Играм могут мешать настройки сети",
        };
        return new StepResult(Key, status, title, details.Count == 0 ? null : string.Join(" ", details), action);
    }
}
