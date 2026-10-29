using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AgentSpeed.History.Perf;

/// <summary>A numeric column's needs: its widest cell, and its header's narrowest width in one, two and three lines.</summary>
internal readonly record struct PerfColumnNeed(double Content, double OneLine, double TwoLines, double ThreeLines)
{
    public double Header(int lines) => lines switch
    {
        <= 1 => OneLine,
        2 => TwoLines,
        _ => ThreeLines,
    };
}

/// <summary>
/// Fits the 性能表現 table's columns to the section's width so nothing scrolls sideways (DESIGN 12, <i>Widths</i>):
/// numbers keep their widest cell and their header (wrapping to two, then three lines, with room for the sort glyph so
/// sorting never moves a column); the model column takes the rest.
/// </summary>
internal static class PerfTableColumns
{
    /// <summary>The space a grid cell keeps on either side (the row presenter's own cell margin).</summary>
    public const double CellPadding = 6;

    /// <summary>Kept in every header for the sort glyph.</summary>
    public const double GlyphRoom = 12;

    /// <summary>Extra room per numeric column when the section is wide and it fits.</summary>
    public const double WideAir = 8;

    public const double HeaderLineHeight = 16;

    /// <summary>The model column never gets less than this while the numbers can still give way.</summary>
    public const double ModelMinimum = 120;

    /// <summary>The model column needs no more than this before the numbers get air.</summary>
    public const double ModelMaximum = 260;

    /// <returns>The model column's width, then each numeric column's, in order.</returns>
    public static double[] Fit(double available, double modelWant, IReadOnlyList<PerfColumnNeed> numeric, bool wide)
    {
        ArgumentNullException.ThrowIfNull(numeric);
        var want = Math.Clamp(modelWant, ModelMinimum, ModelMaximum);
        (int Lines, double Air)[] tiers = wide
            ? [(1, WideAir), (2, WideAir), (1, 0), (2, 0), (3, 0)]
            : [(1, 0), (2, 0), (3, 0)];
        double[]? roomiest = null;
        foreach (var (lines, air) in tiers)
        {
            var widths = numeric.Select(n => Math.Ceiling(Math.Max(n.Content, n.Header(lines) + GlyphRoom) + 2 * CellPadding + air)).ToArray();
            var model = available - widths.Sum();
            if (model >= want)
            {
                return [model, .. widths];
            }

            // No layout gives the model all it wants: the numbers get no air, and the model the most room they leave.
            if (air == 0 && model >= ModelMinimum && (roomiest is null || model > roomiest[0]))
            {
                roomiest = [model, .. widths];
            }
        }

        return roomiest ?? Squeeze(available, numeric);
    }

    /// <summary>
    /// The narrowest width that sets <paramref name="text"/> in at most <paramref name="lines"/> lines without splitting a
    /// word (CJK text breaks between characters).
    /// </summary>
    public static double TextWidth(string text, Typeface face, double size, double pixelsPerDip, int lines = 1)
    {
        var full = Format(text, face, size, pixelsPerDip).WidthIncludingTrailingWhitespace;
        if (lines <= 1 || full <= 0)
        {
            return Math.Ceiling(full);
        }

        var low = Math.Min(full, LongestUnbreakable(text, face, size, pixelsPerDip));
        var high = full;
        for (var i = 0; i < 12 && high - low > 0.5; i++)
        {
            var mid = (low + high) / 2;
            var formatted = Format(text, face, size, pixelsPerDip);
            formatted.MaxTextWidth = mid;
            formatted.LineHeight = HeaderLineHeight;
            if (Math.Round(formatted.Height / HeaderLineHeight) <= lines)
            {
                high = mid;
            }
            else
            {
                low = mid;
            }
        }

        return Math.Ceiling(high) + 1;
    }

    /// <summary>Squeezes the numbers toward their widest cells, in proportion to their slack, to leave the model its minimum.</summary>
    private static double[] Squeeze(double available, IReadOnlyList<PerfColumnNeed> numeric)
    {
        var widths = numeric.Select(n => Math.Ceiling(Math.Max(n.Content, n.Header(3) + GlyphRoom) + 2 * CellPadding)).ToArray();
        var floors = numeric.Select(n => Math.Ceiling(n.Content + 2 * CellPadding)).ToArray();
        var deficit = widths.Sum() + ModelMinimum - available;
        var slack = widths.Zip(floors, (w, f) => w - f).Sum();
        if (deficit > 0 && slack > 0)
        {
            var share = Math.Min(1, deficit / slack);
            for (var i = 0; i < widths.Length; i++)
            {
                widths[i] = Math.Floor(widths[i] - (widths[i] - floors[i]) * share);
            }
        }

        return [Math.Max(0, available - widths.Sum()), .. widths];
    }

    private static double LongestUnbreakable(string text, Typeface face, double size, double pixelsPerDip)
    {
        var longest = 0.0;
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Any(IsIdeographic))
            {
                // Ideographs break between any two; Latin runs inside stay whole.
                foreach (var part in Runs(token))
                {
                    longest = Math.Max(longest, Format(part, face, size, pixelsPerDip).WidthIncludingTrailingWhitespace);
                }
            }
            else
            {
                longest = Math.Max(longest, Format(token, face, size, pixelsPerDip).WidthIncludingTrailingWhitespace);
            }
        }

        return longest;
    }

    private static IEnumerable<string> Runs(string token)
    {
        var latin = new System.Text.StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(token);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            if (element.Any(IsIdeographic))
            {
                if (latin.Length > 0)
                {
                    yield return latin.ToString();
                    latin.Clear();
                }

                yield return element;
            }
            else
            {
                latin.Append(element);
            }
        }

        if (latin.Length > 0)
        {
            yield return latin.ToString();
        }
    }

    private static bool IsIdeographic(char c) => (c >= '⺀' && !char.IsSurrogate(c)) || char.IsHighSurrogate(c);

    private static FormattedText Format(string text, Typeface face, double size, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, Brushes.Black, pixelsPerDip)
        {
            Trimming = TextTrimming.None,
        };
}
