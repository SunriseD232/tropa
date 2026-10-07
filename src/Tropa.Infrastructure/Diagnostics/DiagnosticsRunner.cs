using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Tropa.Core.Compatibility;
using Tropa.Core.Diagnostics;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Security;
using Tropa.Core.Testing;
using Tropa.Infrastructure.Cores;
using Tropa.Infrastructure.Testing;

namespace Tropa.Infrastructure.Diagnostics;

/// <summary>Всё, что нужно диагностике от приложения.</summary>
public sealed record DiagnosticsInput
{
    public required AppSettings Settings { get; init; }
    public required Profile? Active { get; init; }
    public required IReadOnlyList<Profile> Profiles { get; init; }
    public required bool Connected { get; init; }
    public required CoreLocations Locations { get; init; }
    public required string RunDirectory { get; init; }
    public required SecretScrubber Scrubber { get; init; }
    public required Action<string> Log { get; init; }
    public TesterOptions? TesterOptions { get; init; }
}

/// <summary>Ответ провайдера и туннеля для одного домена (инструмент «Проверка домена»).</summary>
public sealed record DomainCheck(string Domain, DnsAnswer? Provider, DnsAnswer? Tunnel, DnsVerdict Verdict, string? ProviderDns);

/// <summary>
/// Девять шагов диагностики (docs/07-testing-diagnostics.md, §2). Шаги независимы: сбой одного
/// не останавливает остальные. В сеть «напрямую» ходит временное ядро мимо туннеля, через сервер —
/// временный вход через активный сервер; текущее подключение не трогается.
/// </summary>
public static class DiagnosticsRunner
{
    public static readonly IReadOnlyList<(string Key, string Title)> Steps =
    [
        ("netDirect", "Интернет без прокси"),
        ("clock", "Часы"),
        ("tcping", "Сервер отвечает"),
        ("realityHs", "Рукопожатие"),
        ("dataFlow", "Данные идут"),
        ("dnsPoison", "Подмена DNS"),
        ("udpTest", "UDP"),
        ("leaks", "Утечки"),
        ("otherVpn", "Другой VPN"),
    ];

    /// <summary>Домены для проверки подмены DNS: заблокированные в России сервисы.</summary>
    public static readonly IReadOnlyList<string> PoisonDomains = ["discord.com", "youtube.com", "x.com", "rutracker.org"];

    private const string DirectProbeUrl = "https://ya.ru/";
    private const string TunnelDns = "1.1.1.1";
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(8);

    public static async Task<IReadOnlyList<StepResult>> RunAsync(DiagnosticsInput input, IProgress<StepResult>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var results = Steps.ToDictionary(s => s.Key, s => new StepResult(s.Key, StepStatus.Pending, s.Title));
        void Set(StepResult r)
        {
            lock (results)
                results[r.Key] = r;
            progress?.Report(r);
        }

        foreach (var r in results.Values.ToList())
            progress?.Report(r);

        var effective = CompatRules.Evaluate(input.Settings, input.Active).Effective;
        var adapters = Adapters();

        // 1, 2, 6: сеть напрямую, часы и DNS — одним временным ядром.
        Set(results["netDirect"] with { Status = StepStatus.Running });
        try
        {
            await ServerTester.ProbeAsync(input.Active, input.Profiles, input.Settings, input.Locations, input.RunDirectory,
                input.Scrubber, input.Log, async (ports, token) =>
                {
                    var directOk = await DirectAsync(ports, Set, token).ConfigureAwait(false);
                    if (!directOk)
                        Set(new StepResult("dnsPoison", StepStatus.Skipped, "Подмена DNS не проверялась", "Нет интернета без прокси."));
                    else if (ports.ServerPort is null)
                        Set(new StepResult("dnsPoison", StepStatus.Skipped, "Подмена DNS не проверялась", "Не выбран сервер, сравнивать не с чем."));
                    else
                    {
                        Set(results["dnsPoison"] with { Status = StepStatus.Running });
                        var dns = ProviderDns();
                        var checks = new List<(string, DnsVerdict)>();
                        foreach (var domain in PoisonDomains)
                        {
                            var check = await CheckDomainAsync(ports, dns, domain, token).ConfigureAwait(false);
                            checks.Add((domain, check.Verdict));
                        }

                        Set(Diagnosis.DnsPoison(checks));
                    }

                    return true;
                }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or UnsupportedProfileException or IOException)
        {
            var message = input.Scrubber.Scrub(ex.Message);
            Set(new StepResult("netDirect", StepStatus.Bad, "Не удалось запустить проверочное ядро", message));
            Set(new StepResult("clock", StepStatus.Skipped, "Часы не проверялись"));
            Set(new StepResult("dnsPoison", StepStatus.Skipped, "Подмена DNS не проверялась"));
        }

        // 3, 4, 5, 7: сервер — тем же тестом, что и на экране серверов, файл 10 МБ.
        await ServerStepsAsync(input, Set, ct).ConfigureAwait(false);

        // 8, 9: настройки, адаптеры и процессы.
        Set(Diagnosis.Leaks(effective, input.Connected, adapters));
        Set(Diagnosis.OtherVpn(Diagnosis.ForeignVpnAdapters(adapters), ForeignProcesses(input.Locations)));

        lock (results)
            return Steps.Select(s => results[s.Key]).ToList();
    }

    private static async Task<bool> DirectAsync(ProbePorts ports, Action<StepResult> set, CancellationToken ct)
    {
        using var client = ServerTester.ProbeClient(ports.DirectPort, ports.Auth, StepTimeout, allowRedirect: false);
        try
        {
            using var response = await client.GetAsync(DirectProbeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var location = response.Headers.Location;
            if (code is >= 300 and < 400 && location is { IsAbsoluteUri: true } && !IsYandex(location.Host))
            {
                set(new StepResult("netDirect", StepStatus.Warn, "Провайдер перенаправляет на свою страницу",
                    "Похоже на страницу входа в сеть (Wi-Fi в кафе, гостинице) или на неоплаченный интернет.",
                    "Откройте любой сайт в браузере без Тропы и пройдите вход."));
            }
            else
            {
                set(new StepResult("netDirect", StepStatus.Ok, "Интернет работает"));
            }

            if (response.Headers.Date is { } date)
                set(Diagnosis.Clock(DateTimeOffset.UtcNow - date));
            else
                set(new StepResult("clock", StepStatus.Skipped, "Часы не проверены", "Сайт не сообщил время."));
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            set(new StepResult("netDirect", StepStatus.Bad, "Нет интернета даже без прокси",
                "Проблема не в Тропе и не в сервере: компьютер не выходит в сеть.",
                "Проверьте кабель или Wi-Fi, перезагрузите роутер, проверьте баланс у провайдера."));
            set(new StepResult("clock", StepStatus.Skipped, "Часы не проверены", "Нет интернета без прокси."));
            return false;
        }
    }

    private static bool IsYandex(string host) =>
        host.Equals("ya.ru", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".ya.ru", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("yandex.ru", StringComparison.OrdinalIgnoreCase);

    private static async Task ServerStepsAsync(DiagnosticsInput input, Action<StepResult> set, CancellationToken ct)
    {
        string[] keys = ["tcping", "realityHs", "dataFlow", "udpTest"];
        if (input.Active is not { } active)
        {
            foreach (var k in keys)
                set(new StepResult(k, StepStatus.Skipped, Steps.First(s => s.Key == k).Title, "Не выбран сервер."));
            return;
        }

        foreach (var k in keys)
            set(new StepResult(k, StepStatus.Running, Steps.First(s => s.Key == k).Title));

        ServerTestResult r;
        try
        {
            var settings = input.Settings with { Connection = input.Settings.Connection with { SpeedSizeMb = 10 } };
            var results = await ServerTester.TestAsync([active], input.Profiles, settings, TestKinds.Standard, input.Locations,
                input.RunDirectory, input.Scrubber, input.Log, null, ct, input.TesterOptions).ConfigureAwait(false);
            r = results[active.Id];
        }
        catch (Exception ex) when (ex is CoreStartException or IntegrityException or IOException)
        {
            var message = input.Scrubber.Scrub(ex.Message);
            foreach (var k in keys)
                set(new StepResult(k, StepStatus.Bad, "Проверка сервера не запустилась", message));
            return;
        }

        foreach (var step in ServerSteps(active, r))
            set(step);
    }

    /// <summary>Шаги 3, 4, 5, 7 из результата теста сервера.</summary>
    internal static IEnumerable<StepResult> ServerSteps(Profile active, ServerTestResult r)
    {
        var tcpFailed = r.TcpMs is null && active.ChainVia is null;
        if (active.ChainVia is not null)
            yield return new StepResult("tcping", StepStatus.Skipped, "Сервер отвечает", "Сервер в цепочке: напрямую до него не достучаться по замыслу.");
        else if (tcpFailed)
        {
            yield return new StepResult("tcping", StepStatus.Bad, "Сервер не отвечает", r.Error,
                "Сервер выключен или его адрес заблокирован. Обновите подписку или выберите другой сервер.");
        }
        else
            yield return new StepResult("tcping", StepStatus.Ok, $"Сервер отвечает: {r.TcpMs} мс");

        if (tcpFailed)
        {
            foreach (var k in new[] { ("realityHs", "Рукопожатие"), ("dataFlow", "Данные идут"), ("udpTest", "UDP") })
                yield return new StepResult(k.Item1, StepStatus.Skipped, k.Item2, "Сервер не отвечает.");
            yield break;
        }

        if (r.DelayMs is null)
        {
            var reality = active.Security.Type == SecurityType.Reality;
            yield return new StepResult("realityHs", StepStatus.Bad, "Рукопожатие не проходит", r.Error,
                reality
                    ? "Проверьте часы (шаг 2), обновите подписку: возможно, сменились ключ или SNI."
                    : "Обновите подписку: возможно, сменились пароль, путь или SNI.");
            yield return new StepResult("dataFlow", StepStatus.Skipped, "Данные идут", "Нет рукопожатия.");
            yield return new StepResult("udpTest", StepStatus.Skipped, "UDP", "Нет рукопожатия.");
            yield break;
        }

        yield return new StepResult("realityHs", StepStatus.Ok, $"Рукопожатие проходит: {r.DelayMs} мс");

        if (r.Frozen)
        {
            yield return new StepResult("dataFlow", StepStatus.Bad, "Данные не идут",
                $"Пришло {r.FrozenAfterBytes / 1024 ?? 0} КБ, затем тишина. Так провайдеры ограничивают IP хостингов.",
                "Выберите сервер у другого хостинга или включите обход DPI в «Правилах».");
        }
        else if (r.SpeedMbps is { } mbps)
        {
            yield return mbps < ServerHealth.SlowSpeedMbps
                ? new StepResult("dataFlow", StepStatus.Warn, $"Данные идут медленно: {mbps:0.#} Мбит/с", "Видео в высоком качестве может подтормаживать.")
                : new StepResult("dataFlow", StepStatus.Ok, $"Данные идут: {mbps:0.#} Мбит/с");
        }
        else
        {
            yield return new StepResult("dataFlow", StepStatus.Warn, "Скорость не измерена", r.Error);
        }

        yield return r.UdpOk switch
        {
            true => new StepResult("udpTest", StepStatus.Ok, "UDP проходит", "Голос в Discord и звонки будут работать."),
            false => new StepResult("udpTest", StepStatus.Warn, "UDP не проходит",
                "Сайты будут работать, а голос в Discord, звонки и игры — нет. Сервер или провайдер не пропускает UDP.",
                "Попробуйте другой сервер."),
            null => new StepResult("udpTest", StepStatus.Skipped, "UDP не проверялся", "Проверка UDP выключена в настройках."),
        };
    }

    /// <summary>Инструмент «Проверка домена»: ответ DNS провайдера и ответ через туннель.</summary>
    public static Task<DomainCheck> CheckDomainAsync(DiagnosticsInput input, string domain, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ServerTester.ProbeAsync(input.Active, input.Profiles, input.Settings, input.Locations, input.RunDirectory,
            input.Scrubber, input.Log, (ports, token) => CheckDomainAsync(ports, ProviderDns(), domain, token), ct);
    }

    private static async Task<DomainCheck> CheckDomainAsync(ProbePorts ports, IPAddress? providerDns, string domain, CancellationToken ct)
    {
        DnsAnswer? provider = null;
        DnsAnswer? tunnel = null;
        if (providerDns is not null)
            provider = await QueryUdpAsync(ports.DirectPort, ports.Auth, providerDns, domain, ct).ConfigureAwait(false);
        if (ports.ServerPort is { } sp)
            tunnel = await QueryTcpAsync(sp, ports.Auth, TunnelDns, domain, ct).ConfigureAwait(false);
        return new DomainCheck(domain, provider, tunnel, Diagnosis.CompareDns(provider, tunnel), providerDns?.ToString());
    }

    /// <summary>DNS провайдера: первый IPv4-сервер DNS у работающего адаптера с основным шлюзом (не наш TUN).</summary>
    private static IPAddress? ProviderDns()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var props = ni.GetIPProperties();
            if (props.UnicastAddresses.Any(u => u.Address.Equals(Diagnosis.OwnTunAddress))
                || !props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                continue;
            var dns = props.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (dns is not null)
                return dns;
        }

        return null;
    }

    internal static async Task<DnsAnswer?> QueryUdpAsync(int socksPort, LocalAuth auth, IPAddress server, string domain, CancellationToken ct, int dnsPort = 53)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var control = await Socks5.OpenAsync(socksPort, auth, timeout.Token).ConfigureAwait(false);
            var relay = await Socks5.UdpAssociateAsync(control.GetStream(), timeout.Token).ConfigureAwait(false);
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var id = (ushort)RandomNumberGenerator.GetInt32(1, 65535);
            await udp.SendAsync(Socks5.WrapUdp(server.ToString(), dnsPort, DnsMessage.BuildQuery(domain, DnsMessage.TypeA, id)), relay, timeout.Token).ConfigureAwait(false);
            while (true)
            {
                var received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                if (DnsMessage.Parse(Socks5.UnwrapUdp(received.Buffer).Span, id) is { } answer)
                    return answer;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or Socks5Exception or SocketException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal static async Task<DnsAnswer?> QueryTcpAsync(int socksPort, LocalAuth auth, string server, string domain, CancellationToken ct, int dnsPort = 53)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(StepTimeout);
        try
        {
            using var client = await Socks5.OpenAsync(socksPort, auth, timeout.Token).ConfigureAwait(false);
            var stream = client.GetStream();
            await Socks5.ConnectAsync(stream, server, dnsPort, timeout.Token).ConfigureAwait(false);
            var id = (ushort)RandomNumberGenerator.GetInt32(1, 65535);
            await stream.WriteAsync(DnsMessage.WithTcpLength(DnsMessage.BuildQuery(domain, DnsMessage.TypeA, id)), timeout.Token).ConfigureAwait(false);
            var lengthBytes = new byte[2];
            await stream.ReadExactlyAsync(lengthBytes, timeout.Token).ConfigureAwait(false);
            var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(lengthBytes)];
            await stream.ReadExactlyAsync(body, timeout.Token).ConfigureAwait(false);
            return DnsMessage.Parse(body, id);
        }
        catch (Exception ex) when (ex is OperationCanceledException or Socks5Exception or SocketException or IOException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static IReadOnlyList<AdapterInfo> Adapters()
    {
        var list = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            var props = ni.GetIPProperties();
            list.Add(new AdapterInfo(ni.Name, ni.Description, ni.OperationalStatus == OperationalStatus.Up,
                props.UnicastAddresses.Select(u => u.Address).ToList(),
                props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any))));
        }

        return list;
    }

    /// <summary>Запущенные клиенты обхода. sing-box и xray считаются чужими, только если это не наши файлы.</summary>
    private static List<string> ForeignProcesses(CoreLocations locations)
    {
        var ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var core in new[] { "sing-box", "xray" })
        {
            ours.Add(Path.GetFullPath(CoreProcess.ExecutablePath(locations, core)));
        }

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (Diagnosis.ForeignClients.Contains(p.ProcessName))
                {
                    found.Add(p.ProcessName);
                    continue;
                }

                if (!p.ProcessName.Equals("sing-box", StringComparison.OrdinalIgnoreCase) && !p.ProcessName.Equals("xray", StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    // Ядра нашей службы работают под SYSTEM: путь к ним прочитать нельзя, их не считаем.
                    var path = p.MainModule?.FileName;
                    if (path is not null && !ours.Contains(Path.GetFullPath(path)) && !path.Contains(@"\Tropa\", StringComparison.OrdinalIgnoreCase))
                        found.Add($"{p.ProcessName} ({Path.GetFileName(Path.GetDirectoryName(path))})");
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }

        return [.. found];
    }
}
