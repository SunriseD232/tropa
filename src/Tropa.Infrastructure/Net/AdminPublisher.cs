using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Tropa.Infrastructure.Net;

/// <summary>Документ для друзей не опубликован. Сообщение без адреса и ключа: это секреты.</summary>
public sealed class AdminPublishException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Публикация документа для друзей (ADR-048): HTTP PUT по адресу администратора с ключом в заголовке
/// Authorization: Bearer. Только HTTPS, без перенаправлений, 20 секунд. Тот же адрес друзья добавляют
/// как подписку (GET). Системный прокси Windows не используется; при неудаче напрямую и подключённой
/// Тропе — повтор через её локальный вход, как у подписок.
/// </summary>
public static class AdminPublisher
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Адрес публикации: только https://, без логина и пароля в адресе.</summary>
    public static string? ValidateUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Укажите адрес.";
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return "Адрес должен начинаться с https://.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "Логин и пароль в адресе не нужны: Тропа передаёт ключ отдельно.";
        return null;
    }

    public static async Task PublishAsync(string url, string key, string json, LocalProxy? proxy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (ValidateUrl(url) is { } error)
            throw new AdminPublishException(error);
        try
        {
            await PutAsync(new Uri(url.Trim()), key, json, null, ct).ConfigureAwait(false);
        }
        catch (AdminPublishException) when (proxy is not null)
        {
            await PutAsync(new Uri(url.Trim()), key, json, proxy, ct).ConfigureAwait(false);
        }
    }

    private static async Task PutAsync(Uri uri, string key, string json, LocalProxy? proxy, CancellationToken ct)
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
        using var client = new HttpClient(handler) { Timeout = Timeout };
        using var request = new HttpRequestMessage(HttpMethod.Put, uri)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is 401 or 403)
                throw new AdminPublishException("Сервер не принял ключ публикации.");
            if (code is < 200 or >= 300)
                throw new AdminPublishException($"Сервер ответил кодом {code}.");
        }
        catch (HttpRequestException ex)
        {
            throw new AdminPublishException("Не удалось связаться с сервером публикации.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new AdminPublishException("Сервер публикации не ответил за 20 секунд.", ex);
        }
    }
}
