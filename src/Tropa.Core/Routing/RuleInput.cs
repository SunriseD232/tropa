using System.Globalization;
using System.Text.RegularExpressions;

namespace Tropa.Core.Routing;

/// <summary>Проверка и нормализация того, что пользователь вводит на экране «Правила».</summary>
public static partial class RuleInput
{
    [GeneratedRegex(@"^[\p{L}\p{N} ._()+-]{1,96}\.exe$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ProcessName();

    [GeneratedRegex(@"^(?=.{1,253}$)(?!-)[a-z0-9-]{1,63}(?<!-)(\.(?!-)[a-z0-9-]{1,63}(?<!-))*$", RegexOptions.CultureInvariant)]
    private static partial Regex Domain();

    private static readonly IdnMapping Idn = new();

    /// <summary>«discord» → «discord.exe»; путь «C:\…\Discord.exe» → «Discord.exe». null — недопустимое имя.</summary>
    public static string? NormalizeProcess(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var name = input.Trim().Trim('"');
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
            name = name[(slash + 1)..];
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name += ".exe";
        return ProcessName().IsMatch(name) ? name : null;
    }

    /// <summary>
    /// «https://www.YouTube.com/watch» → «youtube.com»; «*.example.org» → «example.org»; кириллица → punycode.
    /// Правило срабатывает для домена и всех поддоменов. null — недопустимый домен.
    /// </summary>
    public static string? NormalizeDomain(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var s = input.Trim().ToLowerInvariant();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            s = s[(scheme + 3)..];
        var cut = s.IndexOfAny(['/', '?', '#', ':']);
        if (cut >= 0)
            s = s[..cut];
        s = s.TrimStart('*', '.').TrimEnd('.');
        if (s.StartsWith("www.", StringComparison.Ordinal))
            s = s[4..];
        try
        {
            s = Idn.GetAscii(s);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return Domain().IsMatch(s) && s.Contains('.', StringComparison.Ordinal) ? s : null;
    }
}
