using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tropa.Core.Compatibility;
using Tropa.Core.Model;

namespace Tropa.Core.Generation;

/// <summary>Сервер, который обслуживает Xray, и локальный порт его SOCKS-входа.</summary>
public sealed record XrayTarget(Profile Profile, int Port);

/// <summary>
/// Генератор конфига Xray (docs/05-config-generation.md, §3). Xray работает только «за SOCKS»:
/// у каждого сервера свой вход на 127.0.0.1 с паролем, а ещё — вход «напрямую» с фрагментацией
/// и шумом. Всё остальное (TUN, DNS, правила) делает sing-box.
///
/// Внимание: Xray молча игнорирует неизвестные поля, поэтому «xray -test» опечатки не ловит.
/// Схема проверяется сквозными тестами с настоящим Xray-сервером.
/// </summary>
public static class XrayConfigBuilder
{

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, NewLine = "\n" };

    public static string Build(IReadOnlyList<XrayTarget> targets, AppSettings settings, LocalAuth auth)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(auth);
        if (targets.Count == 0)
            throw new ArgumentException("Xray нечего обслуживать.", nameof(targets));
        var s = CompatRules.Evaluate(settings).Effective;

        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();

        foreach (var (profile, port) in targets)
        {
            var issues = ProfileCompat.Issues(profile);
            if (issues.Count > 0)
                throw new UnsupportedProfileException($"Сервер «{profile.Name}» нельзя запустить: {string.Join(" ", issues)}");
            if (profile.ChainVia is not null)
                throw new UnsupportedProfileException($"Цепочка для сервера «{profile.Name}» с ядром Xray пока не поддерживается.");

            var tag = "srv-" + profile.Id.ToString("N")[..8];
            var inTag = "in-" + tag;
            inbounds.Add((JsonNode)SocksInbound(inTag, port, auth));
            outbounds.Add((JsonNode)Outbound(profile, tag, s));
            rules.Add((JsonNode)new JsonObject { ["type"] = "field", ["inboundTag"] = Arr([inTag]), ["outboundTag"] = tag });
        }

        // Всё, что не подошло ни под одно правило, отбрасываем.
        outbounds.Add((JsonNode)new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" });

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["loglevel"] = s.Expert.LogLevel switch
                {
                    CoreLogLevel.Debug => "debug",
                    CoreLogLevel.Info => "info",
                    _ => "warning",
                },
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["routing"] = new JsonObject { ["rules"] = rules },
        };
        return config.ToJsonString(Indented) + "\n";
    }

    private static JsonObject SocksInbound(string tag, int port, LocalAuth auth) => new()
    {
        ["tag"] = tag,
        ["listen"] = "127.0.0.1",
        ["port"] = port,
        ["protocol"] = "socks",
        ["settings"] = new JsonObject
        {
            ["auth"] = "password",
            ["accounts"] = new JsonArray((JsonNode)new JsonObject { ["user"] = auth.Username, ["pass"] = auth.Password }),
            ["udp"] = true,
        },
    };

    private static JsonObject Outbound(Profile p, string tag, AppSettings s)
    {
        var ob = new JsonObject { ["tag"] = tag };
        switch (p.Protocol)
        {
            case Protocol.Vless:
                ob["protocol"] = "vless";
                ob["settings"] = Vnext(p, new JsonObject
                {
                    ["id"] = p.Credential.Reveal(),
                    ["encryption"] = "none",
                    ["flow"] = p.Flow == VlessFlow.XtlsRprxVision ? "xtls-rprx-vision" : "",
                });
                break;
            case Protocol.Vmess:
                ob["protocol"] = "vmess";
                ob["settings"] = Vnext(p, new JsonObject { ["id"] = p.Credential.Reveal(), ["security"] = p.VmessCipher, ["alterId"] = 0 });
                break;
            case Protocol.Shadowsocks:
                ob["protocol"] = "shadowsocks";
                ob["settings"] = new JsonObject
                {
                    ["servers"] = new JsonArray((JsonNode)new JsonObject
                    {
                        ["address"] = p.Address,
                        ["port"] = p.Port,
                        ["method"] = p.SsMethod,
                        ["password"] = p.Credential.Reveal(),
                    }),
                };
                break;
            case Protocol.Hysteria2:
                throw new UnsupportedProfileException($"Сервер «{p.Name}» (Hysteria2) работает только через sing-box.");
            default:
                ob["protocol"] = "trojan";
                ob["settings"] = new JsonObject
                {
                    ["servers"] = new JsonArray((JsonNode)new JsonObject
                    {
                        ["address"] = p.Address,
                        ["port"] = p.Port,
                        ["password"] = p.Credential.Reveal(),
                    }),
                };
                break;
        }

        ob["streamSettings"] = Stream(p, s);
        if (s.Dpi.Mux && p.Flow != VlessFlow.XtlsRprxVision && p.Transport.Type != TransportType.Xhttp)
            ob["mux"] = new JsonObject { ["enabled"] = true, ["concurrency"] = s.Dpi.MuxConc };
        return ob;
    }

    private static JsonObject Vnext(Profile p, JsonObject user) => new()
    {
        ["vnext"] = new JsonArray((JsonNode)new JsonObject
        {
            ["address"] = p.Address,
            ["port"] = p.Port,
            ["users"] = new JsonArray((JsonNode)user),
        }),
    };

    private static JsonObject Stream(Profile p, AppSettings s)
    {
        var t = p.Transport;
        var stream = new JsonObject
        {
            ["network"] = t.Type switch
            {
                TransportType.Tcp => "raw",
                TransportType.Ws => "ws",
                TransportType.Grpc => "grpc",
                TransportType.HttpUpgrade => "httpupgrade",
                _ => "xhttp",
            },
            ["security"] = p.Security.Type switch
            {
                SecurityType.Tls => "tls",
                SecurityType.Reality => "reality",
                _ => "none",
            },
        };

        switch (t.Type)
        {
            case TransportType.Xhttp:
                var x = new JsonObject { ["path"] = t.Path ?? "/", ["mode"] = XhttpMode(t.XhttpMode) };
                if (t.Host is { } xh)
                    x["host"] = xh;
                stream["xhttpSettings"] = x;
                break;
            case TransportType.Ws:
                var ws = new JsonObject { ["path"] = t.Path ?? "/" };
                if (t.Host is { } wh)
                    ws["host"] = wh;
                stream["wsSettings"] = ws;
                break;
            case TransportType.HttpUpgrade:
                var hu = new JsonObject { ["path"] = t.Path ?? "/" };
                if (t.Host is { } hh)
                    hu["host"] = hh;
                stream["httpupgradeSettings"] = hu;
                break;
            case TransportType.Grpc:
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = t.ServiceName ?? "" };
                break;
        }

        var sec = p.Security;
        var fingerprint = sec.Fingerprint ?? s.Dpi.Utls;
        if (sec.Type == SecurityType.Tls)
        {
            var tls = new JsonObject
            {
                ["serverName"] = sec.Sni ?? p.Address,
                ["fingerprint"] = fingerprint,
                ["allowInsecure"] = sec.AllowInsecure,
            };
            if (sec.Alpn is { } alpn)
                tls["alpn"] = Arr(alpn.Split(','));
            stream["tlsSettings"] = tls;
        }
        else if (sec.Type == SecurityType.Reality && sec.Reality is { } r)
        {
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = sec.Sni ?? p.Address,
                ["fingerprint"] = fingerprint,
                // В Xray 25+ поле называется password, в старых версиях — publicKey. Xray пропускает
                // неизвестные поля, поэтому пишем оба: конфиг верен для любой версии.
                ["password"] = r.PublicKey,
                ["publicKey"] = r.PublicKey,
                ["shortId"] = r.ShortId,
                ["spiderX"] = r.SpiderX ?? "/",
            };
        }

        return stream;
    }

    private static string XhttpMode(XhttpMode m) => m switch
    {
        Model.XhttpMode.PacketUp => "packet-up",
        Model.XhttpMode.StreamUp => "stream-up",
        Model.XhttpMode.StreamOne => "stream-one",
        _ => "auto",
    };

    private static JsonArray Arr(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

    internal static string Port(int p) => p.ToString(CultureInfo.InvariantCulture);
}
