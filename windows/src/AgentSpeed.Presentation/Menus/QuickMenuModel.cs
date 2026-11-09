using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;
using AgentSpeed.Presentation.Updates;
using static AgentSpeed.Presentation.Menus.MenuParts;

namespace AgentSpeed.Presentation.Menus;

/// <summary>
/// The quick menu of the tray icon and the taskbar widget (DESIGN 2b, Master switch). With one orb its size, always on
/// top and click-through act on that orb; with several, a <c>settings.orb</c> submenu lists each orb with Show,
/// Click-through, Show Name and its settings, the way back to a click-through orb.
/// </summary>
public static class QuickMenuModel
{
    /// <param name="sources">Source availability for the orbs' colour dots.</param>
    /// <param name="update">The available release; adds the "Update to …" row on top (UPDATES section 4).</param>
    /// <param name="sourceTraffic">Per-process traffic can be measured now (the per-orb traffic submenu says so otherwise).</param>
    /// <param name="report">Live reporting while it is on and registered: its item with a submenu (DESIGN 11, Menu).</param>
    /// <param name="characters">The installed characters (id, localized name) for the <c>character</c> orbs' items.</param>
    public static MenuLevel Build(AppSettings settings, SourceAvailability sources, Localizer localizer, ReleaseInfo? update = null,
        bool sourceTraffic = true, ReportMenuState? report = null, IReadOnlyList<MenuCharacter>? characters = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        var orbs = (settings.Orbs ?? OrbsDocument.Fresh()).Orbs;
        var shown = settings.ShowOrbs && orbs.Any(o => o.Visible);
        var entries = new List<MenuEntry>();
        if (update is not null)
        {
            entries.Add(new MenuRow(UpdateText.MenuItem(update, localizer)) { Action = new MenuAction.OpenUpdate(), Icon = new MenuIcon.AccentDot() });
            entries.Add(MenuSeparator.Instance);
        }

        entries.Add(Command(localizer["menu.dashboard"], new MenuAction.OpenDashboard(), MenuGlyph.Dashboard) with { IsDefault = true });
        entries.Add(Command(localizer["history.open"], new MenuAction.OpenHistory(), MenuGlyph.History));
        if (report is not null)
        {
            entries.Add(MenuSeparator.Instance);
            entries.Add(ReportMenu(report, localizer));
        }

        entries.Add(MenuSeparator.Instance);

        if (orbs.Length == 1)
        {
            var orb = orbs[0];
            var on = settings.ShowOrbs && orb.Visible;
            entries.Add(SizeMenu(orb, localizer, on));
            entries.Add(EffectsMenu(settings.Effects, localizer));
            entries.Add(Toggle(localizer["menu.alwaysOnTop"], orb.AlwaysOnTop, new MenuAction.SetOrbAlwaysOnTop(orb.Id, !orb.AlwaysOnTop), on));
            entries.Add(Toggle(localizer["menu.clickThrough"], orb.ClickThrough, new MenuAction.SetOrbClickThrough(orb.Id, !orb.ClickThrough), on));
        }
        else
        {
            entries.Add(OrbsMenu(settings, orbs, sources, localizer, sourceTraffic, characters ?? []));
            entries.Add(EffectsMenu(settings.Effects, localizer));
        }

        entries.Add(new MenuRow(localizer[MasterSwitchKey(orbs.Length, shown)]) { Action = new MenuAction.SetShowOrbs(!shown) });
        entries.AddRange(BoardRows(settings.Boards ?? BoardsDocument.Empty, localizer));
        entries.Add(Toggle(localizer["settings.bar.show"], settings.ShowOnTaskbar, new MenuAction.SetShowOnTaskbar(!settings.ShowOnTaskbar)));
        entries.Add(MenuSeparator.Instance);
        entries.Add(Command(localizer["menu.settings"], new MenuAction.OpenSettings(null), MenuGlyph.Settings));
        entries.Add(Command(localizer["menu.about"], new MenuAction.OpenSettings("about"), MenuGlyph.About));
        entries.Add(Command(localizer["update.check"] + "…", new MenuAction.CheckForUpdates(), MenuGlyph.CheckForUpdates));
        entries.Add(MenuSeparator.Instance);
        entries.Add(Command(localizer["menu.quit"], new MenuAction.Quit(), MenuGlyph.Quit));
        return new MenuLevel([.. entries]);
    }

    /// <summary>
    /// The boards' rows (DESIGN 2c): <c>boards.hide</c> while any board shows, else <c>boards.show</c> (all boards; none
    /// without a board), then <c>boards.add</c>.
    /// </summary>
    public static IEnumerable<MenuRow> BoardRows(BoardsDocument boards, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(boards);
        ArgumentNullException.ThrowIfNull(localizer);
        if (boards.Count > 0)
        {
            var shown = BoardEditing.AnyVisible(boards);
            yield return new MenuRow(localizer[shown ? "boards.hide" : "boards.show"]) { Action = new MenuAction.SetShowBoards(!shown) };
        }

        yield return BoardMenuModel.AddRow(boards, null, localizer);
    }

    /// <summary><c>report.menu.on</c> (or <c>report.menu.paused</c>) with pause or resume, the leaderboard and the settings.</summary>
    public static MenuRow ReportMenu(ReportMenuState report, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);
        var rows = new List<MenuEntry>();
        if (report.Paused)
        {
            rows.Add(new MenuRow(localizer["report.resume"]) { Action = new MenuAction.ResumeReporting() });
        }
        else
        {
            rows.Add(new MenuRow(localizer["report.pause.hour"]) { Action = new MenuAction.PauseReporting(Indefinitely: false) });
            rows.Add(new MenuRow(localizer["report.pause.indefinite"]) { Action = new MenuAction.PauseReporting(Indefinitely: true) });
        }

        if (report.HasLeaderboard)
        {
            rows.Add(MenuSeparator.Instance);
            rows.Add(new MenuRow(localizer["report.service.leaderboard"]) { Action = new MenuAction.OpenLeaderboard() });
        }

        rows.Add(MenuSeparator.Instance);
        rows.Add(new MenuRow(localizer["report.menu.settings"]) { Action = new MenuAction.OpenSettings("report") });
        var label = report.Paused ? localizer["report.menu.paused"] : localizer.Format("report.menu.on", report.ServiceName);
        return new MenuRow(label) { Icon = new MenuIcon.StatusDot(report.Tone), Submenu = new MenuLevel([.. rows]) };
    }

    /// <summary>
    /// The master switch's label: show or hide, singular with one orb. Orbs count as shown when the switch is on and
    /// at least one orb is visible; turning it on while every orb is hidden shows them all.
    /// </summary>
    public static string MasterSwitchKey(int orbCount, bool shown) => (orbCount > 1, shown) switch
    {
        (false, true) => "menu.hideOrb",
        (false, false) => "menu.showOrb",
        (true, true) => "menu.hideOrbs",
        (true, false) => "menu.showOrbs",
    };

    private static MenuRow OrbsMenu(AppSettings settings, IEnumerable<OrbDefinition> orbs, SourceAvailability sources, Localizer localizer,
        bool sourceTraffic, IReadOnlyList<MenuCharacter> characters) =>
        Submenu(localizer["settings.orb"], orbs.Select(orb =>
        {
            var shown = settings.ShowOrbs && orb.Visible;
            return (MenuEntry)(Submenu(OrbNaming.DisplayName(orb, localizer, RemoteHostStates.Choices(settings.RemoteHosts, localizer)),
            [
                Toggle(localizer["orbs.visible"], shown, new MenuAction.SetOrbVisible(orb.Id, !shown)),
                Toggle(localizer["menu.clickThrough"], orb.ClickThrough, new MenuAction.SetOrbClickThrough(orb.Id, !orb.ClickThrough)),
                ShowNameToggle(orb, settings, localizer),
                .. orb.Style == OrbStyle.Character
                    ? new MenuEntry[] { OrbMenuModel.CharacterMenu(orb, characters, localizer), OrbMenuModel.ReadoutsMenu(orb, settings, localizer) }
                    : [OrbMenuModel.UnitMenu(orb, settings, localizer), OrbMenuModel.TrafficMenu(orb, settings, localizer, sourceTraffic)],
                MenuSeparator.Instance,
                Command(localizer["menu.orb.settings"], new MenuAction.OpenOrbSettings(orb.Id), MenuGlyph.Settings),
            ]) with
            {
                Icon = DotsOf(orb, sources),
                Detail = orb.Visible ? null : localizer["orbs.hidden"],
            });
        }));
}
