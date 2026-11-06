using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Remote;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;
using static AgentSpeed.Presentation.Menus.MenuParts;

namespace AgentSpeed.Presentation.Menus;

/// <summary>
/// The context menu of one board (DESIGN 2c, Interaction and window): header (style name and sources);
/// <c>boards.style</c> ▸, <c>orbs.sources</c> ▸, <c>boards.readouts</c> ▸ (the live metrics), <c>menu.size</c> ▸ S / M / L, <c>menu.effects</c> ▸ (global);
/// <c>menu.alwaysOnTop</c>, <c>menu.clickThrough</c>; <c>boards.add</c>, <c>boards.remove</c>; <c>menu.dashboard</c>,
/// <c>boards.settings</c>.
/// </summary>
public static class BoardMenuModel
{
    private static readonly (BoardSize Size, string Key)[] Sizes =
    [
        (BoardSize.S, "menu.size.small"),
        (BoardSize.M, "menu.size.medium"),
        (BoardSize.L, "menu.size.large"),
    ];

    /// <summary>The menu of <paramref name="boardId"/>; null when no such board exists.</summary>
    /// <param name="hosts">Each host's dot and word for the Hosts submenu (DESIGN 13), by id.</param>
    public static MenuLevel? Build(string boardId, AppSettings settings, SourceAvailability sources, Localizer localizer,
        IReadOnlyDictionary<string, MenuHostState>? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);
        var document = settings.Boards ?? BoardsDocument.Empty;
        if (document.Find(boardId) is not { } board)
        {
            return null;
        }

        var id = board.Id;
        var hostsMenu = HostsMenu(board.Hosts, new MenuAction.SetBoardAllHosts(id), host => new MenuAction.ToggleBoardHost(id, host), settings,
            hosts, localizer);
        return new MenuLevel(
        [
            new MenuRow(Title(board, localizer, RemoteHostStates.Choices(settings.RemoteHosts, localizer)))
            {
                Icon = DotsOf(board.Sources, sources), IsHeader = true, IsEnabled = false,
            },
            MenuSeparator.Instance,
            Submenu(localizer["boards.style"], BoardStyles.All.Select(style =>
                (MenuEntry)Toggle(StyleName(style, localizer), board.Style == style, new MenuAction.SetBoardStyle(id, style)))),
            SourcesMenu(board.Sources, new MenuAction.SetBoardAllSources(id), kind => new MenuAction.ToggleBoardSource(id, kind), sources, localizer),
            .. hostsMenu is null ? Array.Empty<MenuEntry>() : [hostsMenu],
            ReadoutsMenu(board, localizer),
            Submenu(localizer["menu.size"], Sizes.Select(s =>
                (MenuEntry)Toggle(localizer[s.Key], board.Size == s.Size, new MenuAction.SetBoardSize(id, s.Size)))),
            EffectsMenu(settings.Effects, localizer),
            MenuSeparator.Instance,
            Toggle(localizer["menu.alwaysOnTop"], board.AlwaysOnTop, new MenuAction.SetBoardAlwaysOnTop(id, !board.AlwaysOnTop)),
            Toggle(localizer["menu.clickThrough"], board.ClickThrough, new MenuAction.SetBoardClickThrough(id, !board.ClickThrough)),
            MenuSeparator.Instance,
            AddRow(document, id, localizer),
            new MenuRow(localizer["boards.remove"]) { Action = new MenuAction.RemoveBoard(id) },
            MenuSeparator.Instance,
            Command(localizer["menu.dashboard"], new MenuAction.OpenDashboard(), MenuGlyph.Dashboard),
            Command(localizer["boards.settings"], new MenuAction.OpenBoardSettings(id), MenuGlyph.Settings),
        ]);
    }

    /// <summary><c>boards.readouts</c> ▸ the four live metrics switches in order, each with a check; choosing one toggles it.</summary>
    public static MenuRow ReadoutsMenu(BoardDefinition board, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(localizer);
        return Submenu(localizer["boards.readouts"], BoardReadouts.Order.Select(readout =>
            (MenuEntry)Toggle(localizer[BoardReadouts.ItemKey(readout)], board.Readouts[readout],
                new MenuAction.SetBoardReadout(board.Id, readout, !board.Readouts[readout]))));
    }

    /// <summary><c>boards.add</c> (from a board, an orb or nowhere); disabled at four boards with <c>boards.limit</c>.</summary>
    public static MenuRow AddRow(BoardsDocument? document, string? fromId, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        var canAdd = (document ?? BoardsDocument.Empty).CanAdd;
        return new MenuRow(localizer["boards.add"])
        {
            Action = new MenuAction.AddBoard(fromId),
            IsEnabled = canAdd,
            ToolTip = canAdd ? null : localizer.Format("boards.limit", BoardsDocument.MaxBoards),
        };
    }

    /// <summary>
    /// The header: the style's name, the board's sources and, while remote hosts exist, its hosts
    /// (<c>霓虹座艙 · 全部來源 · devbox +1</c>; <paramref name="hosts"/> from <see cref="RemoteHostStates.Choices"/>).
    /// </summary>
    public static string Title(BoardDefinition board, Localizer localizer, IReadOnlyList<OrbHostChoice>? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(localizer);
        return StyleName(board.Style, localizer) + " · " + OrbNaming.AutomaticName(board.Sources, localizer)
            + (hosts is null ? string.Empty : OrbNaming.HostSuffix(board.Hosts, hosts, localizer));
    }

    public static string StyleName(BoardStyle style, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer["boards.style." + BoardStyles.Key(style)];
    }
}
