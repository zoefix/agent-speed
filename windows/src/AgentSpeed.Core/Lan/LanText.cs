using System.Text;

namespace AgentSpeed.Core.Lan;

/// <summary>Text rules shared by the settings document and the frame (LAN 2.1, 3.1), counted in Unicode scalar values.</summary>
public static class LanText
{
    public const int NameLimit = 64;
    public const string DefaultComputerName = "Agent Speed";

    /// <summary>White space as the reference implementation strips it (Unicode white space and U+001C–U+001F).</summary>
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001c' and <= '\u001f';

    public static string Trim(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var start = 0;
        var end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    public static int ScalarCount(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        foreach (var _ in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    /// <summary>The first <paramref name="limit"/> scalar values of <paramref name="text"/>.</summary>
    public static string CutScalars(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder();
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ == limit)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }

    /// <summary>Trimmed, <paramref name="low"/>–<paramref name="high"/> scalar values and no control characters; else null.</summary>
    public static string? Field(string? value, int low, int high)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = Trim(value);
        var length = ScalarCount(trimmed);
        if (length < low || length > high || trimmed.Any(c => c < ' ' || c == '\u007f'))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>LAN 3.1 <c>name</c>: trimmed, cut to 64 scalar values and trimmed again; empty becomes <c>Agent Speed</c>.</summary>
    public static string ComputerName(string? raw)
    {
        var text = raw is null ? string.Empty : Trim(raw);
        if (ScalarCount(text) > NameLimit)
        {
            text = Trim(CutScalars(text, NameLimit));
        }

        return text.Length == 0 ? DefaultComputerName : text;
    }

    /// <summary>The first <paramref name="limit"/> UTF-8 bytes of <paramref name="text"/>, cut at a scalar boundary (DNS-SD names).</summary>
    public static string CutUtf8(string text, int limit)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            bytes += rune.Utf8SequenceLength;
            if (bytes > limit)
            {
                break;
            }

            builder.Append(rune.ToString());
        }

        return builder.ToString();
    }
}
