namespace AgentSpeed.Presentation.Motion;

/// <summary>How the agents list changes between collapsed and expanded.</summary>
public enum DisclosureTransition
{
    /// <summary>Jumps to the new state (the flyout is hidden).</summary>
    Instant,

    /// <summary>The viewport and window spring to their new heights.</summary>
    Spring,

    /// <summary>Reduce Motion: heights change at once and only the rows that come or go fade.</summary>
    Crossfade,
}

/// <summary>Rules for opening and closing the dashboard agents list (DESIGN section 4, item 4, and section 7).</summary>
public static class AgentsDisclosure
{
    /// <summary>Opacity changes take 0.2 s, ease-out (DESIGN section 6).</summary>
    public const double CrossfadeSeconds = 0.2;

    public static DisclosureTransition TransitionFor(bool visible, bool reduceMotion) =>
        !visible ? DisclosureTransition.Instant : reduceMotion ? DisclosureTransition.Crossfade : DisclosureTransition.Spring;

    /// <summary>
    /// Scroll offset while the list closes: the offset it had when closing began, shrinking with the remaining height,
    /// so the first rows slide back into view as the viewport reaches its collapsed height.
    /// </summary>
    public static double CollapseOffset(double fromOffset, double fromViewport, double toViewport, double viewport)
    {
        if (!double.IsFinite(fromOffset) || fromOffset <= 0)
        {
            return 0;
        }

        var span = fromViewport - toViewport;
        var remaining = span > 0 && double.IsFinite(viewport) ? Math.Clamp((viewport - toViewport) / span, 0, 1) : 0;
        return fromOffset * remaining;
    }

    /// <summary>Rows that appear on expanding and leave on collapsing: every row past the collapsed list.</summary>
    public static (int First, int Count) ChangingRows(int collapsedRows, int shownRows)
    {
        var first = Math.Max(0, collapsedRows);
        return (first, Math.Max(0, shownRows - first));
    }
}
