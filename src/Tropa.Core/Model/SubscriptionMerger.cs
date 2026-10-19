namespace Tropa.Core.Model;

/// <summary>Итог обновления подписки.</summary>
public sealed record MergeResult(IReadOnlyList<StoredProfile> Profiles, int Added, int Updated, int Removed);

/// <summary>
/// Слияние свежего списка серверов подписки с сохранённым. Сервер узнаётся по
/// <see cref="Profile.IdentityKey"/>: при совпадении сохраняются Id (значит, и выбранный
/// сервер, и ссылки из правил), избранное и порядок. Пропавшие серверы помечаются и
/// удаляются через <see cref="RemovalGrace"/>.
/// </summary>
public static class SubscriptionMerger
{
    public static readonly TimeSpan RemovalGrace = TimeSpan.FromDays(7);

    public static MergeResult Merge(Guid subscriptionId, IReadOnlyList<StoredProfile> existing, IReadOnlyList<Profile> fresh, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(fresh);

        var mine = existing.Where(p => p.Profile.SubscriptionId == subscriptionId).ToList();
        var others = existing.Where(p => p.Profile.SubscriptionId != subscriptionId);
        var byKey = new Dictionary<string, StoredProfile>(StringComparer.Ordinal);
        foreach (var p in mine)
            byKey.TryAdd(p.Profile.IdentityKey, p);

        var result = new List<StoredProfile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int added = 0, updated = 0, order = 0;
        foreach (var profile in fresh)
        {
            var key = profile.IdentityKey;
            if (!seen.Add(key))
                continue; // дубликаты внутри подписки отбрасываем

            if (byKey.TryGetValue(key, out var old))
            {
                // Полосу Hysteria2, заданную пользователем, ссылка без полосы не сбрасывает.
                var merged = profile with
                {
                    Id = old.Profile.Id,
                    SubscriptionId = subscriptionId,
                    ChainVia = old.Profile.ChainVia,
                    UpMbps = profile.UpMbps ?? old.Profile.UpMbps,
                    DownMbps = profile.DownMbps ?? old.Profile.DownMbps,
                };
                if (merged != old.Profile)
                    updated++;
                result.Add(old with { Profile = merged, RemovedByProvider = null, Order = order++ });
            }
            else
            {
                result.Add(new StoredProfile { Profile = profile with { SubscriptionId = subscriptionId }, Order = order++ });
                added++;
            }
        }

        var removed = 0;
        foreach (var old in mine.Where(p => !seen.Contains(p.Profile.IdentityKey)))
        {
            var since = old.RemovedByProvider ?? now;
            if (now - since >= RemovalGrace)
                continue;
            if (old.RemovedByProvider is null)
                removed++;
            result.Add(old with { RemovedByProvider = since, Order = order++ });
        }

        // Порядок, который задал пользователь, сохраняется: известные серверы остаются на своих местах,
        // новые встают следом в порядке провайдера.
        var known = mine.ToDictionary(p => p.Profile.Id, p => p.Order);
        var tail = (known.Count == 0 ? existing.Select(p => p.Order).DefaultIfEmpty(-1).Max() : known.Values.Max()) + 1;
        var first = known.Count == 0 ? tail : known.Values.Min();
        result = result
            .Select((p, i) => (p, order: known.TryGetValue(p.Profile.Id, out var o) ? o : tail, i))
            .OrderBy(x => x.order).ThenBy(x => x.i)
            .Select((x, n) => x.p with { Order = first + n })
            .ToList();

        return new MergeResult([.. others, .. result], added, updated, removed);
    }
}
