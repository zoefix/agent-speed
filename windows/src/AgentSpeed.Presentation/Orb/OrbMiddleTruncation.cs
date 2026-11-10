using System.Globalization;
using System.Text;

namespace AgentSpeed.Presentation.Orb;

/// <summary>Cuts a text in the middle with an ellipsis so it fits a width (the models card's ids, DESIGN 2a.6).</summary>
public static class OrbMiddleTruncation
{
    public const string Ellipsis = "…";

    /// <summary>
    /// <paramref name="text"/> when its elements fit <paramref name="max"/>; else as many whole text elements from both ends
    /// as fit around the ellipsis, the start first. <paramref name="measure"/> gives one text element's advance.
    /// </summary>
    public static string Fit(string text, double max, Func<string, double> measure)
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
        int head = 0, tail = 0;
        var used = 0.0;
        while (head + tail < elements.Count)
        {
            var fromHead = head <= tail;
            var next = fromHead ? widths[head] : widths[elements.Count - 1 - tail];
            if (used + next > room)
            {
                break;
            }

            used += next;
            if (fromHead)
            {
                head++;
            }
            else
            {
                tail++;
            }
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < head; i++)
        {
            builder.Append(elements[i]);
        }

        builder.Append(Ellipsis);
        for (var i = elements.Count - tail; i < elements.Count; i++)
        {
            builder.Append(elements[i]);
        }

        return builder.ToString();
    }
}
