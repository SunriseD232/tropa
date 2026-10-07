using System.Reflection;
using System.Text.Json;

namespace Tropa.App.Services;

/// <summary>Статья справки для кнопки «i» (docs/info.ru.json).</summary>
internal sealed record InfoArticle(string T, string? What, string? Why, string? Dpi, string? Risk, string? Conf);

/// <summary>Тексты справки. Источник — docs/info.ru.json, вшитый в сборку.</summary>
internal static class InfoTexts
{
    private static readonly Lazy<Dictionary<string, InfoArticle>> All = new(Load);

    public static InfoArticle? Get(string key) => All.Value.GetValueOrDefault(key);

    private static Dictionary<string, InfoArticle> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Tropa.info.ru.json")
            ?? throw new InvalidOperationException("В сборке нет текстов справки.");
        var result = new Dictionary<string, InfoArticle>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(stream);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            string? S(string name) => p.Value.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            result[p.Name] = new InfoArticle(S("t") ?? p.Name, S("what"), S("why"), S("dpi"), S("risk"), S("conf"));
        }

        return result;
    }
}
