using System.Globalization;
using Tropa.Core.Model;

namespace Tropa.Core.Diagnostics;

/// <summary>Системный прокси Windows (WinINet, HKCU), как его видит диагностика.</summary>
public sealed record ProxySnapshot(bool Enabled, string? Server, string? AutoConfigUrl, bool AppliedByUs);

public static partial class Diagnosis
{
    /// <summary>
    /// Порты на этом компьютере, куда указывает строка ProxyServer: «127.0.0.1:10880»,
    /// «http=127.0.0.1:10880;https=localhost:10880;socks=[::1]:10881», «http://127.0.0.1:10880».
    /// </summary>
    public static IReadOnlyList<int> LoopbackProxyPorts(string? server)
    {
        var ports = new List<int>();
        if (string.IsNullOrWhiteSpace(server))
            return ports;
        foreach (var raw in server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw;
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
                part = part[(eq + 1)..];
            var scheme = part.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
                part = part[(scheme + 3)..];
            part = part.TrimEnd('/');
            var colon = part.LastIndexOf(':');
            if (colon <= 0)
                continue;
            var host = part[..colon].Trim('[', ']');
            if (!int.TryParse(part[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
                continue;
            if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "::1" || host.StartsWith("127.", StringComparison.Ordinal))
            {
                if (!ports.Contains(port))
                    ports.Add(port);
            }
        }

        return ports;
    }

    /// <summary>
    /// Прокси включён и указывает на наш вход, а включали его не мы в этом сеансе — значит, он остался
    /// от прошлого режима «Только браузеры» (сбой, переустановка, потерянный журнал). Пока Тропа
    /// работает, вход требует пароль, и программы с этим прокси (браузеры, Discord) не открывают ничего;
    /// после отключения Тропы — тем более.
    /// </summary>
    public static bool IsStaleOwnProxy(ProxySnapshot proxy, IEnumerable<int> ownPorts)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(ownPorts);
        return proxy.Enabled && !proxy.AppliedByUs && LoopbackProxyPorts(proxy.Server).Intersect(ownPorts).Any();
    }

    /// <summary>Шаг «Системный прокси»: не уводит ли он программы мимо туннеля.</summary>
    public static StepResult SystemProxy(ProxySnapshot proxy, CaptureMode mode, bool connected, int ownPort)
    {
        ArgumentNullException.ThrowIfNull(proxy);
        var pac = !string.IsNullOrWhiteSpace(proxy.AutoConfigUrl);
        if (!proxy.Enabled && !pac)
            return new StepResult("sysProxy", StepStatus.Ok, "Системный прокси выключен");

        // Включила сама Тропа (режим «Только браузеры») и вернёт при отключении.
        if (proxy.AppliedByUs)
            return new StepResult("sysProxy", StepStatus.Ok, "Системный прокси — Тропа", connected ? "Режим «Только браузеры»." : null);

        const string action = "Параметры Windows → Сеть и Интернет → Прокси: выключите «Использовать прокси-сервер» и «Использовать сценарий настройки», затем переподключитесь.";
        if (pac && !proxy.Enabled)
        {
            return new StepResult("sysProxy", StepStatus.Warn, "Включён сценарий настройки прокси (PAC)",
                "Браузеры и Discord спрашивают у сценария, куда идти, и могут уйти мимо Тропы.", action);
        }

        var loopback = LoopbackProxyPorts(proxy.Server);
        if (loopback.Contains(ownPort) && !proxy.AppliedByUs)
        {
            return new StepResult("sysProxy", StepStatus.Bad, $"Остался старый системный прокси Тропы ({proxy.Server})",
                "Браузеры и Discord идут в вход Тропы без пароля, и он их не пускает: страницы и Discord грузятся долго или не грузятся. Тропа снимает такой прокси при подключении.",
                "Переподключитесь. Если не помогло — " + action);
        }

        var where = loopback.Count > 0
            ? "другую программу на этом компьютере (часто v2rayN или другой клиент обхода)"
            : "внешний прокси-сервер";
        var tunNote = mode == CaptureMode.Tun
            ? "Браузеры и Discord отправляют трафик туда, а не в туннель Тропы, — соединение медленное или не устанавливается."
            : "Тропа его заменит при подключении, а после отключения вернёт.";
        return new StepResult("sysProxy", mode == CaptureMode.Tun ? StepStatus.Warn : StepStatus.Ok,
            $"Системный прокси указывает на {where}: {proxy.Server}", tunNote, mode == CaptureMode.Tun ? action : null);
    }
}
