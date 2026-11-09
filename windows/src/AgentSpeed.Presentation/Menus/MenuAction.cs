using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
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

    /// <summary>A <c>character</c> orb's character: a pack id, or null for Auto (CHARACTERS 7).</summary>
    public sealed record SetOrbCharacter(string OrbId, string? Character) : MenuAction;

    /// <summary>One card of a <c>character</c> orb (DESIGN 2a.6, Readouts).</summary>
    public sealed record SetOrbCharacterReadout(string OrbId, Settings.Orbs.CharacterReadout Readout, bool On) : MenuAction;

    public sealed record SetOrbAllSources(string OrbId) : MenuAction;

    public sealed record ToggleOrbSource(string OrbId, SourceKind Source) : MenuAction;

    /// <summary>The orb tracks every host again (DESIGN 13).</summary>
    public sealed record SetOrbAllHosts(string OrbId) : MenuAction;

    /// <summary>Checks or unchecks one host of the orb (<c>local</c> or a remote host id; never the last one).</summary>
    public sealed record ToggleOrbHost(string OrbId, string HostId) : MenuAction;

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

    /// <summary><c>boards.show</c> / <c>boards.hide</c>: every board at once.</summary>
    public sealed record SetShowBoards(bool Show) : MenuAction;

    public sealed record SetBoardStyle(string BoardId, BoardStyle Style) : MenuAction;

    public sealed record SetBoardAllSources(string BoardId) : MenuAction;

    public sealed record ToggleBoardSource(string BoardId, SourceKind Source) : MenuAction;

    public sealed record SetBoardAllHosts(string BoardId) : MenuAction;

    public sealed record ToggleBoardHost(string BoardId, string HostId) : MenuAction;

    public sealed record SetBoardSize(string BoardId, BoardSize Size) : MenuAction;

    /// <summary>One of the board's live metrics switches (<c>boards.readouts</c> ▸), on or off.</summary>
    public sealed record SetBoardReadout(string BoardId, BoardReadout Readout, bool On) : MenuAction;

    public sealed record SetBoardAlwaysOnTop(string BoardId, bool On) : MenuAction;

    public sealed record SetBoardClickThrough(string BoardId, bool On) : MenuAction;

    /// <summary><c>boards.add</c> next to the window it came from (an orb or a board id; null: the main display).</summary>
    public sealed record AddBoard(string? FromId) : MenuAction;

    /// <summary>Removes the board after asking (<c>boards.remove.confirm</c>).</summary>
    public sealed record RemoveBoard(string BoardId) : MenuAction;

    /// <summary>Settings → 懸浮窗, Boards, with this board selected (null: the list).</summary>
    public sealed record OpenBoardSettings(string? BoardId) : MenuAction;

    /// <summary>Pauses live reporting for an hour, or (<paramref name="Indefinitely"/>) until the user resumes (DESIGN 11).</summary>
    public sealed record PauseReporting(bool Indefinitely) : MenuAction;

    public sealed record ResumeReporting : MenuAction;

    /// <summary>Opens the reporting service's leaderboard in the browser (an <c>https</c> link only).</summary>
    public sealed record OpenLeaderboard : MenuAction;
}
