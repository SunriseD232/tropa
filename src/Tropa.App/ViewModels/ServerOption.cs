using Tropa.Infrastructure;

namespace Tropa.App.ViewModels;

/// <summary>Вариант «через какой сервер» у пункта маршрута (ADR-067): null — выбранный сейчас сервер.</summary>
internal sealed record ServerOption(Guid? Id, string Name)
{
    public override string ToString() => Name;

    /// <summary>«Выбранный сервер», затем видимые серверы в порядке списка. Скрытый, но уже выбранный в пункте — тоже.</summary>
    public static IReadOnlyList<ServerOption> List(TropaEngine engine, Guid? keep = null)
    {
        var state = engine.State;
        var list = new List<ServerOption> { new(null, "Выбранный сервер") };
        foreach (var p in state.Profiles.Where(p => !p.Hidden || p.Profile.Id == keep).OrderBy(p => p.Order))
            list.Add(new ServerOption(p.Profile.Id, p.Profile.Name));
        if (keep is { } id && list.All(o => o.Id != id))
            list.Add(new ServerOption(id, "Удалённый сервер (идёт через выбранный)"));
        return list;
    }

    /// <summary>Подпись для пересоздания строк: меняется, когда меняются серверы или их названия.</summary>
    public static string Signature(TropaEngine engine) =>
        string.Join("|", engine.State.Profiles.Where(p => !p.Hidden).OrderBy(p => p.Order).Select(p => p.Profile.Id.ToString("N")[..8] + p.Profile.Name));
}
