using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Orb.Names;

/// <summary>When an orb carries its name plate (DESIGN 2b, Name plate, When it shows).</summary>
public static class NamePlateRules
{
    /// <summary>No plate below this size (<c>minimal</c> only): it would outweigh the orb's 7 pt title.</summary>
    public const double MinSize = 80;

    /// <summary>
    /// What the global setting decides: <c>"always"</c>, <c>"never"</c>, or automatically when two or more orbs are
    /// visible or the orb tracks some (not all) sources.
    /// </summary>
    public static bool Automatic(OrbDefinition orb, OrbNamesMode mode, int visibleOrbs)
    {
        ArgumentNullException.ThrowIfNull(orb);
        return mode switch
        {
            OrbNamesMode.Always => true,
            OrbNamesMode.Never => false,
            _ => visibleOrbs >= 2 || !orb.Sources.TracksAll,
        };
    }

    /// <summary>The orb's own <c>showName</c>, else the global setting.</summary>
    public static bool Wants(OrbDefinition orb, OrbNamesMode mode, int visibleOrbs)
    {
        ArgumentNullException.ThrowIfNull(orb);
        return orb.ShowName ?? Automatic(orb, mode, visibleOrbs);
    }

    /// <summary>
    /// Whether the orb's plate shows while the orb is visible (the menus' check mark): wanted and at least 80 pt. The
    /// visible orbs are counted with this one shown.
    /// </summary>
    public static bool IsOn(OrbDefinition orb, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(orb);
        return orb.Size >= MinSize && Wants(orb, Mode(settings), VisibleWith(settings, orb));
    }

    /// <summary>The result of following the global setting (the editor's <c>orbs.showName.auto</c>).</summary>
    public static bool AutomaticResult(OrbDefinition orb, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(orb);
        return orb.Size >= MinSize && Automatic(orb, Mode(settings), VisibleWith(settings, orb));
    }

    /// <summary>The plate is drawn: the master switch and the orb are on, and <see cref="IsOn"/>.</summary>
    public static bool Shows(OrbDefinition orb, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(orb);
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ShowOrbs && orb.Visible && IsOn(orb, settings);
    }

    /// <summary>The orbs visible on the desktop.</summary>
    public static int VisibleCount(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.ShowOrbs ? (settings.Orbs ?? OrbsDocument.Fresh()).Orbs.Count(o => o.Visible) : 0;
    }

    /// <summary>The value stored by the menus' <c>orbs.showName</c>: the opposite of the effective state.</summary>
    public static bool Toggled(OrbDefinition orb, AppSettings settings) => !IsOn(orb, settings);

    private static OrbNamesMode Mode(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.OrbNames;
    }

    /// <summary>The orbs that would be visible with this one shown (the master switch shows every visible orb).</summary>
    private static int VisibleWith(AppSettings settings, OrbDefinition orb) =>
        (settings.Orbs ?? OrbsDocument.Fresh()).Orbs.Count(o => o.Visible || string.Equals(o.Id, orb.Id, StringComparison.Ordinal));
}
