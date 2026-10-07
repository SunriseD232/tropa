using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tropa.Core.Model;
using Tropa.Core.Security;

namespace Tropa.Core.Diagnostics;

/// <summary>Что попадает в отчёт. Собирает интерфейс, отчёт пользователь видит до сохранения.</summary>
public sealed record ReportInput
{
    public required string AppVersion { get; init; }
    public required string CoreVersions { get; init; }
    public required string WindowsVersion { get; init; }
    public required AppSettings Settings { get; init; }
    public required IReadOnlyList<Profile> Profiles { get; init; }
    public required IReadOnlyList<StepResult> Steps { get; init; }
    public required IReadOnlyList<string> Log { get; init; }
    public required SecretScrubber Scrubber { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Отчёт диагностики (docs/07-testing-diagnostics.md, §3): Markdown без секретов. Адреса серверов
/// заменяются на server-1…N, имена серверов не выводятся, всё проходит через SecretScrubber.
/// </summary>
public static class DiagnosticReport
{
    public const int LogLines = 200;

    public static string Build(ReportInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var s = input.Settings;
        var sb = new StringBuilder();
        var inv = CultureInfo.InvariantCulture;
        sb.AppendLine("# Отчёт диагностики Тропы");
        sb.AppendLine();
        sb.AppendLine(inv, $"- Время: {input.At:yyyy-MM-dd HH:mm zzz}");
        sb.AppendLine(inv, $"- Тропа: {input.AppVersion}");
        sb.AppendLine(inv, $"- Ядра: {input.CoreVersions}");
        sb.AppendLine(inv, $"- Windows: {input.WindowsVersion}");
        sb.AppendLine(inv, $"- Режим: {s.Connection.Mode}, стек TUN: {s.Connection.TunStack}, строгий маршрут: {OnOff(s.Connection.StrictRoute)}");
        sb.AppendLine(inv, $"- Основа правил: {s.Routing.Preset}, QUIC блокируется: {OnOff(s.Routing.BlockQuic)}, UDP через сервер: {OnOff(s.Routing.UdpProxy)}");
        sb.AppendLine(inv, $"- DNS: удалённый {Host(s.Dns.RemoteDns)}, FakeIP {OnOff(s.Dns.Fakeip)}, перехват {OnOff(s.Dns.DnsHijack)}");
        sb.AppendLine(inv, $"- Обход DPI: {s.Dpi.DpiPreset}, фрагментация {OnOff(s.Dpi.Fragment)}, шум {OnOff(s.Dpi.Noise)}, Mux {OnOff(s.Dpi.Mux)}");
        sb.AppendLine(inv, $"- Kill switch: {OnOff(s.General.KillSwitch)}, ядро: {s.Cores.CoreChoice}");
        sb.AppendLine();

        sb.AppendLine("## Серверы");
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (input.Profiles.Count == 0)
            sb.AppendLine("- нет серверов");
        foreach (var p in input.Profiles)
        {
            if (!aliases.ContainsKey(p.Address))
                aliases[p.Address] = "server-" + (aliases.Count + 1).ToString(inv);
            sb.AppendLine(inv, $"- {aliases[p.Address]}: {p.Protocol}, {p.Transport.Type}, {p.Security.Type}{(p.Flow == VlessFlow.XtlsRprxVision ? ", Vision" : "")}{(p.ChainVia is null ? "" : ", цепочка")}");
        }

        sb.AppendLine();
        sb.AppendLine("## Правила");
        if (s.Routing.Rules.Count == 0)
            sb.AppendLine("- нет своих правил");
        foreach (var r in s.Routing.Rules)
        {
            var what = string.Join(", ", r.Match.Processes.Concat(r.Match.DomainSuffixes));
            sb.AppendLine(inv, $"- {(r.Enabled ? "" : "(выкл) ")}{what}: TCP {r.Tcp.Kind}, UDP {r.Udp.Kind}");
        }

        sb.AppendLine();
        sb.AppendLine("## Шаги");
        foreach (var step in input.Steps)
        {
            sb.AppendLine(inv, $"- [{step.Status}] {step.Title}");
            if (step.Details is not null)
                sb.AppendLine(inv, $"  {step.Details}");
        }

        sb.AppendLine();
        sb.AppendLine(inv, $"## Журнал (последние {LogLines} строк)");
        sb.AppendLine("```");
        foreach (var line in input.Log.TakeLast(LogLines))
            sb.AppendLine(line);
        sb.AppendLine("```");

        // Сначала адреса серверов (длинные раньше коротких, чтобы не заменить часть другого адреса), затем секреты.
        var text = sb.ToString();
        foreach (var (address, alias) in aliases.OrderByDescending(a => a.Key.Length))
            text = Regex.Replace(text, @"(?<![\w.-])" + Regex.Escape(address) + @"(?![\w-]|\.\w)", alias,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return input.Scrubber.Scrub(text);
    }

    private static string OnOff(bool v) => v ? "вкл" : "выкл";

    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;
}
