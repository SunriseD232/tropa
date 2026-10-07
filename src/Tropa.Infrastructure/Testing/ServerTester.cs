using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Tropa.Core.Compatibility;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Security;
using Tropa.Core.Testing;
using Tropa.Infrastructure.Cores;

namespace Tropa.Infrastructure.Testing;

/// <summary>Какие проверки выполнять.</summary>
[Flags]
public enum TestKinds
{
    None = 0,
    Tcp = 1,
    Delay = 2,
    Speed = 4,
    Udp = 8,
    Stability = 16,

    /// <summary>«Тест всех»: всё, кроме долгой проверки стабильности.</summary>
    Standard = Tcp | Delay | Speed | Udp,
}

/// <summary>Промежуточный результат — чтобы таблица обновлялась по мере проверки.</summary>
public sealed record TestProgress(Guid ProfileId, ServerTestResult Result);

/// <summary>Параметры тестов, которые удобно менять в интеграционных тестах.</summary>
public sealed record TesterOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan SpeedTimeout { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan UdpTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public int StabilitySamples { get; init; } = 30;
    public TimeSpan StabilityInterval { get; init; } = TimeSpan.FromSeconds(1);
    public int ParallelSpeedTests { get; init; } = 2;
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// Проверка серверов (docs/07-testing-diagnostics.md, §1) во временном экземпляре ядра: у каждого
/// сервера свой SOCKS-вход на 127.0.0.1 с паролем, плюс вход «напрямую» для TCP-пинга.
/// Текущее подключение не трогается, ядро выходит в сеть мимо туннеля.
/// </summary>
public static partial class ServerTester
{
    public static async Task<IReadOnlyDictionary<Guid, ServerTestResult>> TestAsync(
        IReadOnlyList<Profile> profiles, IReadOnlyList<Profile> allProfiles, AppSettings settings, TestKinds kinds,
        CoreLocations locations, string runDirectory, SecretScrubber scrubber, Action<string> log,
        IProgress<TestProgress>? progress, CancellationToken ct, TesterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        options ??= new TesterOptions();
        var now = options.Time.GetUtcNow();
        var results = new Dictionary<Guid, ServerTestResult>();
        var testable = new List<Profile>();
        foreach (var p in profiles)
        {
            var issues = ProfileCompat.Issues(p);
            if (issues.Count > 0)
                Report(results, progress, p.Id, new ServerTestResult { At = now, Error = "Несовместимые параметры: " + issues[0] });
            else if (p.Transport.Type == TransportType.Xhttp)
                Report(results, progress, p.Id, new ServerTestResult { At = now, Error = "XHTTP будет поддержан на этапе 5 (нужно ядро Xray)." });
            else
                testable.Add(p);
        }

        if (testable.Count == 0)
            return results;

        var auth = new LocalAuth("test-" + Storage.SecretStore.NewRandomToken(4), Storage.SecretStore.NewRandomToken());
        var targets = testable.Select(p => new SingBoxConfigBuilder.TestTarget(p, FreePort())).ToList();
        var pingPort = FreePort();
        var config = SingBoxConfigBuilder.BuildTest(targets, settings, auth, allProfiles, pingPort);

        var violations = ConfigGuard.CheckSingBox(config, new GuardPolicy { AllowedDirectories = [runDirectory] });
        if (violations.Count > 0)
            throw new IntegrityException("Тестовый конфиг не прошёл проверку: " + violations[0]);

        await using var core = await CoreProcess.StartSingBoxAsync(locations, config, pingPort, runDirectory, scrubber, log, ct).ConfigureAwait(false);
        using var limiter = new SemaphoreSlim(Math.Clamp(settings.Connection.Parallel, 1, 10));
        using var speedLimiter = new SemaphoreSlim(Math.Max(1, options.ParallelSpeedTests));
        var tasks = targets.Select(async t =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var r = await TestOneAsync(t, pingPort, auth, settings, kinds, speedLimiter, options, ct).ConfigureAwait(false);
                lock (results)
                    Report(results, progress, t.Profile.Id, r);
            }
            finally
            {
                limiter.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private static void Report(Dictionary<Guid, ServerTestResult> results, IProgress<TestProgress>? progress, Guid id, ServerTestResult r)
    {
        results[id] = r;
        progress?.Report(new TestProgress(id, r));
    }

    private static async Task<ServerTestResult> TestOneAsync(
        SingBoxConfigBuilder.TestTarget t, int pingPort, LocalAuth auth, AppSettings settings, TestKinds kinds,
        SemaphoreSlim speedLimiter, TesterOptions o, CancellationToken ct)
    {
        var r = new ServerTestResult { At = o.Time.GetUtcNow() };

        // 1. TCP-пинг до сервера напрямую. Для цепочек пропускаем: напрямую сервер может быть недоступен по замыслу.
        if (kinds.HasFlag(TestKinds.Tcp) && t.Profile.ChainVia is null)
        {
            var (tcp, error) = await TcpPingAsync(pingPort, auth, t.Profile.Address, t.Profile.Port, o, ct).ConfigureAwait(false);
            r = r with { TcpMs = tcp };
            if (tcp is null)
                return r with { Error = error };
        }

        // 2. Реальная задержка: HTTP-запрос через сервер.
        if (kinds.HasFlag(TestKinds.Delay) || kinds.HasFlag(TestKinds.Speed) || kinds.HasFlag(TestKinds.Stability))
        {
            var (delay, error) = await DelayAsync(t.Port, auth, settings.Connection.TestUrl, o, ct).ConfigureAwait(false);
            r = r with { DelayMs = delay };
            if (delay is null)
                return r with { Error = error };
        }

        // 3. UDP: STUN через сервер.
        if (kinds.HasFlag(TestKinds.Udp) && settings.Connection.UdpTestOn)
            r = r with { UdpOk = await UdpAsync(t.Port, auth, settings.Connection.StunServer, o, ct).ConfigureAwait(false) };

        // 4. Скорость с детектором «заморозки». Не больше двух одновременно, иначе замеры мешают друг другу.
        if (kinds.HasFlag(TestKinds.Speed))
        {
            await speedLimiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var url = SpeedUrl(settings.Connection.SpeedUrl, settings.Connection.SpeedSizeMb);
                var (mbps, frozen, bytes, error) = await SpeedAsync(t.Port, auth, url, o, ct).ConfigureAwait(false);
                r = r with { SpeedMbps = mbps, Frozen = frozen, FrozenAfterBytes = frozen ? bytes : null, Error = error ?? r.Error };
            }
            finally
            {
                speedLimiter.Release();
            }
        }

        // 5. Стабильность: серия запросов раз в секунду.
        if (kinds.HasFlag(TestKinds.Stability))
        {
            var samples = new List<int?>();
            for (var i = 0; i < o.StabilitySamples; i++)
            {
                samples.Add((await DelayAsync(t.Port, auth, settings.Connection.TestUrl, o, ct, warmup: false).ConfigureAwait(false)).Ms);
                await Task.Delay(o.StabilityInterval, o.Time, ct).ConfigureAwait(false);
            }

            var (loss, jitter, median) = ServerHealth.Stability(samples);
            r = r with { Loss = loss, JitterMs = jitter, MedianMs = median };
        }

        return r;
    }

    /// <summary>
    /// TCP-пинг через вход «напрямую»: sing-box отвечает на SOCKS CONNECT только после настоящего
    /// соединения с сервером. Первый замер прогревает DNS, берём лучший из трёх.
    /// </summary>
    private static async Task<(int? Ms, string? Error)> TcpPingAsync(int pingPort, LocalAuth auth, string host, int port, TesterOptions o, CancellationToken ct)
    {
        var best = int.MaxValue;
        string? error = null;
        for (var i = 0; i < 4; i++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.RequestTimeout);
            try
            {
                using var client = await Socks5.OpenAsync(pingPort, auth, timeout.Token).ConfigureAwait(false);
                var sw = Stopwatch.StartNew();
                await Socks5.ConnectAsync(client.GetStream(), host, port, timeout.Token).ConfigureAwait(false);
                if (i > 0)
                    best = Math.Min(best, (int)sw.ElapsedMilliseconds);
            }
            catch (Socks5Exception ex)
            {
                return (null, "Сервер не принимает соединения: " + ex.Message.TrimEnd('.').ToLowerInvariant() + ".");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                error = "Сервер не ответил за " + o.RequestTimeout.TotalSeconds + " с.";
            }
            catch (IOException)
            {
                error = "Соединение с сервером оборвалось.";
            }
        }

        return best == int.MaxValue ? (null, error ?? "Сервер не отвечает.") : (best, null);
    }

    private static HttpClient Client(int port, LocalAuth auth, TimeSpan timeout) => new(new SocketsHttpHandler
    {
        UseProxy = true,
        Proxy = new WebProxy($"socks5://127.0.0.1:{port}") { Credentials = new NetworkCredential(auth.Username, auth.Password) },
        PooledConnectionLifetime = TimeSpan.Zero, // каждый замер — новое соединение через сервер
        ConnectTimeout = timeout,
    })
    { Timeout = timeout };

    private static async Task<(int? Ms, string? Error)> DelayAsync(int port, LocalAuth auth, string url, TesterOptions o, CancellationToken ct, bool warmup = true)
    {
        using var client = Client(port, auth, o.RequestTimeout);
        try
        {
            // Первый запрос прогревает DNS и рукопожатие, берём лучший из двух следующих.
            if (warmup)
                await GetAsync(client, url, ct).ConfigureAwait(false);
            var best = int.MaxValue;
            for (var i = 0; i < (warmup ? 2 : 1); i++)
            {
                var sw = Stopwatch.StartNew();
                await GetAsync(client, url, ct).ConfigureAwait(false);
                best = Math.Min(best, (int)sw.ElapsedMilliseconds);
            }

            return (best, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Через сервер нет ответа за " + o.RequestTimeout.TotalSeconds + " с.");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.HttpRequestError switch
            {
                HttpRequestError.ProxyTunnelError => "Сервер не пропустил запрос: ошибка рукопожатия или ключа.",
                HttpRequestError.ConnectionError => "Не удалось подключиться через сервер.",
                HttpRequestError.SecureConnectionError => "Ошибка TLS на пути к тестовому адресу.",
                _ => "Запрос через сервер не прошёл.",
            });
        }
    }

    private static async Task GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)response.StatusCode >= 500)
            throw new HttpRequestException(HttpRequestError.InvalidResponse, "bad status");
    }

    /// <summary>Скачивание через сервер. «Заморозка»: данные были, но меньше 64 КБ, и затем 8 с тишины.</summary>
    private static async Task<(double? Mbps, bool Frozen, long Bytes, string? Error)> SpeedAsync(
        int port, LocalAuth auth, string url, TesterOptions o, CancellationToken ct)
    {
        using var client = Client(port, auth, Timeout.InfiniteTimeSpan);
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(o.SpeedTimeout);
        var detector = new FreezeDetector(o.Time);
        var start = o.Time.GetTimestamp();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, total.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return (null, false, 0, $"Тестовый файл недоступен (код {(int)response.StatusCode}).");
            await using var stream = await response.Content.ReadAsStreamAsync(total.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                // Каждое чтение ограничено секундой, чтобы проверять детектор во время тишины.
                using var tick = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                tick.CancelAfter(TimeSpan.FromSeconds(1));
                int read;
                try
                {
                    read = await stream.ReadAsync(buffer, tick.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!total.IsCancellationRequested)
                {
                    if (detector.IsFrozen)
                        return (null, true, detector.Bytes, null);
                    continue;
                }

                if (read == 0)
                    break;
                detector.OnData(read);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (detector.IsFrozen)
                return (null, true, detector.Bytes, null);
            if (detector.NothingReceived)
                return (null, false, 0, "Загрузка не началась за " + o.SpeedTimeout.TotalSeconds + " с.");
            // Не успели скачать всё за отведённое время — считаем скорость по тому, что пришло.
        }
        catch (HttpRequestException)
        {
            if (detector.IsFrozen)
                return (null, true, detector.Bytes, null);
            return (null, false, detector.Bytes, "Загрузка через сервер оборвалась.");
        }
        catch (IOException)
        {
            return (null, false, detector.Bytes, "Загрузка через сервер оборвалась.");
        }

        var seconds = Math.Max(0.001, o.Time.GetElapsedTime(start).TotalSeconds);
        return (Math.Round(detector.Bytes * 8 / seconds / 1_000_000, 1), false, detector.Bytes, null);
    }

    /// <summary>STUN через SOCKS5 UDP ASSOCIATE: две попытки по 3 с.</summary>
    private static async Task<bool> UdpAsync(int port, LocalAuth auth, string stunServer, TesterOptions o, CancellationToken ct)
    {
        var colon = stunServer.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(stunServer[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var stunPort))
            return false;
        var stunHost = stunServer[..colon].Trim('[', ']');

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.UdpTimeout);
            try
            {
                using var control = await Socks5.OpenAsync(port, auth, timeout.Token).ConfigureAwait(false);
                var relay = await Socks5.UdpAssociateAsync(control.GetStream(), timeout.Token).ConfigureAwait(false);
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                var (request, id) = Stun.BindingRequest();
                await udp.SendAsync(Socks5.WrapUdp(stunHost, stunPort, request), relay, timeout.Token).ConfigureAwait(false);
                while (true)
                {
                    var received = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                    if (Stun.IsSuccessResponse(Socks5.UnwrapUdp(received.Buffer).Span, id))
                        return true;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is Socks5Exception or SocketException or IOException)
            {
            }
        }

        return false;
    }

    [GeneratedRegex(@"bytes=\d+", RegexOptions.CultureInvariant)]
    private static partial Regex BytesParam();

    /// <summary>Размер тестового файла из настроек подставляется в адрес вида …?bytes=N.</summary>
    internal static string SpeedUrl(string url, int sizeMb) =>
        BytesParam().IsMatch(url) ? BytesParam().Replace(url, "bytes=" + (Math.Clamp(sizeMb, 1, 100) * 1_000_000).ToString(CultureInfo.InvariantCulture)) : url;

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
