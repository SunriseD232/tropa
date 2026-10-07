using System.Collections.Frozen;
using System.Text.Json;

namespace Tropa.Core.Security;

/// <summary>
/// Проверка конфига Xray перед запуском (docs/05-config-generation.md, §5). Xray работает только
/// «за SOCKS» на 127.0.0.1, поэтому белые списки здесь ещё уже, чем у sing-box. Отдельно запрещены
/// корневой «env» (переменные окружения процесса), логи в файлы и API.
/// </summary>
public static class XrayConfigGuard
{
    private static readonly FrozenSet<string> RootKeys = new[] { "log", "inbounds", "outbounds", "routing" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> LogKeys = new[] { "loglevel" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> OutboundProtocols = new[] { "vless", "vmess", "trojan", "freedom", "blackhole" }.ToFrozenSet(StringComparer.Ordinal);

    // Поля, через которые Xray читает или пишет файлы, или выполняет что-то вне сети.
    private static readonly FrozenSet<string> ForbiddenKeys = new[]
    {
        "access", "error", "certificateFile", "keyFile", "certificates", "env", "api", "stats", "policy",
        "reverse", "fakedns", "observatory", "burstObservatory", "dialerProxy", "metrics",
    }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlyList<string> Check(string json)
    {
        var v = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        }
        catch (JsonException)
        {
            return ["Конфиг Xray не является корректным JSON."];
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ["Конфиг Xray должен быть JSON-объектом."];

            foreach (var p in root.EnumerateObject())
            {
                if (!RootKeys.Contains(p.Name))
                    v.Add($"Xray: недопустимый раздел «{p.Name}».");
            }

            Scan(root, "", v);

            if (root.TryGetProperty("log", out var log) && log.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in log.EnumerateObject())
                {
                    if (!LogKeys.Contains(p.Name))
                        v.Add($"Xray: log.{p.Name} запрещено.");
                }
            }

            var inboundTags = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("inbounds", out var inbounds) && inbounds.ValueKind == JsonValueKind.Array)
            {
                foreach (var ib in inbounds.EnumerateArray())
                {
                    if (Str(ib, "protocol") != "socks")
                        v.Add($"Xray: разрешены только SOCKS-входы, а не «{Str(ib, "protocol")}».");
                    if (!ConfigGuard.IsLoopback(Str(ib, "listen")))
                        v.Add($"Xray: вход «{Str(ib, "tag")}» должен слушать только 127.0.0.1.");
                    if (!ib.TryGetProperty("settings", out var st) || Str(st, "auth") != "password")
                        v.Add($"Xray: вход «{Str(ib, "tag")}» без пароля запрещён.");
                    if (Str(ib, "tag") is { } tag)
                        inboundTags.Add(tag);
                }
            }
            else
            {
                v.Add("Xray: нет списка inbounds.");
            }

            var outboundTags = new HashSet<string>(StringComparer.Ordinal);
            if (root.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array)
            {
                foreach (var ob in outbounds.EnumerateArray())
                {
                    if (Str(ob, "protocol") is not { } proto || !OutboundProtocols.Contains(proto))
                        v.Add($"Xray: недопустимый протокол выхода «{Str(ob, "protocol")}».");
                    if (Str(ob, "tag") is { } tag && !outboundTags.Add(tag))
                        v.Add($"Xray: повторяющийся тег выхода «{tag}».");
                }
            }
            else
            {
                v.Add("Xray: нет списка outbounds.");
            }

            if (root.TryGetProperty("routing", out var routing) && routing.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
            {
                foreach (var rule in rules.EnumerateArray())
                {
                    if (Str(rule, "outboundTag") is { } target && !outboundTags.Contains(target))
                        v.Add($"Xray: правило ссылается на несуществующий выход «{target}».");
                    if (rule.TryGetProperty("inboundTag", out var tags) && tags.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in tags.EnumerateArray())
                        {
                            if (t.GetString() is { } name && !inboundTags.Contains(name))
                                v.Add($"Xray: правило ссылается на несуществующий вход «{name}».");
                        }
                    }
                }
            }
        }

        return v;
    }

    private static void Scan(JsonElement e, string path, List<string> v)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    var here = path.Length == 0 ? p.Name : path + "." + p.Name;
                    if (ForbiddenKeys.Contains(p.Name))
                        v.Add($"Xray: {here} запрещено.");
                    Scan(p.Value, here, v);
                }

                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var item in e.EnumerateArray())
                    Scan(item, $"{path}[{i++}]", v);
                break;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
