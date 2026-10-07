using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Tropa.Core.Model;
using Tropa.Core.Parsing;

namespace Tropa.Infrastructure.Net;

/// <summary>Локальный прокси Тропы, через который можно скачать подписку, если адрес заблокирован.</summary>
public sealed record LocalProxy(int Port, string? Username, string? Password);

public sealed record FetchResult(string Body, IReadOnlyList<KeyValuePair<string, string>> Headers, bool ViaProxy);

/// <summary>Подписку не удалось скачать. Сообщение без URL: в нём секрет.</summary>
public sealed class SubscriptionFetchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Загрузка подписки (docs/02-security.md, §3.2): только HTTPS, не больше 5 МБ, 20 секунд,
/// не больше 5 перенаправлений и только на HTTPS. Системный прокси Windows не используется:
/// это может быть сама Тропа, а прямой запрос должен быть прямым.
/// </summary>
public static class SubscriptionFetcher
{
    public const int MaxBytes = 5 * 1024 * 1024;
    private const int MaxRedirects = 5;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static string UserAgentFor(UserAgentMode mode, string? custom) => mode switch
    {
        UserAgentMode.V2rayN => "v2rayN/7.13.8",
        UserAgentMode.SingBox => "sing-box 1.14.2",
        UserAgentMode.Custom when !string.IsNullOrWhiteSpace(custom) => custom.Trim(),
        _ => "Tropa/" + (typeof(SubscriptionFetcher).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"),
    };

    /// <summary>Сначала напрямую; если не вышло и разрешено — через локальный прокси Тропы.</summary>
    public static async Task<FetchResult> FetchAsync(Subscription sub, LocalProxy? proxy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sub);
        try
        {
            return await FetchOnceAsync(sub, null, ct).ConfigureAwait(false);
        }
        catch (SubscriptionFetchException) when (sub.FetchViaProxy && proxy is not null)
        {
            return await FetchOnceAsync(sub, proxy, ct).ConfigureAwait(false);
        }
    }

    private static async Task<FetchResult> FetchOnceAsync(Subscription sub, LocalProxy? proxy, CancellationToken ct)
    {
        if (!Uri.TryCreate(sub.Url.Reveal(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new SubscriptionFetchException("Адрес подписки должен начинаться с https://.");
        var allowHttp = uri.Scheme == "http"; // только если пользователь сам добавил http-адрес

        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy($"http://127.0.0.1:{proxy.Port}")
            {
                Credentials = proxy.Username is null ? null : new NetworkCredential(proxy.Username, proxy.Password),
            },
            UseCookies = false,
        };
        using var client = new HttpClient(handler) { Timeout = Timeout };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);

        try
        {
            for (var redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd(UserAgentFor(sub.UserAgent, sub.CustomUserAgent));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                if (sub.SendHwid)
                {
                    request.Headers.TryAddWithoutValidation("x-hwid", Hwid.Value);
                    request.Headers.TryAddWithoutValidation("x-device-os", "Windows");
                    request.Headers.TryAddWithoutValidation("x-ver-os", Environment.OSVersion.Version.ToString());
                    request.Headers.TryAddWithoutValidation("x-device-model", "PC");
                }

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (next.Scheme != "https" && !(allowHttp && next.Scheme == "http"))
                        throw new SubscriptionFetchException("Сервер подписки перенаправил на небезопасный адрес. Загрузка отменена.");
                    uri = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new SubscriptionFetchException($"Сервер подписки ответил ошибкой {(int)response.StatusCode}.");

                var body = await ReadLimitedAsync(response.Content, timeout.Token).ConfigureAwait(false);
                var headers = response.Headers.Concat(response.Content.Headers)
                    .Select(h => new KeyValuePair<string, string>(h.Key, string.Join(", ", h.Value)))
                    .ToList();
                return new FetchResult(body, headers, proxy is not null);
            }

            throw new SubscriptionFetchException("Слишком много перенаправлений.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SubscriptionFetchException("Сервер подписки не ответил за 20 секунд.");
        }
        catch (HttpRequestException ex)
        {
            throw new SubscriptionFetchException("Не удалось подключиться к серверу подписки.", ex);
        }
    }

    private static async Task<string> ReadLimitedAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength is > MaxBytes)
            throw new SubscriptionFetchException("Подписка слишком большая.");
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new SubscriptionFetchException("Подписка слишком большая.");
            buffer.Write(chunk, 0, read);
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (DecoderFallbackException)
        {
            throw new SubscriptionFetchException("Подписка не является текстом.");
        }
    }
}

/// <summary>
/// Идентификатор устройства для провайдеров, которые ограничивают число устройств.
/// Не серийные номера: хэш MachineGuid с солью Тропы, по нему нельзя узнать что-то о компьютере.
/// </summary>
public static class Hwid
{
    private static readonly Lazy<string> Lazy = new(Compute);

    public static string Value => Lazy.Value;

    private static string Compute()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var guid = key?.GetValue("MachineGuid") as string ?? Environment.MachineName;
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("Tropa.HWID.v1:" + guid)))[..32];
    }
}
