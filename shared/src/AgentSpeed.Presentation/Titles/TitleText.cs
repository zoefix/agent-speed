using System.Globalization;
using System.Text;

namespace AgentSpeed.Presentation.Titles;

/// <summary>What sanitizing did: the clean text and whether color emoji were removed (<c>titles.custom.noEmoji</c>).</summary>
public readonly record struct SanitizedText(string Text, bool RemovedEmoji);

/// <summary>A committed title cut to the width budget; <see cref="Cut"/> shows <c>titles.custom.tooLong</c>.</summary>
public readonly record struct FittedText(string Text, bool Cut);

/// <summary>The text rules of custom titles and names (DESIGN 1, Custom title sets), applied on commit.</summary>
public static class TitleText
{
    /// <summary>A title or idle label holds at most this many grapheme clusters.</summary>
    public const int GraphemeLimit = 24;

    /// <summary>The maximum width budget <c>w7</c> in DIP (the readout's title style at the 7 pt floor, tracking 0).</summary>
    public const double MaximumWidth = 51.5;

    /// <summary>The comfortable budget: fits every style from 96 pt.</summary>
    public const double ComfortableWidth = 44.2;

    /// <summary>The size <c>w7</c> is measured at.</summary>
    public const double MeasureSize = 7;

    /// <summary>
    /// NFC; controls, line and paragraph separators and bidi controls removed; tabs, line breaks and other spaces become
    /// one space; runs collapse; trimmed; emoji with emoji presentation removed; at most <paramref name="limit"/>
    /// grapheme clusters.
    /// </summary>
    public static SanitizedText Sanitize(string? text, int limit = GraphemeLimit)
    {
        if (string.IsNullOrEmpty(text))
        {
            return new SanitizedText(string.Empty, false);
        }

        if (text.Length > 4096)
        {
            text = text[..4096];
        }

        string normalized;
        try
        {
            normalized = text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            normalized = text;
        }

        var scalars = new StringBuilder(normalized.Length);
        var lastWasSpace = true;
        foreach (var rune in normalized.EnumerateRunes())
        {
            if (IsSpace(rune))
            {
                if (!lastWasSpace)
                {
                    scalars.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            if (IsRemoved(rune))
            {
                continue;
            }

            scalars.Append(rune.ToString());
            lastWasSpace = false;
        }

        var removedEmoji = false;
        var kept = new StringBuilder(scalars.Length);
        var count = 0;
        var elements = StringInfo.GetTextElementEnumerator(scalars.ToString());
        while (elements.MoveNext())
        {
            var element = (string)elements.Current;
            if (IsColorEmoji(element))
            {
                removedEmoji = true;
                continue;
            }

            if (count >= limit)
            {
                break;
            }

            kept.Append(element);
            count++;
        }

        var collapsed = string.Join(' ', kept.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return new SanitizedText(collapsed, removedEmoji);
    }

    /// <summary>Grapheme clusters (user-perceived characters) of a text.</summary>
    public static int GraphemeCount(string text) => new StringInfo(text ?? string.Empty).LengthInTextElements;

    /// <summary>The longest prefix of whole grapheme clusters whose width fits <paramref name="maxWidth"/>.</summary>
    public static FittedText Fitted(string text, double maxWidth, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        if (text.Length == 0 || measure(text) <= maxWidth)
        {
            return new FittedText(text, false);
        }

        var info = new StringInfo(text);
        var low = 0;
        var high = info.LengthInTextElements;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            if (measure(info.SubstringByTextElements(0, middle)) <= maxWidth)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        var prefix = low == 0 ? string.Empty : info.SubstringByTextElements(0, low).Trim(' ');
        return new FittedText(prefix, true);
    }

    /// <summary>The readout's case rule: Latin uppercase on the orb and the badge when the app language is English.</summary>
    public static string DisplayCase(string text, bool english) => english ? text.ToUpperInvariant() : text;

    private static bool IsSpace(Rune rune) => rune.Value switch
    {
        0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x85 => true,
        _ => Rune.GetUnicodeCategory(rune) == UnicodeCategory.SpaceSeparator,
    };

    private static bool IsRemoved(Rune rune)
    {
        if (rune.Value is >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }

    /// <summary>
    /// A grapheme drawn as a color emoji: an emoji-presentation scalar, or any emoji with the emoji variation selector or
    /// a keycap. Text symbols such as ★ ✦ ∞ stay.
    /// </summary>
    public static bool IsColorEmoji(string grapheme)
    {
        ArgumentNullException.ThrowIfNull(grapheme);
        var variation = false;
        var emoji = false;
        foreach (var rune in grapheme.EnumerateRunes())
        {
            if (EmojiRanges.IsEmojiPresentation(rune.Value))
            {
                return true;
            }

            variation |= rune.Value is 0xFE0F or 0x20E3;
            emoji |= EmojiRanges.IsEmoji(rune.Value);
        }

        return variation && emoji;
    }
}
