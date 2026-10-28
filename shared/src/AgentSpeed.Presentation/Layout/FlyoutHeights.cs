namespace AgentSpeed.Presentation.Layout;

/// <summary>
/// The two heights that change while the dashboard agents list opens or closes (DESIGN section 4, item 4): the agents
/// viewport and the flyout window. The window hugs its content up to the screen cap and every other section keeps its
/// height, so both targets are known before the first frame and nothing drawn is measured back into the animation.
/// </summary>
/// <param name="Viewport">Agents viewport height in DIPs.</param>
/// <param name="Window">Flyout window height in DIPs.</param>
public readonly record struct FlyoutHeights(double Viewport, double Window)
{
    /// <summary>Slack before a fraction of a pixel counts as one more pixel, so float noise never adds a row.</summary>
    private const double PixelSlack = 0.001;

    /// <summary>The window height for content of a natural height: the content, up to <paramref name="max"/>.</summary>
    public static double Fit(double content, double max)
    {
        if (!double.IsFinite(content) || content <= 0)
        {
            return 0;
        }

        return double.IsNaN(max) ? content : Math.Min(content, Math.Max(0, max));
    }

    /// <summary>Heights for a viewport of <paramref name="viewport"/> DIPs inside the rest of the content (<paramref name="chrome"/>).</summary>
    public static FlyoutHeights For(double chrome, double viewport, double max)
    {
        viewport = double.IsFinite(viewport) ? Math.Max(0, viewport) : 0;
        chrome = double.IsFinite(chrome) ? Math.Max(0, chrome) : 0;
        return new FlyoutHeights(viewport, Fit(chrome + viewport, max));
    }

    /// <summary>
    /// Both heights on whole device pixels, rounded the same way. Content laid out at the snapped viewport height then
    /// fills the snapped window exactly, so whatever sits between the list and the anchored edge keeps its pixel on every
    /// frame instead of wobbling by one as the two round apart.
    /// </summary>
    public FlyoutHeights SnapToPixels(double scale) =>
        double.IsFinite(scale) && scale > 0 ? new FlyoutHeights(Snap(Viewport, scale), Snap(Window, scale)) : this;

    /// <summary>
    /// Whole device pixels covering <paramref name="dip"/> at <paramref name="scale"/>. Rounding up keeps the window at
    /// least as tall as its content, so the outer scroll bar never appears over a sub-pixel overflow.
    /// </summary>
    public static double ToPixels(double dip, double scale)
    {
        if (!double.IsFinite(dip) || !double.IsFinite(scale) || dip <= 0 || scale <= 0)
        {
            return 0;
        }

        return Math.Max(0, Math.Ceiling(dip * scale - PixelSlack));
    }

    private static double Snap(double dip, double scale) =>
        double.IsFinite(dip) ? Math.Round(Math.Max(0, dip) * scale, MidpointRounding.AwayFromZero) / scale : 0;
}
