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
    public static CorePlan Make(AppSettings settings, IEnumerable<Profile> used)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(used);
        var s = CompatRules.Evaluate(settings).Effective;
        var profiles = used.DistinctBy(p => p.Id).ToList();

        List<Profile> viaXray = s.Cores.CoreChoice switch
        {
            CoreChoice.Xray => profiles,
            _ => profiles.Where(p => p.Transport.Type == TransportType.Xhttp).ToList(),
        };

        if (s.Cores.CoreChoice == CoreChoice.SingBox && viaXray.Count > 0)
            throw new UnsupportedProfileException(
                $"Сервер «{viaXray[0].Name}» использует XHTTP, а в настройках выбрано «Всегда sing-box». Выберите «Автоматически».");

        // Шум есть только в Xray: прямой UDP пойдёт через его вход «напрямую».
        return new CorePlan(viaXray, s.Dpi.Noise);
    }
}
