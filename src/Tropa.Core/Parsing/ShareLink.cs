using System.Collections.Frozen;
using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Tropa.Core.Model;
using Tropa.Core.Security;

namespace Tropa.Core.Parsing;

/// <summary>
/// Разбор и сборка ссылок vless://, vmess://, trojan:// (docs/04-domain-model.md, §3).
/// Разбор не проверяет совместимость параметров между собой — это делает CompatRules,
/// чтобы профиль из подписки импортировался с пометкой, а не терялся молча.
/// </summary>
public static class ShareLink
{
    public static ParseResult Parse(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
            return ParseResult.Fail("Пустая ссылка.");
        link = link.Trim();
        if (link.Length > Validation.MaxLinkLength)
            return ParseResult.Fail("Ссылка слишком длинная.");

        var schemeEnd = link.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
            return ParseResult.Fail("Это не ссылка на сервер.");

        var scheme = link[..schemeEnd].ToLowerInvariant();
        var rest = link[(schemeEnd + 3)..];
        var warnings = new List<string>();
        try
        {
            var profile = scheme switch
            {
                "vless" => ParseVless(rest, warnings),
                "trojan" => ParseTrojan(rest, warnings),
                "vmess" => ParseVmess(rest, warnings),
                _ => throw new LinkFormatException($"Протокол «{scheme}» пока не поддерживается."),
            };
            return ParseResult.Ok(profile, warnings);
        }
        catch (LinkFormatException ex)
        {
            return ParseResult.Fail(ex.Message);
        }
    }

    // ---------------- VLESS ----------------

    private static Profile ParseVless(string rest, List<string> warnings)
    {
        var url = UrlParts.Split(rest);
        return Create(Protocol.Vless, url.Host, url.Port, url.UserInfo, url.Fragment, url.Query, warnings);
    }

    /// <summary>
    /// Общая точка создания профиля из разобранных частей. Через неё проходят и ссылки,
    /// и JSON-импорт, поэтому проверки значений в одном месте.
    /// </summary>
    internal static Profile Create(
        Protocol protocol, string? host, string? port, string? credential, string? name,
        IReadOnlyDictionary<string, string> q, List<string> warnings, string? vmessCipher = null)
    {
        if (protocol == Protocol.Vless)
        {
            var encryption = q.GetValueOrDefault("encryption");
            if (!string.IsNullOrEmpty(encryption) && encryption != "none")
                throw new LinkFormatException("VLESS Encryption пока не поддерживается.");
        }

        var address = Validation.Address(host);
        var portNumber = Validation.Port(port);
        return new Profile
        {
            Name = NameSanitizer.Sanitize(name, $"{address}:{portNumber}"),
            Protocol = protocol,
            Address = address,
            Port = portNumber,
            Credential = new Secret(protocol == Protocol.Trojan ? Validation.Password(credential) : Validation.Uuid(credential)),
            Flow = protocol == Protocol.Vless ? ParseFlow(q.GetValueOrDefault("flow")) : VlessFlow.None,
            VmessCipher = vmessCipher ?? "auto",
            Transport = ParseTransport(q, warnings),
            Security = ParseSecurity(q, protocol == Protocol.Trojan ? "tls" : "none", warnings),
        };
    }

    private static VlessFlow ParseFlow(string? flow) => flow switch
    {
        null or "" or "none" => VlessFlow.None,
        "xtls-rprx-vision" or "xtls-rprx-vision-udp443" => VlessFlow.XtlsRprxVision,
        _ => throw new LinkFormatException($"Flow «{flow}» не поддерживается."),
    };

    // ---------------- Trojan ----------------

    private static Profile ParseTrojan(string rest, List<string> warnings)
    {
        var url = UrlParts.Split(rest);
        return Create(Protocol.Trojan, url.Host, url.Port, url.UserInfo, url.Fragment, url.Query, warnings);
    }

    // ---------------- VMess ----------------

    internal static readonly FrozenSet<string> VmessCiphers =
        new[] { "auto", "aes-128-gcm", "chacha20-poly1305", "none", "zero" }.ToFrozenSet(StringComparer.Ordinal);

    private static Profile ParseVmess(string rest, List<string> warnings)
    {
        if (rest.Contains('?', StringComparison.Ordinal) || rest.Contains('@', StringComparison.Ordinal))
            throw new LinkFormatException("Этот вариант vmess-ссылки не поддерживается, нужен формат vmess://<base64 JSON>.");

        var json = Base64Text.Decode(rest.Split('#')[0])
            ?? throw new LinkFormatException("vmess-ссылка: неверный base64.");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            throw new LinkFormatException("vmess-ссылка: внутри не JSON.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new LinkFormatException("vmess-ссылка: внутри не JSON-объект.");

            var aid = Field(root, "aid");
            if (!string.IsNullOrEmpty(aid) && aid != "0")
                throw new LinkFormatException("VMess с alterId > 0 (устаревший режим без AEAD) не поддерживается.");

            var cipher = Field(root, "scy") ?? "auto";
            if (!VmessCiphers.Contains(cipher))
            {
                warnings.Add($"Неизвестный шифр VMess «{cipher}», будет использован auto.");
                cipher = "auto";
            }

            // Приводим поля vmess к параметрам query, чтобы разобрать транспорт и TLS тем же кодом.
            var net = Field(root, "net") ?? "tcp";
            var q = new Dictionary<string, string>(StringComparer.Ordinal) { ["type"] = net };
            AddIf(q, "headerType", Field(root, "type"));
            AddIf(q, "host", Field(root, "host"));
            AddIf(q, net == "grpc" ? "serviceName" : "path", Field(root, "path"));
            AddIf(q, "security", Field(root, "tls"));
            AddIf(q, "sni", Field(root, "sni"));
            AddIf(q, "alpn", Field(root, "alpn"));
            AddIf(q, "fp", Field(root, "fp"));
            AddIf(q, "allowInsecure", Field(root, "allowInsecure"));

            return Create(Protocol.Vmess, Field(root, "add"), Field(root, "port"), Field(root, "id"), Field(root, "ps"), q, warnings, cipher);
        }
    }

    // В vmess-JSON числа и строки встречаются вперемешку.
    private static string? Field(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => null,
        };
    }

    private static void AddIf(Dictionary<string, string> q, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            q[key] = value;
    }

    // ---------------- Общие параметры ----------------

    private static TransportSettings ParseTransport(IReadOnlyDictionary<string, string> q, List<string> warnings)
    {
        var type = (q.GetValueOrDefault("type") ?? "tcp").ToLowerInvariant();
        switch (type)
        {
            case "tcp" or "raw":
                var header = q.GetValueOrDefault("headerType");
                if (!string.IsNullOrEmpty(header) && header != "none")
                    throw new LinkFormatException($"Маскировка TCP «{header}» не поддерживается.");
                return TransportSettings.Tcp;

            case "ws" or "httpupgrade":
                return new TransportSettings
                {
                    Type = type == "ws" ? TransportType.Ws : TransportType.HttpUpgrade,
                    Path = Validation.Optional(q.GetValueOrDefault("path"), "путь") ?? "/",
                    Host = Validation.Optional(q.GetValueOrDefault("host"), "host"),
                };

            case "grpc":
                if (q.GetValueOrDefault("mode") == "multi")
                    warnings.Add("Режим gRPC multi не поддерживается, будет использован обычный.");
                return new TransportSettings
                {
                    Type = TransportType.Grpc,
                    ServiceName = Validation.Optional(q.GetValueOrDefault("serviceName"), "имя сервиса gRPC"),
                };

            case "xhttp" or "splithttp":
                if (!string.IsNullOrEmpty(q.GetValueOrDefault("extra")))
                    warnings.Add("Дополнительные параметры XHTTP (extra) пока не поддерживаются и проигнорированы.");
                return new TransportSettings
                {
                    Type = TransportType.Xhttp,
                    Path = Validation.Optional(q.GetValueOrDefault("path"), "путь") ?? "/",
                    Host = Validation.Optional(q.GetValueOrDefault("host"), "host"),
                    XhttpMode = q.GetValueOrDefault("mode") switch
                    {
                        null or "" or "auto" => XhttpMode.Auto,
                        "packet-up" => XhttpMode.PacketUp,
                        "stream-up" => XhttpMode.StreamUp,
                        "stream-one" => XhttpMode.StreamOne,
                        var m => throw new LinkFormatException($"Режим XHTTP «{m}» не поддерживается."),
                    },
                };

            default:
                throw new LinkFormatException($"Транспорт «{type}» не поддерживается.");
        }
    }

    private static SecuritySettings ParseSecurity(IReadOnlyDictionary<string, string> q, string defaultSecurity, List<string> warnings)
    {
        var security = q.GetValueOrDefault("security");
        if (string.IsNullOrEmpty(security))
            security = defaultSecurity;

        if (!string.IsNullOrEmpty(q.GetValueOrDefault("ech")))
            warnings.Add("ECH пока не поддерживается и проигнорирован.");

        switch (security.ToLowerInvariant())
        {
            case "none":
                return SecuritySettings.None;

            case "tls":
                var insecure = Validation.Flag(q.GetValueOrDefault("allowInsecure")) || Validation.Flag(q.GetValueOrDefault("insecure"));
                if (insecure)
                    warnings.Add("У сервера отключена проверка сертификата (allowInsecure): трафик может прочитать посредник.");
                return new SecuritySettings
                {
                    Type = SecurityType.Tls,
                    Sni = Validation.Sni(q.GetValueOrDefault("sni") ?? q.GetValueOrDefault("peer")),
                    Alpn = Validation.Alpn(q.GetValueOrDefault("alpn"), warnings),
                    Fingerprint = Validation.Fingerprint(q.GetValueOrDefault("fp"), warnings),
                    AllowInsecure = insecure,
                };

            case "reality":
                var sni = Validation.Sni(q.GetValueOrDefault("sni"))
                    ?? throw new LinkFormatException("Для Reality не указано имя сайта-маски (sni).");
                return new SecuritySettings
                {
                    Type = SecurityType.Reality,
                    Sni = sni,
                    Fingerprint = Validation.Fingerprint(q.GetValueOrDefault("fp"), warnings) ?? "chrome",
                    Reality = new RealitySettings
                    {
                        PublicKey = Validation.RealityKey(q.GetValueOrDefault("pbk")),
                        ShortId = Validation.RealitySid(q.GetValueOrDefault("sid")),
                        SpiderX = Validation.Optional(q.GetValueOrDefault("spx"), "spiderX"),
                    },
                };

            default:
                throw new LinkFormatException($"Тип безопасности «{security}» не поддерживается.");
        }
    }

    // ---------------- Сборка ссылки ----------------

    /// <summary>Собирает ссылку из профиля. Ссылка содержит секрет — показывать только по явному действию.</summary>
    public static string Build(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Protocol switch
        {
            Protocol.Vless => BuildUrl("vless", profile, VlessQuery(profile)),
            Protocol.Trojan => BuildUrl("trojan", profile, CommonQuery(profile)),
            Protocol.Vmess => BuildVmess(profile),
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
    }

    private static List<KeyValuePair<string, string>> VlessQuery(Profile p)
    {
        var q = new List<KeyValuePair<string, string>> { new("encryption", "none") };
        if (p.Flow == VlessFlow.XtlsRprxVision)
            q.Add(new("flow", "xtls-rprx-vision"));
        q.AddRange(CommonQuery(p));
        return q;
    }

    private static List<KeyValuePair<string, string>> CommonQuery(Profile p)
    {
        var q = new List<KeyValuePair<string, string>>();
        var t = p.Transport;
        q.Add(new("type", t.Type switch
        {
            TransportType.Tcp => "tcp",
            TransportType.Ws => "ws",
            TransportType.HttpUpgrade => "httpupgrade",
            TransportType.Grpc => "grpc",
            TransportType.Xhttp => "xhttp",
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        }));
        AddPair(q, "path", t.Path);
        AddPair(q, "host", t.Host);
        AddPair(q, "serviceName", t.ServiceName);
        if (t.Type == TransportType.Xhttp && t.XhttpMode != XhttpMode.Auto)
            q.Add(new("mode", XhttpModeName(t.XhttpMode)));

        var s = p.Security;
        q.Add(new("security", s.Type switch
        {
            SecurityType.None => "none",
            SecurityType.Tls => "tls",
            SecurityType.Reality => "reality",
            _ => throw new ArgumentOutOfRangeException(nameof(p)),
        }));
        AddPair(q, "sni", s.Sni);
        AddPair(q, "alpn", s.Alpn);
        AddPair(q, "fp", s.Fingerprint);
        if (s.AllowInsecure)
            q.Add(new("allowInsecure", "1"));
        if (s.Reality is { } r)
        {
            q.Add(new("pbk", r.PublicKey));
            AddPair(q, "sid", r.ShortId);
            AddPair(q, "spx", r.SpiderX);
        }

        return q;
    }

    internal static string XhttpModeName(XhttpMode mode) => mode switch
    {
        XhttpMode.Auto => "auto",
        XhttpMode.PacketUp => "packet-up",
        XhttpMode.StreamUp => "stream-up",
        XhttpMode.StreamOne => "stream-one",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private static void AddPair(List<KeyValuePair<string, string>> q, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            q.Add(new(key, value));
    }

    private static string BuildUrl(string scheme, Profile p, List<KeyValuePair<string, string>> query)
    {
        var host = p.Address.Contains(':', StringComparison.Ordinal) ? $"[{p.Address}]" : p.Address;
        var qs = string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return string.Create(CultureInfo.InvariantCulture,
            $"{scheme}://{Uri.EscapeDataString(p.Credential.Reveal())}@{host}:{p.Port}?{qs}#{Uri.EscapeDataString(p.Name)}");
    }

    private static string BuildVmess(Profile p)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("v", "2");
            w.WriteString("ps", p.Name);
            w.WriteString("add", p.Address);
            w.WriteString("port", p.Port.ToString(CultureInfo.InvariantCulture));
            w.WriteString("id", p.Credential.Reveal());
            w.WriteString("aid", "0");
            w.WriteString("scy", p.VmessCipher);
            var t = p.Transport;
            w.WriteString("net", t.Type switch
            {
                TransportType.Tcp => "tcp",
                TransportType.Ws => "ws",
                TransportType.HttpUpgrade => "httpupgrade",
                TransportType.Grpc => "grpc",
                TransportType.Xhttp => "xhttp",
                _ => throw new ArgumentOutOfRangeException(nameof(p)),
            });
            w.WriteString("type", "none");
            w.WriteString("host", t.Host ?? "");
            w.WriteString("path", (t.Type == TransportType.Grpc ? t.ServiceName : t.Path) ?? "");
            w.WriteString("tls", p.Security.Type == SecurityType.Tls ? "tls" : "");
            w.WriteString("sni", p.Security.Sni ?? "");
            w.WriteString("alpn", p.Security.Alpn ?? "");
            w.WriteString("fp", p.Security.Fingerprint ?? "");
            w.WriteEndObject();
        }

        return "vmess://" + Convert.ToBase64String(ms.ToArray());
    }
}

/// <summary>Ручной разбор «userinfo@host:port?query#fragment» без нормализаций System.Uri.</summary>
internal sealed record UrlParts(string UserInfo, string Host, string Port, IReadOnlyDictionary<string, string> Query, string? Fragment)
{
    public static UrlParts Split(string rest)
    {
        string? fragment = null;
        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            fragment = Uri.UnescapeDataString(rest[(hash + 1)..]);
            rest = rest[..hash];
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        var qm = rest.IndexOf('?', StringComparison.Ordinal);
        if (qm >= 0)
        {
            foreach (var pair in rest[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                var key = eq < 0 ? pair : pair[..eq];
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..]);
                query.TryAdd(key, value); // при повторе ключа побеждает первое значение
            }

            rest = rest[..qm];
        }

        rest = rest.TrimEnd('/');
        var at = rest.LastIndexOf('@');
        if (at <= 0)
            throw new LinkFormatException("В ссылке нет ключа доступа (часть до «@»).");
        var userInfo = Uri.UnescapeDataString(rest[..at]);
        var hostPort = rest[(at + 1)..];

        string host, port;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= hostPort.Length || hostPort[close + 1] != ':')
                throw new LinkFormatException("Неверный формат IPv6-адреса или порта.");
            host = hostPort[..(close + 1)];
            port = hostPort[(close + 2)..];
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon <= 0)
                throw new LinkFormatException("В ссылке не указан порт.");
            host = hostPort[..colon];
            port = hostPort[(colon + 1)..];
        }

        return new UrlParts(userInfo, host, port, query, fragment);
    }
}

/// <summary>Декодирование base64 в обоих алфавитах, с паддингом и без, с пробелами и переносами.</summary>
internal static class Base64Text
{
    public static string? Decode(string input)
    {
        var sb = new StringBuilder(input.Length + 3);
        foreach (var ch in input)
        {
            if (char.IsWhiteSpace(ch))
                continue;
            sb.Append(ch switch { '-' => '+', '_' => '/', _ => ch });
        }

        var s = sb.ToString().TrimEnd('=');
        if (s.Length == 0)
            return null;
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');

        var bytes = new byte[Base64.GetMaxDecodedFromUtf8Length(s.Length)];
        if (!Convert.TryFromBase64String(s, bytes, out var written))
            return null;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
