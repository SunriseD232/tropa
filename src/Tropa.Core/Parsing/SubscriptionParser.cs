using System.Text.Json;
using Tropa.Core.Model;

namespace Tropa.Core.Parsing;

public enum SubscriptionFormat { Empty, LinkList, Base64LinkList, SingBoxJson, XrayJson }

public sealed record ImportedProfile(Profile Profile, IReadOnlyList<string> Warnings, int Position);

/// <summary>Ошибка импорта. Сырой текст строки не хранится: в нём могут быть секреты.</summary>
public sealed record ImportError(int Position, string Message);

public sealed record SubscriptionParseResult(
    SubscriptionFormat Format,
    IReadOnlyList<ImportedProfile> Profiles,
    IReadOnlyList<ImportError> Errors,
    int SkippedUnsupported);

/// <summary>
/// Разбор тела подписки (docs/04-domain-model.md, §2): JSON sing-box/Xray, base64 или список ссылок.
/// Из JSON берутся только описания серверов — чужой конфиг никогда не исполняется (ADR-008).
/// </summary>
public static class SubscriptionParser
{
    public const int MaxProfiles = 5000;
    public const int MaxBodyChars = 5 * 1024 * 1024;

    public static SubscriptionParseResult Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return new(SubscriptionFormat.Empty, [], [], 0);
        if (body.Length > MaxBodyChars)
            return new(SubscriptionFormat.Empty, [], [new ImportError(0, "Подписка слишком большая.")], 0);

        var trimmed = body.Trim().TrimStart('﻿');
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return JsonOutboundImport.Parse(trimmed);

        // Если в тексте уже есть «://», это список ссылок; иначе пробуем base64.
        if (trimmed.Contains("://", StringComparison.Ordinal))
            return ParseLines(trimmed, SubscriptionFormat.LinkList);

        var decoded = Base64Text.Decode(trimmed);
        if (decoded is not null && decoded.Contains("://", StringComparison.Ordinal))
            return ParseLines(decoded, SubscriptionFormat.Base64LinkList);

        return new(SubscriptionFormat.Empty, [], [new ImportError(0, "Не удалось распознать формат подписки.")], 0);
    }

    private static SubscriptionParseResult ParseLines(string text, SubscriptionFormat format)
    {
        var profiles = new List<ImportedProfile>();
        var errors = new List<ImportError>();
        var skipped = 0;
        var position = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            position++;

            if (!IsSupportedScheme(line))
            {
                skipped++;
                continue;
            }

            if (profiles.Count >= MaxProfiles)
            {
                errors.Add(new ImportError(position, $"Больше {MaxProfiles} серверов, остальные пропущены."));
                break;
            }

            var result = ShareLink.Parse(line);
            if (result.Success)
                profiles.Add(new ImportedProfile(result.Profile!, result.Warnings, position));
            else
                errors.Add(new ImportError(position, result.Error!));
        }

        return new(format, profiles, errors, skipped);
    }

    private static bool IsSupportedScheme(string line) =>
        line.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Импорт outbound-ов из JSON-конфигов sing-box и Xray. Всё остальное в конфиге
/// (inbound-ы, пути к файлам, API, логи) игнорируется.
/// </summary>
internal static class JsonOutboundImport
{
    public static SubscriptionParseResult Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32, CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return new(SubscriptionFormat.Empty, [], [new ImportError(0, "Подписка похожа на JSON, но разобрать её не удалось.")], 0);
        }

        using (doc)
        {
            var profiles = new List<ImportedProfile>();
            var errors = new List<ImportError>();
            var skipped = 0;
            var position = 0;
            var format = SubscriptionFormat.Empty;

            // Некоторые панели отдают массив полных конфигов Xray с полем "remarks".
            var configs = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];

            foreach (var config in configs)
            {
                if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("outbounds", out var outbounds)
                    || outbounds.ValueKind != JsonValueKind.Array)
                    continue;
                var remarks = Str(config, "remarks");

                foreach (var ob in outbounds.EnumerateArray())
                {
                    if (ob.ValueKind != JsonValueKind.Object)
                        continue;

                    var isSingBox = ob.TryGetProperty("type", out _);
                    var kind = isSingBox ? Str(ob, "type") : Str(ob, "protocol");
                    if (kind is not ("vless" or "vmess" or "trojan"))
                    {
                        // direct, block, dns, selector и т.п. — служебные, не ошибка.
                        if (kind is not ("direct" or "block" or "dns" or "freedom" or "blackhole" or "selector" or "urltest" or "loopback"))
                            skipped++;
                        continue;
                    }

                    position++;
                    if (profiles.Count >= SubscriptionParser.MaxProfiles)
                    {
                        errors.Add(new ImportError(position, $"Больше {SubscriptionParser.MaxProfiles} серверов, остальные пропущены."));
                        break;
                    }

                    format = isSingBox ? SubscriptionFormat.SingBoxJson : SubscriptionFormat.XrayJson;
                    var warnings = new List<string>();
                    try
                    {
                        var profile = isSingBox ? FromSingBox(ob, kind, warnings) : FromXray(ob, kind, remarks, warnings);
                        profiles.Add(new ImportedProfile(profile, warnings, position));
                    }
                    catch (LinkFormatException ex)
                    {
                        errors.Add(new ImportError(position, ex.Message));
                    }
                }
            }

            return new(format, profiles, errors, skipped);
        }
    }

    private static Profile FromSingBox(JsonElement ob, string kind, List<string> warnings)
    {
        var protocol = ToProtocol(kind);
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        Put(q, "flow", Str(ob, "flow"));

        if (ob.TryGetProperty("transport", out var tr) && tr.ValueKind == JsonValueKind.Object)
        {
            var type = Str(tr, "type");
            Put(q, "type", type);
            Put(q, "path", Str(tr, "path"));
            Put(q, "serviceName", Str(tr, "service_name"));
            Put(q, "host", Str(tr, "host") ?? (tr.TryGetProperty("headers", out var h) ? Str(h, "Host") : null));
        }

        if (ob.TryGetProperty("tls", out var tls) && tls.ValueKind == JsonValueKind.Object && Bool(tls, "enabled"))
        {
            var reality = tls.TryGetProperty("reality", out var r) && r.ValueKind == JsonValueKind.Object && Bool(r, "enabled");
            q["security"] = reality ? "reality" : "tls";
            Put(q, "sni", Str(tls, "server_name"));
            Put(q, "alpn", Join(tls, "alpn"));
            if (Bool(tls, "insecure"))
                q["allowInsecure"] = "1";
            if (tls.TryGetProperty("utls", out var utls) && utls.ValueKind == JsonValueKind.Object)
                Put(q, "fp", Str(utls, "fingerprint"));
            if (reality)
            {
                Put(q, "pbk", Str(r, "public_key"));
                Put(q, "sid", Str(r, "short_id"));
            }
        }
        else
        {
            q["security"] = "none";
        }

        var credential = protocol == Protocol.Trojan ? Str(ob, "password") : Str(ob, "uuid");
        if (protocol == Protocol.Vmess && Num(ob, "alter_id") is { } aid && aid != "0")
            throw new LinkFormatException("VMess с alterId > 0 не поддерживается.");

        return ShareLink.Create(protocol, Str(ob, "server"), Num(ob, "server_port"), credential, Str(ob, "tag"), q, warnings,
            protocol == Protocol.Vmess ? VmessCipher(Str(ob, "security"), warnings) : null);
    }

    private static Profile FromXray(JsonElement ob, string kind, string? remarks, List<string> warnings)
    {
        var protocol = ToProtocol(kind);
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        string? address = null, port = null, credential = null, cipher = null;

        if (ob.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
        {
            if (protocol == Protocol.Trojan)
            {
                var server = First(settings, "servers");
                address = Str(server, "address");
                port = Num(server, "port");
                credential = Str(server, "password");
            }
            else
            {
                var vnext = First(settings, "vnext");
                address = Str(vnext, "address");
                port = Num(vnext, "port");
                var user = First(vnext, "users");
                credential = Str(user, "id");
                Put(q, "flow", Str(user, "flow"));
                Put(q, "encryption", protocol == Protocol.Vless ? Str(user, "encryption") : null);
                if (protocol == Protocol.Vmess)
                {
                    if (Num(user, "alterId") is { } aid && aid != "0")
                        throw new LinkFormatException("VMess с alterId > 0 не поддерживается.");
                    cipher = VmessCipher(Str(user, "security"), warnings);
                }
            }
        }

        if (ob.TryGetProperty("streamSettings", out var ss) && ss.ValueKind == JsonValueKind.Object)
        {
            var network = Str(ss, "network") ?? "tcp";
            q["type"] = network;
            var security = Str(ss, "security") ?? "none";
            q["security"] = security;

            JsonElement tls = default;
            if (security == "tls" && ss.TryGetProperty("tlsSettings", out tls))
            {
                Put(q, "sni", Str(tls, "serverName"));
                Put(q, "alpn", Join(tls, "alpn"));
                Put(q, "fp", Str(tls, "fingerprint"));
                if (Bool(tls, "allowInsecure"))
                    q["allowInsecure"] = "1";
            }
            else if (security == "reality" && ss.TryGetProperty("realitySettings", out tls))
            {
                Put(q, "sni", Str(tls, "serverName"));
                Put(q, "fp", Str(tls, "fingerprint"));
                Put(q, "pbk", Str(tls, "publicKey") ?? Str(tls, "password"));
                Put(q, "sid", Str(tls, "shortId"));
                Put(q, "spx", Str(tls, "spiderX"));
            }

            var transportKey = network switch
            {
                "ws" => "wsSettings",
                "grpc" => "grpcSettings",
                "xhttp" or "splithttp" => "xhttpSettings",
                "httpupgrade" => "httpupgradeSettings",
                "tcp" or "raw" => "tcpSettings",
                _ => null,
            };
            if (transportKey is not null && ss.TryGetProperty(transportKey, out var t) && t.ValueKind == JsonValueKind.Object)
            {
                Put(q, "path", Str(t, "path"));
                Put(q, "serviceName", Str(t, "serviceName"));
                Put(q, "mode", network is "xhttp" or "splithttp" ? Str(t, "mode") : null);
                Put(q, "host", Str(t, "host") ?? (t.TryGetProperty("headers", out var h) ? Str(h, "Host") : null));
                if (t.TryGetProperty("header", out var header))
                    Put(q, "headerType", Str(header, "type"));
            }
        }
        else
        {
            q["security"] = "none";
        }

        var name = Str(ob, "tag");
        if (string.IsNullOrEmpty(name) || name == "proxy")
            name = remarks;
        return ShareLink.Create(protocol, address, port, credential, name, q, warnings, cipher);
    }

    private static Protocol ToProtocol(string kind) => kind switch
    {
        "vless" => Protocol.Vless,
        "vmess" => Protocol.Vmess,
        _ => Protocol.Trojan,
    };

    private static string VmessCipher(string? value, List<string> warnings)
    {
        if (string.IsNullOrEmpty(value))
            return "auto";
        if (ShareLink.VmessCiphers.Contains(value))
            return value;
        warnings.Add($"Неизвестный шифр VMess «{value}», будет использован auto.");
        return "auto";
    }

    private static JsonElement First(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var arr)
        && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0
            ? arr[0]
            : default;

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? Num(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.String => v.GetString(),
            _ => null,
        };
    }

    private static bool Bool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static string? Join(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.String)
            return v.GetString();
        if (v.ValueKind != JsonValueKind.Array)
            return null;
        return string.Join(',', v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()));
    }

    private static void Put(Dictionary<string, string> q, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            q[key] = value;
    }
}
