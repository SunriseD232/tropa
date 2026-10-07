using System.Globalization;
using System.Text;

namespace Tropa.Core.Security;

/// <summary>
/// Очищает названия серверов из подписок: убирает управляющие и невидимые символы
/// (включая bidi-переопределения вроде U+202E, которыми можно подделать отображение),
/// схлопывает пробелы и ограничивает длину.
/// </summary>
public static class NameSanitizer
{
    public const int MaxLength = 64;

    public static string Sanitize(string? name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
            return fallback;

        var sb = new StringBuilder(name.Length);
        var lastWasSpace = false;
        foreach (var rune in name.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
                or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator)
            {
                // Вместо переноса строки или табуляции оставляем пробел, остальное выбрасываем.
                if (Rune.IsWhiteSpace(rune) && !lastWasSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
                continue;
            }

            if (Rune.IsWhiteSpace(rune))
            {
                if (!lastWasSpace && sb.Length > 0)
                    sb.Append(' ');
                lastWasSpace = true;
                continue;
            }

            sb.Append(rune.ToString());
            lastWasSpace = false;
        }

        var result = Truncate(sb.ToString().Trim(), MaxLength);
        return result.Length == 0 ? fallback : result;
    }

    // Обрезаем по графемам, чтобы не разорвать эмодзи-флаг или букву с диакритикой.
    private static string Truncate(string value, int maxElements)
    {
        var info = new StringInfo(value);
        return info.LengthInTextElements <= maxElements
            ? value
            : info.SubstringByTextElements(0, maxElements).TrimEnd();
    }
}
