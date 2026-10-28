using System.Globalization;

namespace AgentSpeed.Presentation.Titles;

public enum TitleBadgeKind
{
    /// <summary>Four Han characters stacked 2 × 2 (Chinese only).</summary>
    Seal,
    TwoLines,
    OneLine,
}

/// <summary>Measures one line: (text, size in canvas units, tracking in em) → advance width in canvas units.</summary>
public delegate double BadgeLineMeasure(string text, double size, double trackingEm);

/// <summary>
/// How the hero title badge sets a title (DESIGN 4c, Hero title badge, Title layout), in units of the 100 × 100 badge
/// canvas. Pure: the caller measures text with its own fonts.
/// </summary>
/// <param name="Size">Font size in canvas units.</param>
/// <param name="Tracking">Tracking in em.</param>
/// <param name="LineAdvance">Line advance in em.</param>
public sealed record TitleBadgeLayout(TitleBadgeKind Kind, IReadOnlyList<string> Lines, double Size, double Tracking, double LineAdvance)
{
    /// <summary>The width the title block may take.</summary>
    public const double MaxWidth = 72;

    public const double Smallest = 9;

    public static TitleBadgeLayout Layout(string text, string languageCode, BadgeLineMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        var graphemes = Graphemes(text);
        var cjk = text.EnumerateRunes().Any(r => IsCjk(r.Value));
        var hanOnly = text.Length > 0 && text.EnumerateRunes().All(r => IsHan(r.Value));
        var chinese = languageCode.StartsWith("zh", StringComparison.Ordinal);
        double TrackingFor(double size) => cjk ? 0.04 : size < 14 ? 0 : 0.05;

        if (chinese && graphemes.Count == 4 && hanOnly)
        {
            return new TitleBadgeLayout(TitleBadgeKind.Seal, [graphemes[0] + graphemes[1], graphemes[2] + graphemes[3]], 21, 0.04, 1.04);
        }

        var maximum = graphemes.Count == 1 ? 34 : cjk ? 28 : graphemes.Count == 2 ? 26 : 22;
        var oneLine = Smallest;
        for (var size = (double)maximum; size >= Smallest; size -= 0.5)
        {
            if (measure(text, size, TrackingFor(size)) <= MaxWidth)
            {
                oneLine = size;
                break;
            }
        }

        var breaks = BreakCandidates(graphemes, hanOnly);
        if (breaks.Count > 0)
        {
            for (var size = cjk ? 22.0 : 20.0; size >= 13; size -= 0.5)
            {
                if (size < 1.15 * oneLine)
                {
                    continue;
                }

                string[]? best = null;
                var bestBalance = double.PositiveInfinity;
                foreach (var index in breaks)
                {
                    var first = string.Concat(graphemes.Take(index)).Trim(' ');
                    var second = string.Concat(graphemes.Skip(index)).Trim(' ');
                    if (first.Length == 0 || second.Length == 0)
                    {
                        continue;
                    }

                    var a = measure(first, size, TrackingFor(size));
                    var b = measure(second, size, TrackingFor(size));
                    if (Math.Max(a, b) > MaxWidth)
                    {
                        continue;
                    }

                    var balance = Math.Abs(a - b);
                    if (balance < bestBalance)
                    {
                        bestBalance = balance;
                        best = [first, second];
                    }
                }

                if (best is not null)
                {
                    return new TitleBadgeLayout(TitleBadgeKind.TwoLines, best, size, TrackingFor(size), 1.1);
                }
            }
        }

        return new TitleBadgeLayout(TitleBadgeKind.OneLine, [text], oneLine, TrackingFor(oneLine), 1.1);
    }

    /// <summary>
    /// Break opportunities (grapheme indexes): after every inner space; in Han-only text of five or more characters
    /// after the second (five) or at the middle (six or more). Runs of kana or Latin letters never break.
    /// </summary>
    public static IReadOnlyList<int> BreakCandidates(IReadOnlyList<string> graphemes, bool hanOnly)
    {
        var result = new List<int>();
        for (var i = 1; i < graphemes.Count - 1; i++)
        {
            if (graphemes[i] == " ")
            {
                result.Add(i);
            }
        }

        if (result.Count == 0 && hanOnly && graphemes.Count >= 5)
        {
            result.Add(graphemes.Count == 5 ? 2 : graphemes.Count / 2);
        }

        return result;
    }

    public static IReadOnlyList<string> Graphemes(string text)
    {
        var result = new List<string>(text.Length);
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            result.Add((string)elements.Current);
        }

        return result;
    }

    public static bool IsHan(int value) => value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF
        or >= 0x20000 and <= 0x2FA1F;

    public static bool IsCjk(int value) => value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xAC00 and <= 0xD7AF
        or >= 0xFF66 and <= 0xFF9F || IsHan(value);

    /// <summary>Whether a text contains a CJK character (Han, kana, Hangul).</summary>
    public static bool HasCjk(string text) => text.EnumerateRunes().Any(r => IsCjk(r.Value));
}
