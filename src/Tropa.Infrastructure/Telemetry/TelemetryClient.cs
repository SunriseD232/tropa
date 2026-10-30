using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tropa.Core.Telemetry;
using Tropa.Infrastructure.Net;
using Tropa.Infrastructure.Storage;

namespace Tropa.Infrastructure.Telemetry;

/// <summary>
/// Куда отправлять журнал (tools/telemetry.config.json, вшит в сборку). Пустой адрес — журнал выключен
/// в этой сборке. Ключ — не секрет пользователя: он одинаков у всех и только отсекает случайные запросы.
/// </summary>
public sealed record TelemetryConfig(string Url, string Token)
{
    public static TelemetryConfig Embedded { get; } = Load();

    public static TelemetryConfig None { get; } = new("", "");

    public bool Enabled => Url.Length > 0;

    private static TelemetryConfig Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tropa.telemetry.config.json");
        if (stream is null)
            return None;
        var o = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException("Настройки журнала повреждены.");
        var url = (string?)o["url"] ?? "";
        if (url.Length > 0 && ValidateUrl(url) is { } error)
            throw new InvalidDataException(error);
        return new TelemetryConfig(url, (string?)o["token"] ?? "");
    }

    /// <summary>Только https; http — лишь на 127.0.0.1 (тесты).</summary>
    public static string? ValidateUrl(string url) =>
        !Uri.TryCreate(url, UriKind.Absolute, out var u) ? "Адрес журнала неверный."
        : u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback) ? null
        : "Адрес журнала должен быть https.";
}

/// <summary>
/// Журнал для разработчика (ADR-050). События сначала дописываются в файл-очередь
/// (%LOCALAPPDATA%\Tropa\telemetry\queue.jsonl) — так они переживают сбой и отсутствие интернета —
/// а раз в минуту (после ошибки — через 5 секунд) уходят пакетом POST-запросом на сервер.
/// Напрямую, а если не вышло и Тропа подключена — через её локальный вход.
/// </summary>
public sealed class TelemetryClient : IAsyncDisposable
{
    private const int MaxBatch = 500;
    private const long MaxQueueBytes = 4 * 1024 * 1024;
    private const int MaxField = 2000;

    private readonly TelemetryConfig _config;
    private readonly Func<DateTimeOffset> _now;
    private readonly string _queuePath;
    private readonly Lock _fileLock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;

    public TelemetryClient(string directory, TelemetryConfig config, Func<DateTimeOffset> now)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        Directory.CreateDirectory(directory);
        _queuePath = Path.Combine(directory, "queue.jsonl");
        InstallId = LoadInstallId(Path.Combine(directory, "id.txt"));
    }

    /// <summary>Случайный идентификатор этой установки: не связан ни с компьютером, ни с учётной записью.</summary>
    public string InstallId { get; }

    /// <summary>Пользователь не выключил журнал в настройках.</summary>
    public bool Allowed { get; set; } = true;

    public bool Active => _config.Enabled && Allowed;

    public string Name { get; set; } = "";

    public string AppVersion { get; set; } = "";

    /// <summary>Локальный вход Тропы, если она подключена, — запасной путь до сервера.</summary>
    public Func<LocalProxy?>? Proxy { get; set; }

    /// <summary>Чем закончилась последняя отправка; null — ещё не отправляли.</summary>
    public string? LastResult { get; private set; }

    public event EventHandler? StatusChanged;

    /// <summary>Дописывает событие в очередь. Ничего не делает, если журнал выключен.</summary>
    public void Record(string code, IReadOnlyDictionary<string, string>? data = null, bool urgent = false)
    {
        if (!Active)
            return;
        var trimmed = data?.ToDictionary(kv => kv.Key, kv => kv.Value is { Length: > MaxField } v ? v[..MaxField] + "…" : kv.Value ?? "", StringComparer.Ordinal);
        var line = JsonSerializer.Serialize(new TelemetryEvent(_now().ToUnixTimeMilliseconds(), code, trimmed), TelemetryJsonContext.Default.TelemetryEvent);
        try
        {
            lock (_fileLock)
            {
                File.AppendAllText(_queuePath, line + "\n", Encoding.UTF8);
                if (new FileInfo(_queuePath).Length > MaxQueueBytes)
                    DropOldest(File.ReadAllLines(_queuePath, Encoding.UTF8).Length / 2);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Журнал не важнее работы Тропы.
            return;
        }

        if (urgent)
            _wake.TrySetResult();
    }

    /// <summary>Сколько событий ждут отправки.</summary>
    public int Pending
    {
        get
        {
            lock (_fileLock)
            {
                try
                {
                    return File.Exists(_queuePath) ? File.ReadLines(_queuePath).Count(l => l.Length > 0) : 0;
                }
                catch (IOException)
                {
                    return 0;
                }
            }
        }
    }

    /// <summary>Пользователь выключил журнал — неотправленное стирается.</summary>
    public void Clear()
    {
        lock (_fileLock)
        {
            try
            {
                File.Delete(_queuePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Фоновая отправка: раз в минуту или раньше, если записана ошибка.</summary>
    public void Start(TimeSpan? interval = null, TimeSpan? urgentDelay = null)
    {
        if (_loop is not null)
            return;
        var every = interval ?? TimeSpan.FromMinutes(1);
        var soon = urgentDelay ?? TimeSpan.FromSeconds(5);
        _loop = Task.Run(async () =>
        {
            var ct = _stop.Token;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var wake = _wake.Task;
                    if (await Task.WhenAny(Task.Delay(every, ct), wake).ConfigureAwait(false) == wake)
                        await Task.Delay(soon, ct).ConfigureAwait(false);
                    _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    await FlushAsync(ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    /// <summary>Отправляет очередь пакетами по 500. true — всё отправлено (или нечего).</summary>
    public async Task<bool> FlushAsync(CancellationToken ct = default)
    {
        if (!Active)
            return false;
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (true)
            {
                List<string> lines;
                lock (_fileLock)
                {
                    if (!File.Exists(_queuePath))
                        return true;
                    lines = File.ReadLines(_queuePath, Encoding.UTF8).Take(MaxBatch).ToList();
                }

                if (lines.Count == 0)
                    return true;
                var events = new List<TelemetryEvent>(lines.Count);
                foreach (var line in lines)
                {
                    try
                    {
                        if (line.Length > 0 && JsonSerializer.Deserialize(line, TelemetryJsonContext.Default.TelemetryEvent) is { } e)
                            events.Add(e);
                    }
                    catch (JsonException)
                    {
                        // Строка оборвалась при сбое — пропускаем.
                    }
                }

                if (events.Count > 0)
                {
                    var body = JsonSerializer.Serialize(new TelemetryBatch { Id = InstallId, Name = Name, App = AppVersion, Events = events },
                        TelemetryJsonContext.Default.TelemetryBatch);
                    if (await SendAsync(body, ct).ConfigureAwait(false) is { } error)
                    {
                        SetResult($"Не отправлено: {error} В очереди: {Pending}.");
                        return false;
                    }
                }

                lock (_fileLock)
                    DropOldest(lines.Count);
                SetResult($"Отправлено {_now():HH:mm}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetResult("Не отправлено: " + ex.Message);
            return false;
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private void SetResult(string text)
    {
        LastResult = text;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Убирает первые <paramref name="count"/> строк; остальное переписывается атомарно.</summary>
    private void DropOldest(int count)
    {
        if (!File.Exists(_queuePath))
            return;
        var rest = File.ReadLines(_queuePath, Encoding.UTF8).Skip(count).Where(l => l.Length > 0).ToList();
        if (rest.Count == 0)
            File.Delete(_queuePath);
        else
            AtomicFile.WriteAllText(_queuePath, string.Join("\n", rest) + "\n");
    }

    private async Task<string?> SendAsync(string body, CancellationToken ct)
    {
        var error = await PostAsync(body, null, ct).ConfigureAwait(false);
        if (error is not null && Proxy?.Invoke() is { } proxy)
            error = await PostAsync(body, proxy, ct).ConfigureAwait(false);
        return error;
    }

    private async Task<string?> PostAsync(string body, LocalProxy? proxy, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy($"http://127.0.0.1:{proxy.Port}")
            {
                Credentials = proxy.Username is null ? null : new NetworkCredential(proxy.Username, proxy.Password),
            },
            UseCookies = false,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_config.Url))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (_config.Token.Length > 0)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Token);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            return code is >= 200 and < 300 ? null : $"сервер ответил кодом {code}.";
        }
        catch (HttpRequestException)
        {
            return "нет связи с сервером журнала.";
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return "сервер журнала не ответил за 20 секунд.";
        }
    }

    private static string LoadInstallId(string path)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path).Trim() is { Length: 32 } id && id.All(Uri.IsHexDigit))
                return id;
            var fresh = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            AtomicFile.WriteAllText(path, fresh);
            return fresh;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Последняя попытка отправить (выход, ошибка) — недолго: не держим закрытие Тропы.
        if (Active && _loop is not null)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await FlushAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
        _sendGate.Dispose();
    }
}
