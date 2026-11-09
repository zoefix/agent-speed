using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Menus;

/// <summary>
/// Actions the app's menus can trigger. The orb members have defaults so the app builds before it implements them:
/// plain settings edits go through <see cref="UpdateSettings"/>, the operations that need the app (placement, undo,
/// confirmation, the chip) do nothing until the app implements them.
/// </summary>
internal interface IAppCommands
{
    void ToggleDashboardFromMenu();

    /// <summary>Opens the Usage History window, filtered to one source or (null) all sources.</summary>
    void OpenHistory(SourceKind? source = null);

    void UpdateSettings(Func<AppSettings, AppSettings> change);

    void OpenSettings(string? page = null);

    /// <summary>Opens the update window for the available release.</summary>
    void OpenUpdate();

    /// <summary>Manual check that reports its result in the update window.</summary>
    void CheckForUpdates();

    void Quit();

    /// <summary>The master switch; turning it on while every orb is hidden shows them all.</summary>
    void SetShowOrbs(bool show) => UpdateSettings(s => s.WithShowOrbs(show));

    /// <summary>Changes an orb's style; its size is clamped to the style's range.</summary>
    void SetOrbStyle(string orbId, OrbStyle style) => UpdateOrbs(d => OrbEditing.SetStyle(d, orbId, style));

    /// <summary>tok/s or the money unit of one orb (DESIGN 9 Orb, Switching).</summary>
    void SetOrbUnit(string orbId, OrbUnit unit) => UpdateOrbs(d => OrbEditing.SetUnit(d, orbId, unit));

    void SetOrbTraffic(string orbId, OrbTraffic traffic) => UpdateOrbs(d => OrbEditing.SetTraffic(d, orbId, traffic));

    /// <summary>A <c>character</c> orb's character: a pack id, or null for Auto (CHARACTERS 7).</summary>
    void SetOrbCharacter(string orbId, string? character) => UpdateOrbs(d => OrbEditing.SetCharacter(d, orbId, character));

    /// <summary>One card of a <c>character</c> orb (DESIGN 2a.6, Readouts).</summary>
    void SetOrbCharacterReadout(string orbId, CharacterReadout readout, bool on) => UpdateOrbs(d => OrbEditing.SetCharacterReadout(d, orbId, readout, on));

    /// <summary>The installed characters for the menus (id, localized name), in catalog order.</summary>
    IReadOnlyList<Presentation.Menus.MenuCharacter> InstalledCharacters => [];

    /// <summary>The orb tracks every source again.</summary>
    void SetOrbAllSources(string orbId) => UpdateOrbs(d => OrbEditing.SetAllSources(d, orbId));

    /// <summary>Checks or unchecks one source of the orb (an all-sources orb becomes that source; never the last one).</summary>
    void ToggleOrbSource(string orbId, SourceKind source) => UpdateOrbs(d => OrbEditing.ToggleSource(d, orbId, source));

    /// <summary>The orb tracks every host again (DESIGN 13).</summary>
    void SetOrbAllHosts(string orbId) => UpdateOrbs(d => OrbEditing.SetHosts(d, orbId, OrbHosts.All));

    /// <summary>Checks or unchecks one host of the orb (an all-hosts orb becomes that host; never the last one).</summary>
    void ToggleOrbHost(string orbId, string hostId) =>
        UpdateSettings(s => OrbEditing.ToggleHost(s.Orbs, orbId, hostId, RemoteHostStates.Order(s.RemoteHosts)) is { } next ? s with { Orbs = next } : s);

    /// <summary>Sets an orb's diameter (DIPs), clamped to its style's range.</summary>
    void SetOrbSize(string orbId, double size) => UpdateOrbs(d => OrbEditing.SetSize(d, orbId, size));

    void SetOrbAlwaysOnTop(string orbId, bool on) => UpdateOrbs(d => OrbEditing.SetAlwaysOnTop(d, orbId, on));

    void SetOrbClickThrough(string orbId, bool on) => UpdateOrbs(d => OrbEditing.SetClickThrough(d, orbId, on));

    /// <summary>Shows or hides one orb; showing it also turns the master switch on.</summary>
    void SetOrbVisible(string orbId, bool visible) => UpdateSettings(s =>
    {
        var next = s with { Orbs = OrbEditing.SetVisible(s.Orbs, orbId, visible) };
        return visible ? next with { ShowOrbs = true } : next;
    });

    /// <summary>Shows or hides this orb's name plate (<c>orbs.showName</c>); automatic is restored in Settings.</summary>
    void SetOrbShowName(string orbId, bool show) => UpdateOrbs(d => OrbEditing.SetShowName(d, orbId, show));

    /// <summary>Adds an orb with the look of <paramref name="fromOrbId"/> next to it (<see cref="OrbEditing.Add"/>), with its chip.</summary>
    void AddOrb(string fromOrbId)
    {
    }

    /// <summary>Adds an exact copy next to the orb (<see cref="OrbEditing.Duplicate"/>), with its chip.</summary>
    void DuplicateOrb(string orbId)
    {
    }

    /// <summary>One orb per available source of this orb, laid out in a row, with undo (<see cref="OrbEditing.Split"/>).</summary>
    void SplitOrb(string orbId)
    {
    }

    /// <summary>Replaces every orb by one all-sources orb that keeps this one's id and look, with undo (<see cref="OrbEditing.Merge"/>).</summary>
    void MergeOrbs(string keepOrbId)
    {
    }

    /// <summary>Asks first (<see cref="OrbMenu.ConfirmRemove"/>), then removes the orb, with undo (<see cref="OrbEditing.Remove"/>).</summary>
    void RemoveOrb(string orbId)
    {
    }

    /// <summary><c>boards.show</c> / <c>boards.hide</c>: every board at once.</summary>
    void SetShowBoards(bool show) => UpdateBoards(d => BoardEditing.SetAllVisible(d, show));

    void SetBoardStyle(string boardId, BoardStyle style) => UpdateBoards(d => BoardEditing.SetStyle(d, boardId, style));

    void SetBoardAllSources(string boardId) => UpdateBoards(d => BoardEditing.SetAllSources(d, boardId));

    /// <summary>Checks or unchecks one source of the board (never the last one).</summary>
    void ToggleBoardSource(string boardId, SourceKind source) => UpdateBoards(d => BoardEditing.ToggleSource(d, boardId, source));

    void SetBoardAllHosts(string boardId) => UpdateBoards(d => BoardEditing.SetHosts(d, boardId, OrbHosts.All));

    /// <summary>Checks or unchecks one host of the board (never the last one).</summary>
    void ToggleBoardHost(string boardId, string hostId) =>
        UpdateSettings(s => BoardEditing.ToggleHost(s.Boards, boardId, hostId, RemoteHostStates.Order(s.RemoteHosts)) is { } next
            ? s with { Boards = next }
            : s);

    void SetBoardSize(string boardId, BoardSize size) => UpdateBoards(d => BoardEditing.SetSize(d, boardId, size));

    void SetBoardReadout(string boardId, BoardReadout readout, bool on) => UpdateBoards(d => BoardEditing.SetReadout(d, boardId, readout, on));

    void SetBoardAlwaysOnTop(string boardId, bool on) => UpdateBoards(d => BoardEditing.SetAlwaysOnTop(d, boardId, on));

    void SetBoardClickThrough(string boardId, bool on) => UpdateBoards(d => BoardEditing.SetClickThrough(d, boardId, on));

    /// <summary><c>boards.add</c> next to the window it came from (an orb or board id; null: the main display).</summary>
    void AddBoard(string? fromId)
    {
    }

    /// <summary>Asks first (<c>boards.remove.confirm</c>), then removes the board.</summary>
    void RemoveBoard(string boardId)
    {
    }

    /// <summary>Opens Settings → floating orbs at the Boards list, with this board selected.</summary>
    void OpenBoardSettings(string? boardId) => OpenSettings("orb");

    /// <summary>Pauses live reporting for an hour, or until the user resumes (DESIGN 11, Menu).</summary>
    void PauseReporting(bool indefinitely)
    {
    }

    void ResumeReporting()
    {
    }

    /// <summary>Opens the reporting service's leaderboard in the browser.</summary>
    void OpenReportLeaderboard()
    {
    }

    /// <summary>Opens Settings → floating orbs with this orb selected.</summary>
    void OpenOrbSettings(string orbId) => OpenSettings("orb");

    private void UpdateOrbs(Func<OrbsDocument, OrbsDocument?> change) =>
        UpdateSettings(s => change(s.Orbs) is { } next ? s with { Orbs = next } : s);

    private void UpdateBoards(Func<BoardsDocument, BoardsDocument?> change) =>
        UpdateSettings(s => change(s.Boards) is { } next ? s with { Boards = next } : s);
}
