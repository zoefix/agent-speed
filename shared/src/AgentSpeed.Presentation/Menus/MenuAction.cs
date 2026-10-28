using AgentSpeed.Core;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Settings;

namespace AgentSpeed.Presentation.Menus;

/// <summary>What a menu row does when it is chosen; the app maps each case to one command.</summary>
public abstract record MenuAction
{
    private MenuAction()
    {
    }

    public sealed record OpenUpdate : MenuAction;

    public sealed record OpenDashboard : MenuAction;

    /// <summary>The Usage History window for all sources.</summary>
    public sealed record OpenHistory : MenuAction;

    /// <summary>Settings, at <paramref name="Page"/> (null: the last page).</summary>
    public sealed record OpenSettings(string? Page) : MenuAction;

    public sealed record CheckForUpdates : MenuAction;

    public sealed record Quit : MenuAction;

    /// <summary>The global Effects intensity.</summary>
    public sealed record SetEffects(EffectsIntensity Level) : MenuAction;

    public sealed record SetShowOnTaskbar(bool Show) : MenuAction;

    /// <summary>The master switch of every orb.</summary>
    public sealed record SetShowOrbs(bool Show) : MenuAction;

    public sealed record SetOrbStyle(string OrbId, OrbStyle Style) : MenuAction;

    /// <summary>tok/s or the money unit of one orb (DESIGN 9 Orb, Switching 2).</summary>
    public sealed record SetOrbUnit(string OrbId, Settings.Orbs.OrbUnit Unit) : MenuAction;

    /// <summary>What one orb's traffic readout shows (DESIGN 10).</summary>
    public sealed record SetOrbTraffic(string OrbId, Settings.Orbs.OrbTraffic Traffic) : MenuAction;

    public sealed record SetOrbAllSources(string OrbId) : MenuAction;

    public sealed record ToggleOrbSource(string OrbId, SourceKind Source) : MenuAction;

    public sealed record SetOrbSize(string OrbId, double Size) : MenuAction;

    public sealed record SetOrbAlwaysOnTop(string OrbId, bool On) : MenuAction;

    public sealed record SetOrbClickThrough(string OrbId, bool On) : MenuAction;

    public sealed record SetOrbVisible(string OrbId, bool Visible) : MenuAction;

    /// <summary>Stores this orb's name plate as shown or hidden (<c>orbs.showName</c>; automatic is restored in Settings).</summary>
    public sealed record SetOrbShowName(string OrbId, bool Show) : MenuAction;

    public sealed record AddOrb(string FromOrbId) : MenuAction;

    public sealed record DuplicateOrb(string OrbId) : MenuAction;

    public sealed record SplitOrb(string OrbId) : MenuAction;

    public sealed record MergeOrbs(string KeepOrbId) : MenuAction;

    /// <summary>Removes the orb after asking (the menu has no undo banner).</summary>
    public sealed record RemoveOrb(string OrbId) : MenuAction;

    public sealed record OpenOrbSettings(string OrbId) : MenuAction;

    /// <summary>Pauses live reporting for an hour, or (<paramref name="Indefinitely"/>) until the user resumes (DESIGN 11).</summary>
    public sealed record PauseReporting(bool Indefinitely) : MenuAction;

    public sealed record ResumeReporting : MenuAction;

    /// <summary>Opens the reporting service's leaderboard in the browser (an <c>https</c> link only).</summary>
    public sealed record OpenLeaderboard : MenuAction;
}
