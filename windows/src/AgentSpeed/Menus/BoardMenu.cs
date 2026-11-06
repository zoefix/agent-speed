using System.Windows;
using System.Windows.Controls;
using AgentSpeed.Controls;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Presentation.Settings.Boards;

namespace AgentSpeed.Menus;

/// <summary>The right-click menu of a leaderboard board (DESIGN 2c, Interaction and window; <see cref="BoardMenuModel"/>).</summary>
internal static class BoardMenu
{
    /// <summary>The menu of <paramref name="boardId"/>; null when no such board exists.</summary>
    /// <param name="hosts">Each host's dot and word for the Hosts submenu (DESIGN 13).</param>
    public static ContextMenu? Build(IAppCommands commands, string boardId, AppSettings settings, SourceAvailability sources, Localizer loc,
        IReadOnlyDictionary<string, MenuHostState>? hosts = null) =>
        BoardMenuModel.Build(boardId, settings, sources, loc, hosts) is { } level ? MenuBuilder.Build(level, commands, loc) : null;

    /// <summary><c>boards.remove.confirm</c>, naming the board by its style and sources; true to remove.</summary>
    public static bool ConfirmRemove(Window? owner, BoardDefinition board, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        return ConfirmDialog.Show(owner, loc["app.name"], loc["boards.remove.confirm"], BoardMenuModel.Title(board, loc), loc["orbs.remove.button"],
            loc["history.cancel"]);
    }
}
