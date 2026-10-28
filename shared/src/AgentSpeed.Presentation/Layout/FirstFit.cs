namespace AgentSpeed.Presentation.Layout;

/// <summary>
/// Picks one of several forms of a text, longest first, by width (DESIGN 4b: a title or note is shortened, never
/// truncated): the first that fits, else the last.
/// </summary>
public static class FirstFit
{
    /// <summary>Slack for layout rounding: a form this much wider than the room still counts as fitting.</summary>
    public const double Tolerance = 0.01;

    public static bool Fits(double width, double available) =>
        !double.IsNaN(width) && (!double.IsFinite(available) || width <= available + Tolerance);

    /// <summary>
    /// Index of the first width that fits in <paramref name="available"/> (any, when it is unbounded); the last offered one
    /// when none fits. A NaN width is a form not offered; -1 when no form is offered.
    /// </summary>
    public static int Index(IReadOnlyList<double> widths, double available)
    {
        ArgumentNullException.ThrowIfNull(widths);
        var last = -1;
        for (var i = 0; i < widths.Count; i++)
        {
            if (double.IsNaN(widths[i]))
            {
                continue;
            }

            if (Fits(widths[i], available))
            {
                return i;
            }

            last = i;
        }

        return last;
    }
}
