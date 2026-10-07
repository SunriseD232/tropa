using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Tropa.Core.Compatibility;
using Tropa.Core.Generation;
using Tropa.Core.Model;
using Tropa.Core.Security;
using Tropa.Infrastructure.Cores;

namespace Tropa.Infrastructure.Testing;

/// <summary>Результат проверки реальной задержки: миллисекунды или понятная причина.</summary>
public sealed record DelayResult(Guid ProfileId, int? Milliseconds, string? Error);

/// <summary>
/// Проверка реальной задержки (docs/07-testing-diagnostics.md, §1) во временном экземпляре ядра:
/// у каждого сервера свой SOCKS-вход на 127.0.0.1 с паролем, текущее подключение не трогается.
/// </summary>
public static class ServerTester
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static async Task<IReadOnlyList<DelayResult>> TestDelayAsync(
        IReadOnlyList<Profile> profiles, IReadOnlyList<Profile> allProfiles, AppSettings settings,
        CoreLocations locations, string runDirectory, SecretScrubber scrubber, Action<string> log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(settings);
        var results = new List<DelayResult>();
        var testable = new List<Profile>();
        foreach (var p in profiles)
        {
            var issues = ProfileCompat.Issues(p);
            if (issues.Count > 0)
                results.Add(new DelayResult(p.Id, null, "Несовместимые параметры: " + issues[0]));
            else if (p.Transport.Type == TransportType.Xhttp)
                results.Add(new DelayResult(p.Id, null, "XHTTP будет поддержан на этапе 5 (нужно ядро Xray)."));
            else
                testable.Add(p);
        }

        if (testable.Count == 0)
            return results;

        var auth = new LocalAuth("test-" + Storage.SecretStore.NewRandomToken(4), Storage.SecretStore.NewRandomToken());
        var targets = testable.Select(p => new SingBoxConfigBuilder.TestTarget(p, FreePort())).ToList();
        var config = SingBoxConfigBuilder.BuildTest(targets, settings, auth, allProfiles);

        var violations = ConfigGuard.CheckSingBox(config, new GuardPolicy { AllowedDirectories = [runDirectory] });
        if (violations.Count > 0)
            throw new IntegrityException("Тестовый конфиг не прошёл проверку: " + violations[0]);

        await using var core = await CoreProcess.StartSingBoxAsync(locations, config, targets[0].Port, runDirectory, scrubber, log, ct).ConfigureAwait(false);
        using var limiter = new SemaphoreSlim(Math.Clamp(settings.Connection.Parallel, 1, 10));
        var tasks = targets.Select(async t =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await MeasureAsync(t, auth, settings.Connection.TestUrl, ct).ConfigureAwait(false);
            }
            finally
            {
                limiter.Release();
            }
        });
        results.AddRange(await Task.WhenAll(tasks).ConfigureAwait(false));
        return results;
    }

    private static async Task<DelayResult> MeasureAsync(SingBoxConfigBuilder.TestTarget target, LocalAuth auth, string url, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new WebProxy($"socks5://127.0.0.1:{target.Port}") { Credentials = new NetworkCredential(auth.Username, auth.Password) },
            PooledConnectionLifetime = TimeSpan.Zero, // каждый замер — новое соединение через сервер
            ConnectTimeout = RequestTimeout,
        };
        using var client = new HttpClient(handler) { Timeout = RequestTimeout };
        try
        {
            // Первый запрос прогревает DNS и рукопожатие, берём лучший из двух следующих.
            await GetAsync(client, url, ct).ConfigureAwait(false);
            var best = int.MaxValue;
            for (var i = 0; i < 2; i++)
            {
                var sw = Stopwatch.StartNew();
                await GetAsync(client, url, ct).ConfigureAwait(false);
                best = Math.Min(best, (int)sw.ElapsedMilliseconds);
            }

            return new DelayResult(target.Profile.Id, best, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return new DelayResult(target.Profile.Id, null, "Нет ответа за 5 секунд.");
        }
        catch (HttpRequestException ex)
        {
            return new DelayResult(target.Profile.Id, null, ex.HttpRequestError switch
            {
                HttpRequestError.ProxyTunnelError => "Сервер не пропустил запрос (ошибка рукопожатия или ключа).",
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

    internal static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
