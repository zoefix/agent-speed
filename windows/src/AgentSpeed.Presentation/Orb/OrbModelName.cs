using System.Globalization;
using System.Text;

namespace AgentSpeed.Presentation.Orb;

/// <summary>A model's name as the models card shows it (DESIGN 2a.6, Models): its id without a trailing date, cut at its end to fit.</summary>
public static class OrbModelName
{
    public const string Ellipsis = "…";

    /// <summary>The date forms, <c>d</c> a digit.</summary>
    private static readonly string[] DateForms = ["dddddddd", "dddd-dd-dd"];

    /// <summary>
    /// The id without a trailing <c>-YYYYMMDD</c> or <c>-YYYY-MM-DD</c> (<c>claude-sonnet-4-5-20250929</c> →
    /// <c>claude-sonnet-4-5</c>); an id that is only a date stays as it is.
    /// </summary>
    public static string Display(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (var form in DateForms)
        {
            var cut = id.Length - form.Length - 1;
            if (cut > 0 && id[cut] == '-' && Matches(id.AsSpan(cut + 1), form))
            {
                return id[..cut];
            }
        }

        return id;
    }

    /// <summary>
    /// <paramref name="text"/> when its elements fit <paramref name="max"/>; else as many whole text elements from its start
    /// as fit before the ellipsis. <paramref name="measure"/> gives one text element's advance.
    /// </summary>
    public static string Truncate(string text, double max, Func<string, double> measure)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(measure);
        var elements = new List<string>(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            elements.Add(e.GetTextElement());
        }

        var widths = elements.Select(measure).ToArray();
        if (widths.Sum() <= max)
        {
            return text;
        }

        var room = max - measure(Ellipsis);
        var builder = new StringBuilder(text.Length);
        var used = 0.0;
        for (var i = 0; i < elements.Count && used + widths[i] <= room; i++)
        {
            used += widths[i];
            builder.Append(elements[i]);
        }

        return builder.Append(Ellipsis).ToString();
    }

    private static bool Matches(ReadOnlySpan<char> text, string form)
    {
        for (var i = 0; i < form.Length; i++)
        {
            if (form[i] == 'd' ? !char.IsAsciiDigit(text[i]) : text[i] != form[i])
            {
                return false;
            }
        }

        return true;
    }
}
