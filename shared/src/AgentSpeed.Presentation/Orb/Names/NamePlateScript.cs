using System.Globalization;
using System.Text;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>Script questions of a plate's text: CJK tracking and centering, joined scripts, graphemes.</summary>
public static class NamePlateScript
{
    /// <summary>Han, kana, Hangul, Bopomofo, CJK punctuation and full-width forms.</summary>
    public static bool HasCjk(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsCjk(rune.Value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the text may be letter-spaced glyph by glyph: scripts whose letters join or reorder (Arabic, Hebrew,
    /// Syriac, Thaana, N'Ko, the Indic and Southeast Asian scripts) are set as one run without tracking.
    /// </summary>
    public static bool AllowsTracking(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return true;
        }

        foreach (var rune in text.EnumerateRunes())
        {
            if (IsJoined(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The number of graphemes (text elements), so an emoji or a combining sequence counts once.</summary>
    public static int Graphemes(string? text) => string.IsNullOrEmpty(text) ? 0 : new StringInfo(text).LengthInTextElements;

    /// <summary>The start index (UTF-16) of every grapheme.</summary>
    public static int[] GraphemeStarts(string? text) =>
        string.IsNullOrEmpty(text) ? [] : StringInfo.ParseCombiningCharacters(text);

    /// <summary>The first <paramref name="count"/> graphemes.</summary>
    public static string Prefix(string text, int count)
    {
        ArgumentNullException.ThrowIfNull(text);
        var info = new StringInfo(text);
        return count >= info.LengthInTextElements ? text : info.SubstringByTextElements(0, Math.Max(0, count));
    }

    /// <summary>The width of a text set with the plate's tracking, from its natural (untracked) width.</summary>
    public static double TrackedWidth(string text, double naturalWidth, double tracking)
    {
        ArgumentNullException.ThrowIfNull(text);
        var gaps = AllowsTracking(text) ? Math.Max(0, Graphemes(text) - 1) : 0;
        return Math.Max(0, naturalWidth) + gaps * tracking;
    }

    private static bool IsCjk(int c) =>
        c is >= 0x1100 and <= 0x11FF
        or >= 0x2E80 and <= 0x2FDF
        or >= 0x3000 and <= 0x303F
        or >= 0x3040 and <= 0x30FF
        or >= 0x3100 and <= 0x31FF
        or >= 0x3200 and <= 0x33FF
        or >= 0x3400 and <= 0x4DBF
        or >= 0x4E00 and <= 0x9FFF
        or >= 0xA960 and <= 0xA97F
        or >= 0xAC00 and <= 0xD7AF
        or >= 0xF900 and <= 0xFAFF
        or >= 0xFE30 and <= 0xFE4F
        or >= 0xFF00 and <= 0xFFEF
        or >= 0x20000 and <= 0x3134F;

    private static bool IsJoined(int c) =>
        c is >= 0x0590 and <= 0x08FF
        or >= 0x0900 and <= 0x0DFF
        or >= 0x0E00 and <= 0x0EFF
        or >= 0x1000 and <= 0x109F
        or >= 0x1780 and <= 0x17FF
        or >= 0xFB1D and <= 0xFDFF
        or >= 0xFE70 and <= 0xFEFF;
}

/// <summary>Text measured with the platform text stack: the natural (untracked) width of a string at a size, in pt.</summary>
public delegate double NamePlateMeasure(string text, double fontSize);

/// <summary>Grapheme-safe trimming with a trailing ellipsis.</summary>
internal static class Ellipsis
{
    public const string Mark = "…";

    /// <summary>The longest prefix (at least one grapheme) whose <paramref name="compose"/>d text fits.</summary>
    public static string Fit(string text, Func<string, string> compose, Func<string, bool> fits)
    {
        var count = NamePlateScript.Graphemes(text);
        var low = 1;
        var high = count;
        var best = 1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (fits(compose(Cut(text, mid))))
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return compose(Cut(text, best));
    }

    /// <summary>The first <paramref name="count"/> graphemes, trailing spaces removed, then <c>…</c>.</summary>
    public static string Cut(string text, int count)
    {
        var prefix = NamePlateScript.Prefix(text, count).TrimEnd();
        var builder = new StringBuilder(prefix.Length + 1);
        builder.Append(prefix.Length == 0 ? NamePlateScript.Prefix(text, 1) : prefix);
        builder.Append(Mark);
        return builder.ToString();
    }
}
