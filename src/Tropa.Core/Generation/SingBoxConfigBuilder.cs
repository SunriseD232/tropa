using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tropa.Core.Compatibility;
using Tropa.Core.Model;
using Tropa.Core.Routing;

namespace Tropa.Core.Generation;

/// <summary>Профиль нельзя запустить этим ядром или в таком виде.</summary>
public sealed class UnsupportedProfileException(string message) : Exception(message);

/// <summary>Логин и пароль локального прокси (генерируются случайно при установке).</summary>
public sealed record LocalAuth(string Username, string Password);

/// <summary>Всё, что нужно генератору. Генератор чистый: никаких обращений к диску и сети.</summary>
public sealed record SingBoxInput
{
    public required AppSettings Settings { get; init; }

    /// <summary>Активный профиль.</summary>
    public required Profile Active { get; init; }

    /// <summary>Все профили: нужны для цепочек и правил «через конкретный сервер».</summary>
    public IReadOnlyList<Profile> Profiles { get; init; } = [];

    /// <summary>Группа авто-выбора (если авто-выбор включён и серверов больше одного).</summary>
    public IReadOnlyList<Profile> AutoSelectGroup { get; init; } = [];

    /// <summary>Каталог с файлами наборов правил (*.srs).</summary>
    public required string RuleSetDirectory { get; init; }

    public LocalAuth? LocalAuth { get; init; }

    /// <summary>Порт и секрет API статистики (только 127.0.0.1). null — API выключен.</summary>
    public int? ClashApiPort { get; init; }

    public string? ClashApiSecret { get; init; }

    /// <summary>Серверы, которые обслуживает Xray: Id профиля → порт его SOCKS-входа (гибрид, CorePlan).</summary>
    public IReadOnlyDictionary<Guid, int> XrayPorts { get; init; } = new Dictionary<Guid, int>();

    /// <summary>Порт входа Xray «напрямую» (шум для прямого UDP); null — не используется.</summary>
    public int? XrayDirectPort { get; init; }

    /// <summary>Логин и пароль SOCKS-входов Xray.</summary>
    public LocalAuth? XrayAuth { get; init; }

    /// <summary>Путь к xray.exe: в режиме TUN его собственный трафик отправляется напрямую, иначе он зациклится в туннель.</summary>
    public string? XrayPath { get; init; }

    /// <summary>Порт входа проверки обхода DPI (режим «сначала обход DPI»); null — без проверок.</summary>
    public int? DpiProbePort { get; init; }
}

/// <summary>
/// Генератор конфига sing-box (docs/05-config-generation.md). Схема сверяется с закреплённой
/// версией ядра golden-тестами и командой «sing-box check».
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    public const string TunInboundTag = "tun-in";
    public const string MixedInboundTag = "mixed-in";
    public const string LanInboundTag = "lan-in";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, IndentSize = 2, NewLine = "\n" };

    public static string Build(SingBoxInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var compat = CompatRules.Evaluate(input.Settings, input.Active);
        var s = compat.Effective;
        var ctx = new Context(input, s);

        var config = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["level"] = s.Expert.LogLevel switch
                {
                    CoreLogLevel.Debug => "debug",
                    CoreLogLevel.Info => "info",
                    _ => "warn",
                },
                ["timestamp"] = true,
            },
            ["dns"] = BuildDns(ctx),
            ["inbounds"] = BuildInbounds(ctx),
            ["outbounds"] = BuildOutbounds(ctx),
            ["route"] = BuildRoute(ctx),
        };

        if (input.ClashApiPort is { } port)
        {
            config["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    ["external_controller"] = $"127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}",
                    ["secret"] = input.ClashApiSecret ?? throw new ArgumentException("Для API статистики нужен секрет.", nameof(input)),
                },
            };
        }

        return config.ToJsonString(Indented) + "\n";
    }

    /// <summary>Сервер для временного тестового экземпляра ядра и его локальный порт.</summary>
    public sealed record TestTarget(Profile Profile, int Port);

    public const string PingInboundTag = "test-direct";

    /// <summary>
    /// Конфиг для тестов серверов (docs/07-testing-diagnostics.md, §1): у каждого сервера свой
    /// SOCKS-вход на 127.0.0.1 с паролем, весь прочий трафик отбрасывается. Текущее подключение не трогается.
    /// <paramref name="pingPort"/> — вход «напрямую» для TCP-пинга до самих серверов.
    /// Ядро выходит в сеть через физический адаптер (auto_detect_interface), а не через туннель
    /// режима «Весь компьютер» — иначе мерили бы сервер «через текущий сервер».
    /// </summary>
    /// <param name="bindInterface">
    /// Имя физического адаптера. Если на компьютере работает другой VPN со своим TUN, «интерфейс по
    /// умолчанию» может оказаться его туннелем, и проверка пойдёт через чужую программу.
    /// </param>
    public static string BuildTest(IReadOnlyList<TestTarget> targets, AppSettings settings, LocalAuth auth,
        IReadOnlyList<Profile>? allProfiles = null, int? pingPort = null, string? bindInterface = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(auth);
        if (targets.Count == 0 && pingPort is null)
            throw new ArgumentException("Нет серверов для теста.", nameof(targets));

        var input = new SingBoxInput
        {
            Settings = settings,
            // Active не используется тестовым конфигом, но обязателен для контекста.
            Active = targets.Count > 0 ? targets[0].Profile : PlaceholderProfile,
            Profiles = allProfiles ?? targets.Select(t => t.Profile).ToList(),
            RuleSetDirectory = "",
            LocalAuth = auth,
        };
        var ctx = new Context(input, CompatRules.Evaluate(settings).Effective);

        var inbounds = new JsonArray();
        var outbounds = new JsonArray();
        var rules = new JsonArray();
        var added = new HashSet<Guid>();

        void AddServer(Profile p)
        {
            if (!added.Add(p.Id))
                return;
            if (p.ChainVia is { } via)
                AddServer(ctx.FindProfile(via));
            outbounds.Push(Outbound(ctx, p, ctx.TagFor(p)));
        }

        foreach (var (profile, port) in targets)
        {
            AddServer(profile);
            var inTag = "test-" + ctx.TagFor(profile);
            inbounds.Push(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = inTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = port,
                ["users"] = Users(ctx),
            });
            rules.Push(new JsonObject { ["inbound"] = StrArray([inTag]), ["action"] = "route", ["outbound"] = ctx.TagFor(profile) });
        }

        if (pingPort is { } pp)
        {
            inbounds.Push(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = PingInboundTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = pp,
                ["users"] = Users(ctx),
            });
            rules.Push(new JsonObject { ["inbound"] = StrArray([PingInboundTag]), ["action"] = "route", ["outbound"] = DirectTag });
        }

        rules.Push(new JsonObject { ["action"] = "reject" });
        outbounds.Push(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(DnsServer("local", ctx.S.Dns.LocalDns, detour: null)),
                ["final"] = "local",
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = DirectTag,
                ["default_domain_resolver"] = "local",
            },
        };
        var route = (JsonObject)config["route"]!;
        if (bindInterface is { Length: > 0 })
            route["default_interface"] = bindInterface;
        else
            route["auto_detect_interface"] = true;
        return config.ToJsonString(Indented) + "\n";
    }

    private static readonly Profile PlaceholderProfile = new()
    {
        Name = "placeholder",
        Protocol = Protocol.Vless,
        Address = "127.0.0.1",
        Port = 1,
        Credential = new Security.Secret("00000000-0000-0000-0000-000000000000"),
    };

    private sealed class Context(SingBoxInput input, AppSettings settings)
    {
        public SingBoxInput Input { get; } = input;
        public AppSettings S { get; } = settings;
        public bool Tun => S.Connection.Mode == CaptureMode.Tun;
        public SortedSet<string> RuleSetTags { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, string> ServerTags { get; } = [];

        public string TagFor(Profile p)
        {
            if (!ServerTags.TryGetValue(p.Id, out var tag))
            {
                tag = "srv-" + p.Id.ToString("N")[..8];
                ServerTags[p.Id] = tag;
            }

            return tag;
        }

        public Profile FindProfile(Guid id) =>
            Input.Profiles.FirstOrDefault(p => p.Id == id) ?? (Input.Active.Id == id
                ? Input.Active
                : throw new UnsupportedProfileException("Правило или цепочка ссылается на удалённый сервер."));
    }

    // ---------------- DNS ----------------

    private static JsonObject BuildDns(Context ctx)
    {
        var s = ctx.S;
        var servers = new JsonArray(
            DnsServer("remote", s.Dns.RemoteDns, detour: ProxyTag),
            DnsServer("local", s.Dns.LocalDns, detour: null));

        var rules = new JsonArray();
        var hosts = ParseHosts(s.Dns.Hosts);
        if (hosts.Count > 0)
        {
            var predefined = new JsonObject();
            foreach (var (domain, ip) in hosts)
                predefined[domain] = ip;
            servers.Push(new JsonObject { ["type"] = "hosts", ["tag"] = "hosts", ["predefined"] = predefined });
            rules.Push(new JsonObject { ["domain"] = StrArray(hosts.Select(h => h.Domain)), ["server"] = "hosts" });
        }

        switch (s.Dns.LocalDnsRule)
        {
            case LocalDnsRule.Russian:
                foreach (var tag in RuleSets.AlwaysDirect)
                    ctx.RuleSetTags.Add(tag);
                ctx.RuleSetTags.Add(RuleSets.GeositeCategoryRu);
                rules.Push(new JsonObject
                {
                    ["rule_set"] = StrArray([.. RuleSets.AlwaysDirect, RuleSets.GeositeCategoryRu]),
                    ["server"] = "local",
                });
                rules.Push(new JsonObject { ["domain_suffix"] = StrArray(["ru", "su", "xn--p1ai"]), ["server"] = "local" });
                break;
            case LocalDnsRule.DirectRules:
                foreach (var tag in RuleSets.AlwaysDirect)
                    ctx.RuleSetTags.Add(tag);
                rules.Push(new JsonObject { ["rule_set"] = StrArray(RuleSets.AlwaysDirect), ["server"] = "local" });
                break;
            case LocalDnsRule.None:
                break;
        }

        if (s.Dns.Fakeip)
        {
            servers.Push(new JsonObject
            {
                ["type"] = "fakeip",
                ["tag"] = "fake",
                ["inet4_range"] = "198.18.0.0/15",
                ["inet6_range"] = "fc00::/18",
            });
            rules.Push(new JsonObject { ["query_type"] = StrArray(["A", "AAAA"]), ["server"] = "fake" });
        }

        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["rules"] = rules,
            ["final"] = "remote",
        };
        if (s.General.Ipv6Block)
            dns["strategy"] = "ipv4_only";
        if (!s.Dns.DnsCache)
            dns["disable_cache"] = true;
        return dns;
    }

    /// <summary>«https://1.1.1.1/dns-query», «tls://8.8.8.8», «77.88.8.8», «77.88.8.8:53».</summary>
    internal static JsonObject DnsServer(string tag, string address, string? detour)
    {
        JsonObject server;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "tls" or "quic" or "h3")
        {
            server = new JsonObject { ["type"] = uri.Scheme, ["tag"] = tag, ["server"] = uri.IdnHost };
            if (!uri.IsDefaultPort && uri.Port > 0)
                server["server_port"] = uri.Port;
            if (uri.Scheme is "https" or "h3" && uri.AbsolutePath is not ("/" or "/dns-query"))
                server["path"] = uri.AbsolutePath;
            if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out _) is false)
                server["domain_resolver"] = "local";
        }
        else
        {
            var hostPort = address.Trim();
            int? port = null;
            var colon = hostPort.LastIndexOf(':');
            if (colon > 0 && !hostPort.Contains("::", StringComparison.Ordinal)
                && int.TryParse(hostPort[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var p))
            {
                port = p;
                hostPort = hostPort[..colon];
            }

            if (!System.Net.IPAddress.TryParse(hostPort, out _))
                throw new UnsupportedProfileException($"DNS-сервер «{address}» должен быть IP-адресом или https://, tls://-адресом.");
            server = new JsonObject { ["type"] = "udp", ["tag"] = tag, ["server"] = hostPort };
            if (port is { } pp && pp != 53)
                server["server_port"] = pp;
        }

        if (detour is not null)
            server["detour"] = detour;
        return server;
    }

    internal static List<(string Domain, string Ip)> ParseHosts(string text)
    {
        var result = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;
            // Принимаем и «домен IP», и привычный порядок hosts-файла «IP домен».
            var (a, b) = (parts[0], parts[1]);
            if (System.Net.IPAddress.TryParse(a, out _))
                (a, b) = (b, a);
            if (System.Net.IPAddress.TryParse(b, out var ip) && a.Length <= 253)
                result.Add((a.ToLowerInvariant(), ip.ToString()));
        }

        return result;
    }

    // ---------------- Inbounds ----------------

    private static JsonArray BuildInbounds(Context ctx)
    {
        var s = ctx.S;
        var inbounds = new JsonArray();
        if (ctx.Tun)
        {
            var tun = new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = TunInboundTag,
                ["address"] = StrArray(["172.19.0.1/30", "fdfe:dcba:9876::1/126"]),
                ["mtu"] = s.Connection.Mtu,
                ["auto_route"] = true,
                ["strict_route"] = s.Connection.StrictRoute,
                ["stack"] = s.Connection.TunStack switch
                {
                    TunStackKind.Gvisor => "gvisor",
                    TunStackKind.System => "system",
                    _ => "mixed",
                },
            };
            if (s.Connection.LanBypass)
            {
                tun["route_exclude_address"] = StrArray(
                    ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"]);
            }

            inbounds.Push(tun);
        }

        // Локальный порт слушает только 127.0.0.1. Для сети — отдельный порт, всегда с паролем (ADR-013).
        var mixed = new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = MixedInboundTag,
            ["listen"] = "127.0.0.1",
            ["listen_port"] = s.Connection.SocksPort,
        };
        if (s.General.LocalPass)
            mixed["users"] = Users(ctx);
        inbounds.Push(mixed);

        if (s.Routing.DpiFirst && ctx.Input.DpiProbePort is { } probePort)
        {
            inbounds.Push(new JsonObject
            {
                ["type"] = "socks",
                ["tag"] = DpiGroups.ProbeInboundTag,
                ["listen"] = "127.0.0.1",
                ["listen_port"] = probePort,
                ["users"] = Users(ctx),
            });
        }

        if (s.Connection.LanAllow)
        {
            inbounds.Push(new JsonObject
            {
                ["type"] = "mixed",
                ["tag"] = LanInboundTag,
                ["listen"] = "0.0.0.0",
                ["listen_port"] = s.Connection.LanPort,
                ["users"] = Users(ctx),
            });
        }

        return inbounds;
    }

    private static JsonArray Users(Context ctx)
    {
        var auth = ctx.Input.LocalAuth ?? throw new ArgumentException("Для порта с паролем не переданы логин и пароль.", nameof(ctx));
        return new JsonArray(new JsonObject { ["username"] = auth.Username, ["password"] = auth.Password });
    }

    // ---------------- Outbounds ----------------

    private static JsonArray BuildOutbounds(Context ctx)
    {
        var s = ctx.S;
        var outbounds = new JsonArray();
        var added = new HashSet<Guid>();

        void AddServer(Profile p)
        {
            if (!added.Add(p.Id))
                return;
            if (p.ChainVia is { } via)
                AddServer(ctx.FindProfile(via));
            outbounds.Push(Outbound(ctx, p, ctx.TagFor(p)));
        }

        var group = s.Connection.AutoSelect ? ctx.Input.AutoSelectGroup.Where(p => ProfileCompat.Issues(p).Count == 0).ToList() : [];
        if (group.Count > 1)
        {
            foreach (var p in group)
                AddServer(p);
            outbounds.Insert(0, new JsonObject
            {
                ["type"] = "urltest",
                ["tag"] = ProxyTag,
                ["outbounds"] = StrArray(group.Select(ctx.TagFor)),
                ["url"] = s.Connection.TestUrl,
                ["interval"] = $"{s.Connection.AutoIntervalMinutes.ToString(CultureInfo.InvariantCulture)}m",
                ["tolerance"] = s.Connection.AutoTolMs,
            });
        }
        else
        {
            var active = ctx.Input.Active;
            if (active.ChainVia is { } via)
                AddServer(ctx.FindProfile(via));
            added.Add(active.Id);
            ctx.ServerTags[active.Id] = ProxyTag;
            outbounds.Insert(0, Outbound(ctx, active, ProxyTag));
        }

        // Серверы, на которые ссылаются правила «через конкретный сервер».
        foreach (var rule in s.Routing.Rules.Where(r => r.Enabled))
        {
            foreach (var action in new[] { rule.Tcp, rule.Udp })
            {
                if (action.Kind == RuleActionKind.Server && action.ServerId is { } id)
                    AddServer(ctx.FindProfile(id));
            }
        }

        outbounds.Push(new JsonObject { ["type"] = "direct", ["tag"] = DirectTag });
        if (ctx.Input.XrayDirectPort is { } directPort)
            outbounds.Push(XraySocks(ctx, NoiseDirectTag, directPort));

        if (s.Routing.DpiFirst)
        {
            // Адреса заблокированного узнаём через удалённый DNS: провайдерский их подменяет.
            outbounds.Push(new JsonObject { ["type"] = "direct", ["tag"] = DpiGroups.DirectTag, ["domain_resolver"] = "remote" });
            foreach (var g in DpiGroups.All)
            {
                outbounds.Push(new JsonObject
                {
                    ["type"] = "selector",
                    ["tag"] = g.SelectorTag,
                    ["outbounds"] = StrArray([DpiGroups.DirectTag, ProxyTag]),
                    ["default"] = DpiGroups.DirectTag,
                    ["interrupt_exist_connections"] = true,
                });
            }
        }

        return outbounds;
    }

    public const string NoiseDirectTag = "direct-noise";

    /// <summary>SOCKS-выход в локальный Xray (гибрид: sing-box — фронт, Xray — протокол).</summary>
    private static JsonObject XraySocks(Context ctx, string tag, int port)
    {
        var auth = ctx.Input.XrayAuth ?? throw new ArgumentException("Для Xray не переданы логин и пароль.", nameof(ctx));
        return new JsonObject
        {
            ["type"] = "socks",
            ["tag"] = tag,
            ["server"] = "127.0.0.1",
            ["server_port"] = port,
            ["version"] = "5",
            ["username"] = auth.Username,
            ["password"] = auth.Password,
        };
    }

    private static JsonObject Outbound(Context ctx, Profile p, string tag)
    {
        var issues = ProfileCompat.Issues(p);
        if (issues.Count > 0)
            throw new UnsupportedProfileException($"Сервер «{p.Name}» нельзя запустить: {string.Join(" ", issues)}");
        if (ctx.Input.XrayPorts.TryGetValue(p.Id, out var xrayPort))
            return XraySocks(ctx, tag, xrayPort);
        if (p.Transport.Type == TransportType.Xhttp)
            throw new UnsupportedProfileException($"Сервер «{p.Name}» использует XHTTP: для него нужно ядро Xray.");

        var ob = new JsonObject
        {
            ["type"] = p.Protocol switch
            {
                Protocol.Vless => "vless",
                Protocol.Vmess => "vmess",
                Protocol.Trojan => "trojan",
                Protocol.Shadowsocks => "shadowsocks",
                Protocol.Hysteria2 => "hysteria2",
                _ => throw new UnsupportedProfileException("Неизвестный протокол."),
            },
            ["tag"] = tag,
            ["server"] = p.Address,
            ["server_port"] = p.Port,
        };

        switch (p.Protocol)
        {
            case Protocol.Vless:
                ob["uuid"] = p.Credential.Reveal();
                if (p.Flow == VlessFlow.XtlsRprxVision)
                    ob["flow"] = "xtls-rprx-vision";
                ob["packet_encoding"] = "xudp";
                break;
            case Protocol.Vmess:
                ob["uuid"] = p.Credential.Reveal();
                ob["security"] = p.VmessCipher;
                ob["alter_id"] = 0;
                ob["packet_encoding"] = "xudp";
                break;
            case Protocol.Trojan:
                ob["password"] = p.Credential.Reveal();
                break;
            case Protocol.Shadowsocks:
                ob["method"] = p.SsMethod;
                ob["password"] = p.Credential.Reveal();
                break;
            case Protocol.Hysteria2:
                ob["password"] = p.Credential.Reveal();
                if (p.Obfs is { } obfs)
                    ob["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = obfs.Reveal() };
                break;
        }

        if (Tls(ctx, p) is { } tls)
            ob["tls"] = tls;
        if (Transport(p) is { } transport)
            ob["transport"] = transport;
        if (p.ChainVia is { } via)
            ob["detour"] = ctx.TagFor(ctx.FindProfile(via));
        // Mux не для Vision, XHTTP и QUIC (Hysteria2): там он несовместим или уже встроен.
        if (ctx.S.Dpi.Mux && p.Flow != VlessFlow.XtlsRprxVision && p.Protocol != Protocol.Hysteria2)
        {
            ob["multiplex"] = new JsonObject
            {
                ["enabled"] = true,
                ["protocol"] = "h2mux",
                ["max_streams"] = ctx.S.Dpi.MuxConc,
            };
        }

        return ob;
    }

    private static JsonObject? Tls(Context ctx, Profile p)
    {
        var sec = p.Security;
        if (sec.Type == SecurityType.None)
            return null;

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = sec.Sni ?? p.Address,
        };
        if (sec.AllowInsecure)
            tls["insecure"] = true;
        if (sec.Alpn is { } alpn)
            tls["alpn"] = StrArray(alpn.Split(','));
        // uTLS — подделка TLS поверх TCP; у QUIC (Hysteria2) его нет.
        if (p.Protocol != Protocol.Hysteria2)
        {
            tls["utls"] = new JsonObject
            {
                ["enabled"] = true,
                ["fingerprint"] = sec.Fingerprint ?? ctx.S.Dpi.Utls,
            };
        }
        if (sec.Type == SecurityType.Reality)
        {
            var r = sec.Reality!;
            tls["reality"] = new JsonObject
            {
                ["enabled"] = true,
                ["public_key"] = r.PublicKey,
                ["short_id"] = r.ShortId,
            };
        }

        return tls;
    }

    private static JsonObject? Transport(Profile p)
    {
        var t = p.Transport;
        switch (t.Type)
        {
            case TransportType.Tcp:
                return null;
            case TransportType.Ws:
                var ws = new JsonObject { ["type"] = "ws" };
                var (path, earlyData) = SplitEarlyData(t.Path ?? "/");
                ws["path"] = path;
                if (t.Host is { } host)
                    ws["headers"] = new JsonObject { ["Host"] = host };
                if (earlyData > 0)
                {
                    ws["max_early_data"] = earlyData;
                    ws["early_data_header_name"] = "Sec-WebSocket-Protocol";
                }

                return ws;
            case TransportType.HttpUpgrade:
                var hu = new JsonObject { ["type"] = "httpupgrade", ["path"] = t.Path ?? "/" };
                if (t.Host is { } huHost)
                    hu["host"] = huHost;
                return hu;
            case TransportType.Grpc:
                var grpc = new JsonObject { ["type"] = "grpc" };
                if (t.ServiceName is { } svc)
                    grpc["service_name"] = svc;
                return grpc;
            default:
                throw new UnsupportedProfileException("Транспорт не поддерживается ядром sing-box.");
        }
    }

    /// <summary>«/ws?ed=2048» → путь «/ws» и ранние данные 2048 байт (формат Xray-ссылок).</summary>
    internal static (string Path, int EarlyData) SplitEarlyData(string path)
    {
        var q = path.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
            return (path, 0);
        var rest = new List<string>();
        var ed = 0;
        foreach (var part in path[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("ed=", StringComparison.Ordinal)
                && int.TryParse(part[3..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is > 0 and <= 65536)
                ed = n;
            else
                rest.Add(part);
        }

        var basePath = path[..q];
        return (rest.Count == 0 ? basePath : basePath + "?" + string.Join('&', rest), ed);
    }

    // ---------------- Route ----------------

    private static JsonObject BuildRoute(Context ctx)
    {
        var s = ctx.S;
        var rules = new JsonArray();

        if (s.Dns.Sniffing)
        {
            var sniffers = new List<string>();
            if (s.Expert.SniffHttp)
                sniffers.Add("http");
            if (s.Expert.SniffTls)
                sniffers.Add("tls");
            if (s.Expert.SniffQuic)
                sniffers.Add("quic");
            var sniff = new JsonObject { ["action"] = "sniff" };
            if (sniffers.Count > 0)
                sniff["sniffer"] = StrArray(sniffers);
            rules.Push(sniff);
        }

        if (s.Dns.DnsHijack)
            rules.Push(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });

        // Трафик самого Xray (гибрид) в режиме TUN попадает в туннель — отпускаем его напрямую,
        // иначе соединения Xray к серверу ушли бы обратно в sing-box по кругу.
        var xrayLoopRule = ctx.Tun && ctx.Input.XrayPath is { } xrayPath && (ctx.Input.XrayPorts.Count > 0 || ctx.Input.XrayDirectPort is not null);
        if (xrayLoopRule)
            rules.Push(new JsonObject { ["process_path"] = StrArray([ctx.Input.XrayPath!]), ["action"] = "route", ["outbound"] = DirectTag });

        if (s.Routing.DpiFirst && ctx.Input.DpiProbePort is not null)
        {
            rules.Push(new JsonObject
            {
                ["inbound"] = StrArray([DpiGroups.ProbeInboundTag]),
                ["action"] = "route",
                ["outbound"] = DpiGroups.DirectTag,
                ["tls_fragment"] = true,
            });
        }

        if (s.General.Ipv6Block)
            rules.Push(new JsonObject { ["ip_version"] = 6, ["action"] = "reject" });
        if (s.Routing.BlockQuic)
            rules.Push(new JsonObject { ["network"] = "udp", ["port"] = 443, ["action"] = "reject" });

        rules.Push(new JsonObject { ["ip_is_private"] = true, ["outbound"] = DirectTag });

        // Госуслуги, банки и сайты, доступные только из России, — напрямую раньше всего остального.
        foreach (var tag in RuleSets.AlwaysDirect)
            ctx.RuleSetTags.Add(tag);
        rules.Push(Route(ctx, new JsonObject { ["rule_set"] = StrArray(RuleSets.AlwaysDirect) }, RuleAction.Direct));

        foreach (var rule in s.Routing.Rules.Where(r => r.Enabled && !r.Match.IsEmpty))
        {
            if (rule.Match.HasProcessCondition && !ctx.Tun)
                continue; // правила по процессам работают только в TUN (CompatRules: appRules)
            foreach (var expanded in ExpandRule(ctx, rule))
                rules.Push(expanded);
        }

        if (!s.Routing.UdpProxy)
            rules.Push(Route(ctx, new JsonObject { ["network"] = "udp" }, RuleAction.Direct));

        if (s.Dpi.Fragment && s.Dpi.FragScope == FragmentScope.List)
        {
            rules.Push(Route(ctx, new JsonObject { ["domain_suffix"] = StrArray(RuleSets.FragmentDefaults) }, RuleAction.Direct,
                forceFragment: true));
        }

        string final;
        if (s.Routing.Preset != RoutePreset.All)
        {
            // ИИ-сервисы закрыты для России их владельцами — обход DPI не поможет, только сервер.
            AddRuleSetRule(ctx, rules, RuleSets.GeositeAiNonCn, RuleAction.Proxy);
        }

        if (s.Routing.DpiFirst)
        {
            // Сначала обход DPI: каждая группа — через свой переключатель «напрямую с обходом ↔ сервер».
            foreach (var g in DpiGroups.All)
            {
                foreach (var tag in g.RuleSets)
                    ctx.RuleSetTags.Add(tag);
                rules.Push(new JsonObject
                {
                    ["rule_set"] = StrArray(g.RuleSets),
                    ["action"] = "route",
                    ["outbound"] = g.SelectorTag,
                    ["tls_fragment"] = true,
                });
            }
        }

        switch (s.Routing.Preset)
        {
            case RoutePreset.ExceptRu:
                // Заблокированное — через сервер, даже если это .ru; остальное российское — напрямую.
                // При «сначала обход DPI» заблокированное уже разобрано группами выше.
                foreach (var tag in s.Routing.DpiFirst ? [] : RuleSets.Blocked)
                    AddRuleSetRule(ctx, rules, tag, RuleAction.Proxy);
                AddRuleSetRule(ctx, rules, RuleSets.GeositeCategoryRu, RuleAction.Direct);
                rules.Push(Route(ctx, new JsonObject { ["domain_suffix"] = StrArray(["ru", "su", "xn--p1ai"]) }, RuleAction.Direct));
                AddRuleSetRule(ctx, rules, RuleSets.GeoipRu, RuleAction.Direct);
                final = ProxyTag;
                break;
            case RoutePreset.BlockedOnly:
                // При «сначала обход DPI» заблокированное уже разобрано группами выше.
                foreach (var tag in s.Routing.DpiFirst ? [] : RuleSets.Blocked)
                    AddRuleSetRule(ctx, rules, tag, RuleAction.Proxy);
                final = DirectTag;
                break;
            default:
                final = ProxyTag;
                break;
        }

        if (ctx.Input.XrayDirectPort is not null)
            rules = RouteDirectUdpThroughNoise(rules, final);

        var route = new JsonObject
        {
            ["rule_set"] = BuildRuleSets(ctx),
            ["rules"] = rules,
            ["final"] = final,
            ["default_domain_resolver"] = "local",
        };
        if (ctx.Tun && xrayLoopRule)
            route["find_process"] = true;
        if (ctx.Tun)
        {
            route["auto_detect_interface"] = true;
            if (s.Routing.Rules.Any(r => r.Enabled && r.Match.HasProcessCondition))
                route["find_process"] = true;
        }

        return route;
    }

    /// <summary>
    /// Шум (info.ru.json: noise) есть только в Xray: прямой UDP-трафик отправляем через его вход
    /// «напрямую», прямой TCP остаётся в sing-box (там своя фрагментация). Правило «напрямую» без
    /// уточнения сети раскладывается на два: TCP → direct, UDP → direct-noise. Локальная сеть и
    /// трафик самого Xray не трогаются.
    /// </summary>
    private static JsonArray RouteDirectUdpThroughNoise(JsonArray rules, string final)
    {
        var result = new JsonArray();
        foreach (var node in rules.ToList())
        {
            rules.Remove(node);
            var rule = (JsonObject)node!;
            var isDirect = rule["outbound"]?.GetValue<string>() == DirectTag;
            var untouchable = rule.ContainsKey("ip_is_private") || rule.ContainsKey("process_path") && rule["process_path"]!.ToJsonString().Contains("xray", StringComparison.OrdinalIgnoreCase);
            if (!isDirect || untouchable)
            {
                result.Push(rule);
                continue;
            }

            switch (rule["network"]?.GetValue<string>())
            {
                case "udp":
                    rule["outbound"] = NoiseDirectTag;
                    rule.Remove("tls_fragment");
                    result.Push(rule);
                    break;
                case "tcp":
                    result.Push(rule);
                    break;
                default:
                    var tcp = (JsonObject)rule.DeepClone();
                    tcp["network"] = "tcp";
                    var udp = (JsonObject)rule.DeepClone();
                    udp["network"] = "udp";
                    udp["outbound"] = NoiseDirectTag;
                    udp.Remove("tls_fragment");
                    result.Push(tcp);
                    result.Push(udp);
                    break;
            }
        }

        if (final == DirectTag)
            result.Push(new JsonObject { ["network"] = "udp", ["action"] = "route", ["outbound"] = NoiseDirectTag });
        return result;
    }

    private static void AddRuleSetRule(Context ctx, JsonArray rules, string tag, RuleAction action)
    {
        ctx.RuleSetTags.Add(tag);
        rules.Push(Route(ctx, new JsonObject { ["rule_set"] = StrArray([tag]) }, action));
    }

    /// <summary>Раскрывает правило в одно (одинаковые действия) или два (TCP и UDP отдельно) правила ядра.</summary>
    private static IEnumerable<JsonObject> ExpandRule(Context ctx, Rule rule)
    {
        if (rule.Tcp == rule.Udp)
        {
            yield return Route(ctx, MatchFields(ctx, rule.Match), rule.Tcp);
            yield break;
        }

        var tcp = MatchFields(ctx, rule.Match);
        tcp["network"] = "tcp";
        yield return Route(ctx, tcp, rule.Tcp);

        var udp = MatchFields(ctx, rule.Match);
        udp["network"] = "udp";
        yield return Route(ctx, udp, rule.Udp);
    }

    private static JsonObject MatchFields(Context ctx, RuleMatch m)
    {
        var o = new JsonObject();
        void Put(string key, IReadOnlyList<string> values)
        {
            if (values.Count > 0)
                o[key] = StrArray(values);
        }

        Put("process_name", m.Processes);
        Put("process_path", m.ProcessPaths);
        Put("domain", m.Domains);
        Put("domain_suffix", m.DomainSuffixes);
        Put("domain_keyword", m.DomainKeywords);
        Put("domain_regex", m.DomainRegexes);
        Put("ip_cidr", m.IpCidrs);

        var sets = m.GeoSite.Select(RuleSets.GeositeTag).Concat(m.GeoIp.Select(RuleSets.GeoipTag)).ToList();
        foreach (var tag in sets)
            ctx.RuleSetTags.Add(tag);
        Put("rule_set", sets);

        var single = m.Ports.Where(r => r.From == r.To).Select(r => r.From).ToList();
        var ranges = m.Ports.Where(r => r.From != r.To).Select(r => $"{r.From.ToString(CultureInfo.InvariantCulture)}:{r.To.ToString(CultureInfo.InvariantCulture)}").ToList();
        if (single.Count > 0)
            o["port"] = new JsonArray(single.Select(p => (JsonNode)p).ToArray());
        Put("port_range", ranges);
        return o;
    }

    private static JsonObject Route(Context ctx, JsonObject match, RuleAction action, bool forceFragment = false)
    {
        switch (action.Kind)
        {
            case RuleActionKind.Block:
                match["action"] = "reject";
                return match;
            case RuleActionKind.Direct:
                match["action"] = "route";
                match["outbound"] = DirectTag;
                var s = ctx.S;
                if (forceFragment || (s.Dpi.Fragment && s.Dpi.FragScope == FragmentScope.All))
                    match["tls_fragment"] = true;
                return match;
            case RuleActionKind.Server:
                match["action"] = "route";
                match["outbound"] = ctx.TagFor(ctx.FindProfile(action.ServerId ?? throw new UnsupportedProfileException("В правиле не указан сервер.")));
                return match;
            default:
                match["action"] = "route";
                match["outbound"] = ProxyTag;
                return match;
        }
    }

    private static JsonArray BuildRuleSets(Context ctx)
    {
        var sets = new JsonArray();
        foreach (var tag in ctx.RuleSetTags)
        {
            if (tag == RuleSets.RuServices)
            {
                sets.Push(new JsonObject
                {
                    ["type"] = "inline",
                    ["tag"] = tag,
                    ["rules"] = new JsonArray(new JsonObject { ["domain_suffix"] = StrArray(RuServices.DomainSuffixes.Distinct(StringComparer.Ordinal)) }),
                });
                continue;
            }

            sets.Push(new JsonObject
            {
                ["type"] = "local",
                ["tag"] = tag,
                ["format"] = "binary",
                ["path"] = Path.Combine(ctx.Input.RuleSetDirectory, tag + ".srs"),
            });
        }

        return sets;
    }

    private static JsonArray StrArray(IEnumerable<string> values) =>
        new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());
}

internal static class JsonArrayExtensions
{
    // Необобщённая перегрузка: обобщённый JsonArray.Add<T> несовместим с AOT/тримингом.
    public static void Push(this JsonArray array, JsonNode node) => ((ICollection<JsonNode?>)array).Add(node);
}
