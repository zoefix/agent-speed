using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.TaskScore;

/// <summary>Where a score card opens from (DESIGN 12, <i>Placement and timing</i>).</summary>
public enum TaskCardHost
{
    /// <summary>A dashboard agent row: beside the flyout, toward the screen's centre, after 0.45 s.</summary>
    Dashboard,

    /// <summary>A board row or the hero's 智力: beside the board on the side with more room, after 0.35 s.</summary>
    Board,
}

/// <param name="X">The card's resting left edge.</param>
/// <param name="Y">The card's top.</param>
/// <param name="SlideFrom">Where the 2 DIP slide starts, as an offset added to <paramref name="X"/> (toward the row).</param>
/// <param name="OnLeft">The card sits left of its anchor.</param>
public readonly record struct TaskCardSpot(double X, double Y, double SlideFrom, bool OnLeft);

/// <summary>The score card window's size, place and timing (DESIGN 12, <i>Window</i> and <i>Placement and timing</i>).</summary>
public static class TaskCardPlacement
{
    public const double Width = 300;

    /// <summary>Between the card and the panel or board.</summary>
    public const double Gap = 8;

    /// <summary>The card stays this far inside the visible frame.</summary>
    public const double Inset = 8;

    /// <summary>The appearing card slides this far away from the row.</summary>
    public const double Slide = 2;

    public const double DashboardDelay = 0.45;

    /// <summary>The boards' tooltip delay.</summary>
    public const double BoardDelay = 0.35;

    /// <summary>While a card is shown, another row's card replaces it at once with this crossfade.</summary>
    public const double SwitchSeconds = 0.12;

    /// <summary>The card hides this long after the pointer leaves the row.</summary>
    public const double HideDelay = 0.15;

    public const double FadeSeconds = 0.15;

    public static double Delay(TaskCardHost host) => host == TaskCardHost.Dashboard ? DashboardDelay : BoardDelay;

    /// <summary>
    /// Places the card beside <paramref name="anchor"/> (the flyout or the board), its top level with
    /// <paramref name="top"/>, clamped <see cref="Inset"/> inside <paramref name="visible"/>. The dashboard opens toward the
    /// screen's centre and the boards toward the side with more room; a side that cannot hold the card gives way to the other.
    /// </summary>
    public static TaskCardSpot Place(TaskCardHost host, RectD anchor, double top, double cardHeight, RectD visible, double cardWidth = Width)
    {
        var roomLeft = anchor.X - visible.X - Inset - Gap;
        var roomRight = visible.Right - Inset - Gap - anchor.Right;
        var left = host == TaskCardHost.Dashboard ? anchor.Center.X > visible.Center.X : roomLeft > roomRight;
        if (left && roomLeft < cardWidth && roomRight >= cardWidth)
        {
            left = false;
        }
        else if (!left && roomRight < cardWidth && roomLeft >= cardWidth)
        {
            left = true;
        }

        var x = left ? anchor.X - Gap - cardWidth : anchor.Right + Gap;
        x = Clamp(x, visible.X + Inset, visible.Right - Inset - cardWidth);
        var y = Clamp(top, visible.Y + Inset, visible.Bottom - Inset - Math.Max(0, cardHeight));
        return new TaskCardSpot(x, y, left ? Slide : -Slide, left);
    }

    /// <summary>A lower bound wins over an upper bound that crosses it (a card taller than the screen keeps its top).</summary>
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));
}
