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

    /// <summary>
    /// Текущие соединения ядра (для сводки UDP, событие 3.7). Через сервер — всё, что ушло не в
    /// «direct» и не отклонено. Адреса не читаем: в журнал идут только имя программы и счётчики.
    /// </summary>
    public async Task<IReadOnlyList<Tropa.Core.Telemetry.CoreConnection>> ConnectionsAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var response = await _client.GetAsync("connections", timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
        var list = new List<Tropa.Core.Telemetry.CoreConnection>();
        if (!doc.RootElement.TryGetProperty("connections", out var items) || items.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var c in items.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("metadata", out var m) || m.ValueKind != JsonValueKind.Object)
                continue;
            var chain = c.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array && chains.GetArrayLength() > 0
                ? chains[0].GetString()
                : null;
            var process = m.TryGetProperty("processPath", out var path) && path.GetString() is { Length: > 0 } pp ? Path.GetFileName(pp) : null;
            var port = m.TryGetProperty("destinationPort", out var dp) && int.TryParse(dp.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
            list.Add(new Tropa.Core.Telemetry.CoreConnection(
                Id: c.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                Udp: m.TryGetProperty("network", out var net) && net.GetString() == "udp",
                ViaServer: chain is not null and not ("direct" or "block" or "reject"),
                Process: process,
                DestinationPort: port,
                Upload: c.TryGetProperty("upload", out var up) && up.TryGetInt64(out var u) ? u : 0,
                Download: c.TryGetProperty("download", out var down) && down.TryGetInt64(out var d) ? d : 0));
        }

        return list;
    }

    /// <summary>Переключает selector на выход <paramref name="outbound"/> без переподключения.</summary>
    public async Task SelectAsync(string selector, string outbound, CancellationToken ct)
    {
        using var body = new StringContent("{\"name\":" + JsonSerializer.Serialize(outbound) + "}", System.Text.Encoding.UTF8, "application/json");
        using var response = await _client.PutAsync("proxies/" + Uri.EscapeDataString(selector), body, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
