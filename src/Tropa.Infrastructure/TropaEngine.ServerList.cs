using Tropa.Core.Compatibility;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Security;
using Tropa.Infrastructure.Storage;
using Tropa.Infrastructure.Testing;

namespace Tropa.Infrastructure;

/// <summary>Действия со списком серверов, как в v2rayN: копия, дубликаты, нерабочие, экспорт полного конфига.</summary>
public sealed partial class TropaEngine
{
    /// <summary>Копия сервера в «Мои» сразу под исходным. Возвращает новый сервер.</summary>
    public Profile CloneProfile(Guid profileId)
    {
        var source = State.Profiles.FirstOrDefault(p => p.Profile.Id == profileId)
            ?? throw new InvalidOperationException("Сервер не найден.");
        var copy = ServerListActions.Clone(source.Profile, State.Profiles.Select(p => p.Profile.Name));
        Update(s => s with
        {
            // Копия встаёт сразу за исходным: сдвигаем порядок остальных на одну позицию.
            Profiles = [.. s.Profiles.Select(p => p.Order > source.Order ? p with { Order = p.Order + 1 } : p),
                new StoredProfile { Profile = copy, Order = source.Order + 1, Core = source.Core }],
        });
        return copy;
    }

    /// <summary>Лишние копии одних и тех же серверов (ServerListActions.Duplicates).</summary>
    public IReadOnlyList<Guid> FindDuplicates() => ServerListActions.Duplicates(State.Profiles, State.ActiveProfileId);

    /// <summary>Серверы, не работавшие на последней проверке, кроме выбранного (ServerListActions.Dead).</summary>
    public IReadOnlyList<Guid> FindDead() => ServerListActions.Dead(State.Profiles, State.ActiveProfileId);

    /// <summary>
    /// Полный конфиг ядра для одного сервера — С КЛЮЧАМИ (ADR-054). Только по явному действию пользователя,
    /// результат уходит в файл, выбранный в диалоге. Вход — порт и пароль локального прокси из настроек,
    /// режим — «только порт» (без TUN и без прокси Windows): конфиг можно запустить ядром вручную.
    /// </summary>
    public (string Config, string Core) ExportFullConfig(Guid profileId)
    {
        var profile = State.Profiles.FirstOrDefault(p => p.Profile.Id == profileId)?.Profile
            ?? throw new InvalidOperationException("Сервер не найден.");
        var settings = State.Settings with
        {
            Connection = State.Settings.Connection with { Mode = CaptureMode.PortsOnly, RandomPorts = false },
            Routing = State.Settings.Routing with { Rules = [.. State.Settings.Routing.Rules, .. AdminRules()] },
        };
        var all = State.Profiles.Select(p => p.Profile).ToList();
        var used = UsedProfiles(profile, all, CompatRules.Evaluate(settings, profile).Effective).ToList();
        var plan = CorePlan.Make(settings, used, CoreFor);
        if (plan.XrayProfiles.Any(p => p.Id == profile.Id))
        {
            if (used.Count > 1)
                throw new UnsupportedProfileException("У сервера с ядром Xray есть цепочка или правила «через другой сервер» — одним конфигом их не выгрузить.");
            var xray = XrayConfigBuilder.Build([new XrayTarget(profile, settings.Connection.SocksPort)], settings, Auth);
            return (xray, "Xray");
        }

        if (plan.NeedsXray)
            throw new UnsupportedProfileException("Цепочка этого сервера проходит через Xray — одним конфигом sing-box её не выгрузить.");
        var config = SingBoxConfigBuilder.Build(new SingBoxInput
        {
            Settings = settings,
            Active = profile,
            Profiles = all,
            RuleSetDirectory = _paths.GeoDirectory,
            LocalAuth = Auth,
            ClashApiPort = ServerTester.FreePort(),
            ClashApiSecret = SecretStore.NewRandomToken(),
        });
        return (config, "sing-box");
    }
}
