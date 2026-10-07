using System.Collections.Frozen;
using System.Net;
using System.Text.Json;

namespace Tropa.Core.Security;

/// <summary>Что разрешено конфигу, который служба передаст ядру.</summary>
public sealed record GuardPolicy
{
    /// <summary>Каталоги, в которых ядру разрешено читать и писать файлы (гео-базы, кэш).</summary>
    public required IReadOnlyList<string> AllowedDirectories { get; init; }

    /// <summary>Разрешён ли mixed-inbound на всех интерфейсах (только с паролем).</summary>
    public bool AllowLanInbound { get; init; }
}

/// <summary>
/// Последний рубеж в службе (docs/05-config-generation.md, §5): служба не доверяет конфигу,
/// пришедшему от интерфейса, и проверяет его перед запуском ядра. Любое нарушение — отказ.
/// </summary>
public static class ConfigGuard
{
    private static readonly FrozenSet<string> RootKeys = new[] { "log", "dns", "inbounds", "outbounds", "route", "experimental" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> LogKeys = new[] { "level", "timestamp", "disabled" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> InboundTypes = new[] { "tun", "mixed", "socks" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> OutboundTypes = new[] { "vless", "vmess", "trojan", "direct", "block", "urltest", "selector", "socks" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> DnsServerTypes = new[] { "https", "tls", "quic", "h3", "udp", "tcp", "fakeip", "hosts" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ExperimentalKeys = new[] { "clash_api", "cache_file" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> ClashApiKeys = new[] { "external_controller", "secret" }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> CheckSingBox(string json, GuardPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var v = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return ["Конфиг не является корректным JSON."];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ["Конфиг должен быть JSON-объектом."];

            foreach (var p in root.EnumerateObject())
            {
                if (!RootKeys.Contains(p.Name))
                    v.Add($"Недопустимый раздел конфига «{p.Name}».");
            }

            ScanForbiddenKeys(root, "", v);

            if (root.TryGetProperty("log", out var log))
            {
                foreach (var p in log.EnumerateObject())
                {
                    if (!LogKeys.Contains(p.Name))
                        v.Add($"log.{p.Name}: запрещено (ядро не должно писать файлы само).");
                }
            }

            var outboundTags = CheckOutbounds(root, v);
            CheckInbounds(root, policy, v);
            var ruleSetTags = CheckRuleSets(root, policy, v);
            CheckRoute(root, outboundTags, ruleSetTags, v);
            CheckDns(root, outboundTags, ruleSetTags, v);
            CheckExperimental(root, policy, v);
        }

        return v;
    }

    /// <summary>Поля с путями к файлам допустимы только там, где их проверяют явно.</summary>
    private static void ScanForbiddenKeys(JsonElement e, string path, List<string> v)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    var here = path.Length == 0 ? p.Name : path + "." + p.Name;
                    if (p.Name == "output" || (p.Name.EndsWith("_path", StringComparison.Ordinal) && p.Name != "process_path"))
                        v.Add($"{here}: пути к файлам здесь запрещены.");
                    ScanForbiddenKeys(p.Value, here, v);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray())
                    ScanForbiddenKeys(item, $"{path}[{i++}]", v);
                break;
        }
    }

    private static HashSet<string> CheckOutbounds(JsonElement root, List<string> v)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array)
        {
            v.Add("Нет списка outbounds.");
            return tags;
        }

        foreach (var ob in outbounds.EnumerateArray())
        {
            var type = Str(ob, "type");
            var tag = Str(ob, "tag");
            if (type is null || !OutboundTypes.Contains(type))
                v.Add($"Недопустимый тип outbound «{type}».");
            if (tag is null || !tags.Add(tag))
                v.Add($"Outbound без тега или с повторяющимся тегом «{tag}».");
            if (type == "socks" && !IsLoopback(Str(ob, "server")))
                v.Add($"SOCKS-outbound «{tag}» разрешён только на локальное ядро (127.0.0.1).");
        }

        foreach (var ob in outbounds.EnumerateArray())
        {
            if (Str(ob, "detour") is { } detour && !tags.Contains(detour))
                v.Add($"Outbound «{Str(ob, "tag")}» ссылается на несуществующий detour «{detour}».");
            if (ob.TryGetProperty("outbounds", out var members) && members.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in members.EnumerateArray())
                {
                    if (m.GetString() is { } name && !tags.Contains(name))
                        v.Add($"Группа «{Str(ob, "tag")}» ссылается на несуществующий outbound «{name}».");
                }
            }
        }

        return tags;
    }

    private static void CheckInbounds(JsonElement root, GuardPolicy policy, List<string> v)
    {
        if (!root.TryGetProperty("inbounds", out var inbounds) || inbounds.ValueKind != JsonValueKind.Array)
        {
            v.Add("Нет списка inbounds.");
            return;
        }

        foreach (var ib in inbounds.EnumerateArray())
        {
            var type = Str(ib, "type");
            if (type is null || !InboundTypes.Contains(type))
            {
                v.Add($"Недопустимый тип inbound «{type}».");
                continue;
            }

            if (type == "tun")
                continue;

            var listen = Str(ib, "listen");
            if (IsLoopback(listen))
                continue;

            var hasUsers = ib.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array && users.GetArrayLength() > 0;
            if (!policy.AllowLanInbound)
                v.Add($"Inbound «{Str(ib, "tag")}» слушает «{listen}», а доступ из сети не разрешён.");
            else if (!hasUsers)
                v.Add($"Inbound «{Str(ib, "tag")}» открыт в сеть без пароля.");
        }
    }

    private static HashSet<string> CheckRuleSets(JsonElement root, GuardPolicy policy, List<string> v)
    {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("route", out var route) || !route.TryGetProperty("rule_set", out var sets) || sets.ValueKind != JsonValueKind.Array)
            return tags;

        foreach (var set in sets.EnumerateArray())
        {
            var tag = Str(set, "tag");
            if (tag is not null)
                tags.Add(tag);
            switch (Str(set, "type"))
            {
                case "inline":
                    break;
                case "local":
                    if (!IsWithin(Str(set, "path"), policy.AllowedDirectories))
                        v.Add($"Набор правил «{tag}» лежит вне разрешённых каталогов.");
                    break;
                default:
                    v.Add($"Набор правил «{tag}»: разрешены только локальные и встроенные наборы, ядро не скачивает файлы само.");
                    break;
            }
        }

        return tags;
    }

    private static void CheckRoute(JsonElement root, HashSet<string> outbounds, HashSet<string> ruleSets, List<string> v)
    {
        if (!root.TryGetProperty("route", out var route))
            return;
        if (Str(route, "final") is { } final && !outbounds.Contains(final))
            v.Add($"route.final ссылается на несуществующий outbound «{final}».");
        if (!route.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
            return;

        foreach (var rule in rules.EnumerateArray())
        {
            if (Str(rule, "outbound") is { } ob && !outbounds.Contains(ob))
                v.Add($"Правило ссылается на несуществующий outbound «{ob}».");
            CheckRuleSetRefs(rule, ruleSets, v);
        }
    }

    private static void CheckDns(JsonElement root, HashSet<string> outbounds, HashSet<string> ruleSets, List<string> v)
    {
        if (!root.TryGetProperty("dns", out var dns))
            return;
        var servers = new HashSet<string>(StringComparer.Ordinal);
        if (dns.TryGetProperty("servers", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in list.EnumerateArray())
            {
                var type = Str(s, "type");
                if (type is null || !DnsServerTypes.Contains(type))
                    v.Add($"Недопустимый тип DNS-сервера «{type}».");
                if (type == "hosts" && s.TryGetProperty("path", out _))
                    v.Add("DNS hosts: чтение файлов запрещено, только записи в конфиге.");
                if (Str(s, "tag") is { } tag)
                    servers.Add(tag);
                if (Str(s, "detour") is { } detour && !outbounds.Contains(detour))
                    v.Add($"DNS-сервер «{Str(s, "tag")}» ссылается на несуществующий outbound «{detour}».");
            }
        }

        if (Str(dns, "final") is { } final && !servers.Contains(final))
            v.Add($"dns.final ссылается на несуществующий сервер «{final}».");
        if (dns.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
        {
            foreach (var rule in rules.EnumerateArray())
            {
                if (Str(rule, "server") is { } server && !servers.Contains(server))
                    v.Add($"Правило DNS ссылается на несуществующий сервер «{server}».");
                CheckRuleSetRefs(rule, ruleSets, v);
            }
        }
    }

    private static void CheckRuleSetRefs(JsonElement rule, HashSet<string> ruleSets, List<string> v)
    {
        if (!rule.TryGetProperty("rule_set", out var refs) || refs.ValueKind != JsonValueKind.Array)
            return;
        foreach (var r in refs.EnumerateArray())
        {
            if (r.GetString() is { } name && !ruleSets.Contains(name))
                v.Add($"Правило ссылается на неописанный набор правил «{name}».");
        }
    }

    private static void CheckExperimental(JsonElement root, GuardPolicy policy, List<string> v)
    {
        if (!root.TryGetProperty("experimental", out var exp))
            return;
        foreach (var p in exp.EnumerateObject())
        {
            if (!ExperimentalKeys.Contains(p.Name))
            {
                v.Add($"experimental.{p.Name}: запрещено.");
                continue;
            }

            if (p.Name == "clash_api")
            {
                foreach (var k in p.Value.EnumerateObject())
                {
                    if (!ClashApiKeys.Contains(k.Name))
                        v.Add($"experimental.clash_api.{k.Name}: запрещено (веб-панели и внешние загрузки не нужны).");
                }

                var controller = Str(p.Value, "external_controller");
                var host = controller?[..Math.Max(0, controller.LastIndexOf(':'))];
                if (!IsLoopback(host))
                    v.Add("API статистики должно слушать только 127.0.0.1.");
                if (string.IsNullOrEmpty(Str(p.Value, "secret")))
                    v.Add("API статистики без секрета запрещено.");
            }
            else if (p.Name == "cache_file" && p.Value.TryGetProperty("path", out _) && !IsWithin(Str(p.Value, "path"), policy.AllowedDirectories))
            {
                v.Add("Файл кэша лежит вне разрешённых каталогов.");
            }
        }
    }

    internal static bool IsLoopback(string? host)
    {
        if (string.IsNullOrEmpty(host))
            return false;
        host = host.Trim('[', ']');
        return host == "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>Абсолютный локальный путь внутри одного из разрешённых каталогов. UNC и device-пути запрещены.</summary>
    internal static bool IsWithin(string? path, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path)
            || path.LastIndexOf(':') != 1) // альтернативные потоки NTFS «file:stream»
            return false;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }

        foreach (var dir in allowed)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Path.IsPathFullyQualified(dir))
                continue;
            var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
