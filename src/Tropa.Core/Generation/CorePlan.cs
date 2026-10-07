using Tropa.Core.Compatibility;
using Tropa.Core.Model;

namespace Tropa.Core.Generation;

/// <summary>
/// Какие серверы обслуживает Xray и нужен ли ему вход «напрямую» (docs/05-config-generation.md, §1).
/// sing-box всегда остаётся «фронтом»: TUN, DNS, правила по процессам.
/// </summary>
public sealed record CorePlan(IReadOnlyList<Profile> XrayProfiles, bool XrayDirect)
{
    public bool NeedsXray => XrayProfiles.Count > 0 || XrayDirect;

    /// <param name="used">Серверы, которые попадут в конфиг: активный, группа авто-выбора, цепочки, правила.</param>
    /// <param name="coreFor">
    /// Ядро конкретного сервера (выбор пользователя или найденное проверкой). Auto — по общей настройке.
    /// </param>
    public static CorePlan Make(AppSettings settings, IEnumerable<Profile> used, Func<Guid, CoreChoice>? coreFor = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(used);
        var s = CompatRules.Evaluate(settings).Effective;
        var profiles = used.DistinctBy(p => p.Id).ToList();

        var viaXray = new List<Profile>();
        foreach (var p in profiles)
        {
            // Hysteria2 Тропа запускает только в sing-box, XHTTP есть только в Xray.
            if (p.Protocol == Protocol.Hysteria2)
                continue;
            var choice = coreFor?.Invoke(p.Id) ?? CoreChoice.Auto;
            if (choice == CoreChoice.Auto)
                choice = s.Cores.CoreChoice;
            if (p.Transport.Type == TransportType.Xhttp)
            {
                if (choice == CoreChoice.SingBox)
                    throw new UnsupportedProfileException(
                        $"Сервер «{p.Name}» использует XHTTP, а для него выбрано ядро sing-box. Выберите «Автоматически» или Xray.");
                viaXray.Add(p);
            }
            else if (choice == CoreChoice.Xray)
            {
                viaXray.Add(p);
            }
        }

        // Шум есть только в Xray: прямой UDP пойдёт через его вход «напрямую».
        return new CorePlan(viaXray, s.Dpi.Noise);
    }
}
