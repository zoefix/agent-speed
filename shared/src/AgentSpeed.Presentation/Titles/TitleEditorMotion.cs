using AgentSpeed.Presentation.Heat;

namespace AgentSpeed.Presentation.Titles;

/// <summary>
/// Motion and preview script of the title theme settings (DESIGN 5, Title themes): the card and editor springs, the
/// previews' scripted level and the sticky preview column.
/// </summary>
public static class TitleEditorMotion
{
    /// <summary>A new card and the editor height: a 0.35 s spring.</summary>
    public const double SpringResponse = 0.35;

    public const double SpringDamping = 0.8;

    /// <summary>The level the previews show while no field has the focus.</summary>
    public const int RestingLevel = TitlePresets.LevelCount;

    /// <summary>The previews run a level at this multiple of its threshold.</summary>
    public const double PreviewHeadroom = 1.05;

    /// <summary>
    /// Step response (0 → 1) of a spring with <paramref name="response"/> seconds per oscillation and the damping ratio
    /// <paramref name="damping"/>, <paramref name="seconds"/> after it started.
    /// </summary>
    public static double Spring(double seconds, double response = SpringResponse, double damping = SpringDamping)
    {
        if (seconds <= 0 || !double.IsFinite(seconds))
        {
            return seconds > 0 ? 1 : 0;
        }

        var omega = 2 * Math.PI / Math.Max(response, 1e-3);
        if (damping >= 1)
        {
            return 1 - Math.Exp(-omega * seconds) * (1 + omega * seconds);
        }

        var root = Math.Sqrt(1 - damping * damping);
        var omegaD = omega * root;
        var envelope = Math.Exp(-damping * omega * seconds);
        return 1 - envelope * (Math.Cos(omegaD * seconds) + damping / root * Math.Sin(omegaD * seconds));
    }

    /// <summary>Seconds until the spring stays within 0.2 % of its target.</summary>
    public static double SettleTime(double response = SpringResponse, double damping = SpringDamping)
    {
        var omega = 2 * Math.PI / Math.Max(response, 1e-3);
        var zeta = Math.Clamp(damping, 0.05, 0.999);
        return Math.Log(1 / (0.002 * Math.Sqrt(1 - zeta * zeta))) / (zeta * omega);
    }

    /// <summary>The previews' level: the focused row's (0 = idle), else <see cref="RestingLevel"/>.</summary>
    public static int PreviewLevel(int? focusedLevel) =>
        focusedLevel is { } level ? Math.Clamp(level, 0, TitlePresets.LevelCount) : RestingLevel;

    /// <summary>The previews' value for a level: 1.05 × its threshold at <paramref name="redline"/>; 0 for idle.</summary>
    public static double PreviewValue(int level, double redline) =>
        level <= 0 ? 0 : SpeedTitles.Threshold(TitleSettingsText.LevelTitle(level), redline) * PreviewHeadroom;

    /// <summary>
    /// How far the preview column moves down to stay in view while the page scrolls: nothing while its own place is in
    /// view, then it follows the viewport's top plus <paramref name="inset"/>, never past the bottom of its container.
    /// Every coordinate is in the same space (the scrolled content).
    /// </summary>
    public static double StickyOffset(double naturalTop, double height, double containerBottom, double viewportTop, double inset)
    {
        var wanted = viewportTop + inset - naturalTop;
        var limit = containerBottom - height - naturalTop;
        return Math.Clamp(wanted, 0, Math.Max(0, limit));
    }
}
