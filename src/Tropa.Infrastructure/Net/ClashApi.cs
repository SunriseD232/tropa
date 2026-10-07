using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Tropa.Infrastructure.Net;

/// <summary>Скорость за последнюю секунду, байт/с.</summary>
public readonly record struct TrafficSample(long Up, long Down);

/// <summary>Клиент API статистики sing-box. Только 127.0.0.1 и только с секретом (ConfigGuard).</summary>
public sealed class ClashApi(int port, string secret) : IDisposable
{
    private readonly HttpClient _client = new(new SocketsHttpHandler { UseProxy = false })
    {
        BaseAddress = new Uri($"http://127.0.0.1:{port}/"),
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", secret) },
    };

    /// <summary>Поток замеров трафика: sing-box присылает по строке JSON в секунду.</summary>
    public async IAsyncEnumerable<TrafficSample> TrafficAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var response = await _client.GetAsync("traffic", HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
                yield break;
            if (line.Length == 0)
                continue;
            TrafficSample? sample = null;
            try
            {
                using var doc = JsonDocument.Parse(line);
                sample = new TrafficSample(doc.RootElement.GetProperty("up").GetInt64(), doc.RootElement.GetProperty("down").GetInt64());
            }
            catch (JsonException)
            {
            }
            catch (KeyNotFoundException)
            {
            }

            if (sample is { } s)
                yield return s;
        }
    }

    public void Dispose() => _client.Dispose();
}
