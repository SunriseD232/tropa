using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tropa.Core.Model;

namespace Tropa.Core.Telemetry;

/// <summary>
/// Настройки для журнала разработчика (ADR-050): плоский список «раздел.ключ = значение».
/// Списки (правила, свои списки, правки гео-баз, приложения Store) — только количество и короткий
/// отпечаток: в них домены и программы пользователя. Свои hosts и шаблон — так же, только длина.
/// </summary>
public static class SettingsFlattener
{
    /// <summary>Служебное, что меняется само и только засоряет журнал.</summary>
    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "cores.lastManifestSequence",
        "cores.lastUpdateCheck",
    };

    /// <summary>Текст, который может быть личным: в журнал — длина и отпечаток.</summary>
    private static readonly HashSet<string> Private = new(StringComparer.Ordinal)
    {
        "dns.hosts",
        "expert.templateText",
        "telemetry.name",
    };

    public static SortedDictionary<string, string> Flatten(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var node = JsonSerializer.SerializeToNode(settings, BackupJsonContext.Default.AppSettings);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        Walk(node, "", result);
        return result;
    }

    /// <summary>Что изменилось: ключ → «было → стало». Пусто — ничего.</summary>
    public static SortedDictionary<string, string> Diff(AppSettings before, AppSettings after)
    {
        var a = Flatten(before);
        var b = Flatten(after);
        var diff = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in a.Keys.Union(b.Keys))
        {
            var was = a.GetValueOrDefault(key, "—");
            var now = b.GetValueOrDefault(key, "—");
            if (was != now)
                diff[key] = was + " → " + now;
        }

        return diff;
    }

    private static void Walk(JsonNode? node, string path, SortedDictionary<string, string> into)
    {
        if (Skip.Contains(path))
            return;
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, child) in o)
                    Walk(child, path.Length == 0 ? name : path + "." + name, into);
                break;
            case JsonArray a:
                into[path] = a.Count == 0 ? "[]" : $"[{a.Count}] #{Fingerprint(a.ToJsonString())}";
                break;
            case null:
                into[path] = "null";
                break;
            default:
                var text = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
                into[path] = Private.Contains(path) && text.Length > 0 ? $"<{text.Length.ToString(CultureInfo.InvariantCulture)} симв.> #{Fingerprint(text)}"
                    : text.Length > 200 ? text[..200] + "…"
                    : text;
                break;
        }
    }

    /// <summary>6 hex-символов SHA-256: видно, что список сменился, но не что в нём.</summary>
    private static string Fingerprint(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..6];
}
