using System.Text;
using System.Text.RegularExpressions;

namespace AgentSpeed.Presentation.Updates;

public enum NoteBlockKind
{
    Heading,
    Paragraph,
    Bullet,
}

/// <param name="Marker">"•" or the list number ("2.") for bullets; empty otherwise.</param>
/// <param name="Indent">Nesting level of a bullet (0–3).</param>
public readonly record struct NoteBlock(NoteBlockKind Kind, string Text, string Marker = "", int Indent = 0);

/// <summary>
/// Turns release-notes Markdown into plain blocks (headings, paragraphs, bullets) for a light rich-text rendering.
/// Inline markup, links, images and HTML are reduced to their text; nothing is ever interpreted as a link or markup.
/// </summary>
public static partial class ReleaseNotes
{
    public const int MaxBlocks = 200;
    public const int MaxBlockLength = 2000;
    private const string Bullet = "•";

    // Escaped ASCII punctuation is parked in the private-use area while inline markup is stripped.
    private const int EscapeBase = 0xF000;

    public static IReadOnlyList<NoteBlock> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return [];
        }

        try
        {
            return ParseBlocks(markdown);
        }
        catch (RegexMatchTimeoutException)
        {
            return markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Take(MaxBlocks)
                .Select(line => new NoteBlock(NoteBlockKind.Paragraph, Clip(line)))
                .ToArray();
        }
    }

    private static List<NoteBlock> ParseBlocks(string markdown)
    {
        var blocks = new List<NoteBlock>();

        var text = HtmlComment().Replace(markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), string.Empty);
        var paragraph = new StringBuilder();
        NoteBlock? item = null;
        var fenced = false;
        var quoted = false;

        void AppendToParagraph(string text)
        {
            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(text);
        }

        void Flush()
        {
            if (item is { } open)
            {
                Add(blocks, open with { Text = Inline(open.Text) });
                item = null;
            }

            if (paragraph.Length > 0)
            {
                Add(blocks, new NoteBlock(NoteBlockKind.Paragraph, Inline(paragraph.ToString())));
                paragraph.Clear();
            }

            quoted = false;
        }

        foreach (var rawLine in text.Split('\n'))
        {
            if (blocks.Count >= MaxBlocks)
            {
                break;
            }

            var line = rawLine.TrimEnd();
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                Flush();
                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                if (trimmed.Length > 0)
                {
                    Add(blocks, new NoteBlock(NoteBlockKind.Paragraph, Clip(trimmed)));
                }

                continue;
            }

            if (trimmed.Length == 0 || HorizontalRule().IsMatch(trimmed))
            {
                Flush();
                continue;
            }

            if (Heading().Match(trimmed) is { Success: true } heading)
            {
                Flush();
                Add(blocks, new NoteBlock(NoteBlockKind.Heading, Inline(HeadingText(heading.Groups["text"].Value))));
                continue;
            }

            if (ListItem().Match(line) is { Success: true } listItem)
            {
                Flush();
                var indent = Math.Min(3, listItem.Groups["indent"].Value.Replace("\t", "    ", StringComparison.Ordinal).Length / 2);
                var marker = listItem.Groups["marker"].Value;
                var number = char.IsAsciiDigit(marker[0]) ? marker.TrimEnd('.', ')') + "." : Bullet;
                item = new NoteBlock(NoteBlockKind.Bullet, StripTaskBox(listItem.Groups["text"].Value), number, indent);
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                if (!quoted)
                {
                    Flush();
                }

                quoted = true;
                trimmed = trimmed.TrimStart('>', ' ', '\t');
                if (trimmed.Length == 0)
                {
                    Flush();
                    continue;
                }

                AppendToParagraph(trimmed);
                continue;
            }

            if (quoted)
            {
                Flush();
            }

            if (item is { } open && line.Length > trimmed.Length)
            {
                item = open with { Text = open.Text + " " + trimmed };
                continue;
            }

            if (item is not null)
            {
                Flush();
            }

            AppendToParagraph(trimmed);
        }

        Flush();
        return blocks;
    }

    /// <summary>Reduces inline Markdown and HTML to plain text.</summary>
    public static string Inline(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var result = Escape().Replace(text, m => ((char)(EscapeBase + m.Groups["c"].Value[0])).ToString());
        result = Image().Replace(result, "${alt}");
        result = Link().Replace(result, "${text}");
        result = AutoLink().Replace(result, "${url}");
        result = HtmlTag().Replace(result, string.Empty);
        result = Emphasis().Replace(result, "${text}");
        result = Emphasis().Replace(result, "${text}");
        result = result.Replace("`", string.Empty, StringComparison.Ordinal);
        result = Whitespace().Replace(result, " ").Trim();
        result = System.Net.WebUtility.HtmlDecode(result);
        return Clip(Unescape(result));
    }

    private static void Add(List<NoteBlock> blocks, NoteBlock block)
    {
        if (blocks.Count < MaxBlocks && block.Text.Length > 0)
        {
            blocks.Add(block);
        }
    }

    private static string StripTaskBox(string text) =>
        text.StartsWith("[ ] ", StringComparison.Ordinal) || text.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase) ? text[4..] : text;

    private static string Clip(string text)
    {
        if (text.Length <= MaxBlockLength)
        {
            return text;
        }

        var cut = char.IsHighSurrogate(text[MaxBlockLength - 1]) ? MaxBlockLength - 1 : MaxBlockLength;
        return text[..cut] + "…";
    }

    /// <summary>A closing run of '#' counts only when a space precedes it, so "C#" survives.</summary>
    private static string HeadingText(string text)
    {
        var trimmed = text.TrimEnd();
        var end = trimmed.Length;
        while (end > 0 && trimmed[end - 1] == '#')
        {
            end--;
        }

        if (end == 0)
        {
            return string.Empty;
        }

        return end < trimmed.Length && trimmed[end - 1] is ' ' or '\t' ? trimmed[..end].TrimEnd() : trimmed;
    }

    private static string Unescape(string text)
    {
        if (text.AsSpan().IndexOfAnyInRange((char)EscapeBase, (char)(EscapeBase + 0x7F)) < 0)
        {
            return text;
        }

        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] >= EscapeBase && chars[i] <= EscapeBase + 0x7F)
            {
                chars[i] = (char)(chars[i] - EscapeBase);
            }
        }

        return new string(chars);
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, matchTimeoutMilliseconds: 200)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^(?:[-*_]\s*){3,}$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex HorizontalRule();

    [GeneratedRegex(@"^#{1,6}\s+(?<text>.*)$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+]|\d{1,3}[.)])\s+(?<text>.*)$", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\([^)]*\)", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Image();

    [GeneratedRegex(@"\[(?<text>[^\]]+)\]\([^)]*\)", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Link();

    [GeneratedRegex(@"<(?<url>https?://[^>\s]+)>", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex AutoLink();

    [GeneratedRegex(@"</?[A-Za-z][^>]*>", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"(?<mark>\*\*|__|~~|\*)(?<text>\S(?:.*?\S)?)\k<mark>", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"\\(?<c>[\\`*_{}\[\]()#+\-.!>~|])", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Escape();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 200)]
    private static partial Regex Whitespace();
}
