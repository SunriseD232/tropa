using Tropa.Core.Parsing;
using Tropa.Core.Security;
using Tropa.Core.Testing;

namespace Tropa.Core.Model;

/// <summary>
/// Действия со списком серверов (как в v2rayN): поиск дубликатов, нерабочих по последней проверке, копия
/// сервера. Только вычисления — удаляет и сохраняет TropaEngine.
/// </summary>
public static class ServerListActions
{
    /// <summary>
    /// Лишние копии одного и того же сервера среди видимых: совпадает всё, кроме названия (ссылка без «#имени»).
    /// Из каждой группы остаётся выбранный сейчас сервер, иначе верхний в списке.
    /// </summary>
    public static IReadOnlyList<Guid> Duplicates(IEnumerable<StoredProfile> profiles, Guid? activeId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var extra = new List<Guid>();
        foreach (var group in profiles.Where(p => !p.Hidden).GroupBy(p => Key(p.Profile), StringComparer.Ordinal))
        {
            var keep = group.FirstOrDefault(p => p.Profile.Id == activeId) ?? group.OrderBy(p => p.Order).First();
            extra.AddRange(group.Where(p => p != keep).Select(p => p.Profile.Id));
        }

        return extra;
    }

    /// <summary>
    /// Серверы, которые на последней проверке не работали: нет ответа, рукопожатие не проходит, данные не идут.
    /// Выбранный сейчас сервер не трогаем — его убирают руками, иначе подключение без предупреждения сменится.
    /// </summary>
    public static IReadOnlyList<Guid> Dead(IEnumerable<StoredProfile> profiles, Guid? activeId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return profiles
            .Where(p => !p.Hidden && p.Profile.Id != activeId && p.LastTest is { } r
                && ServerHealth.Classify(r) is HealthStatus.NoResponse or HealthStatus.HandshakeError or HealthStatus.Frozen)
            .Select(p => p.Profile.Id)
            .ToList();
    }

    /// <summary>Копия сервера для правки: новый id, в «Мои» (не из подписки), с пометкой в названии.</summary>
    public static Profile Clone(Profile source, IEnumerable<string> existingNames)
    {
        ArgumentNullException.ThrowIfNull(source);
        var names = existingNames.ToHashSet(StringComparer.Ordinal);
        var name = NameSanitizer.Sanitize(source.Name + " (копия)", "Копия");
        for (var n = 2; names.Contains(name) && n < 1000; n++)
            name = NameSanitizer.Sanitize($"{source.Name} (копия {n})", "Копия");
        return source with { Id = Guid.NewGuid(), Name = name, SubscriptionId = null };
    }

    private static string Key(Profile p)
    {
        try
        {
            var link = ShareLink.Build(p with { Name = "-" });
            var hash = link.IndexOf('#', StringComparison.Ordinal);
            // Цепочки в ссылке нет, а сервер «через другой» — не дубликат прямого.
            return (hash < 0 ? link : link[..hash]) + "|" + p.ChainVia;
        }
        catch (ArgumentException)
        {
            // Ссылку собрать не удалось — сравниваем по адресу, порту, протоколу и ключу.
            return p.IdentityKey + "|" + p.ChainVia;
        }
    }
}
