using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Menus;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Settings.Boards;

/// <summary>
/// The words of Settings → 懸浮窗, Boards (DESIGN 2c): each row names the style and the board's sources, the header counts
/// the boards against the limit, and Add explains the limit when it is reached.
/// </summary>
public static class BoardRowText
{
    public static string Name(BoardDefinition board, Localizer loc) => BoardMenuModel.StyleName(board.Style, loc);

    /// <summary><c>全部來源 · M</c>: the sources (and, while remote hosts exist, the hosts, DESIGN 13) and the size.</summary>
    public static string Detail(BoardDefinition board, Localizer loc, IReadOnlyList<OrbHostChoice>? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        var suffix = hosts is null ? string.Empty : OrbNaming.HostSuffix(board.Hosts, hosts, loc);
        return OrbNaming.AutomaticName(board.Sources, loc) + suffix + " · " + BoardSizes.Key(board.Size);
    }

    /// <summary>The row's accessible name: style, sources and size, and <c>orbs.hidden</c> for a hidden board.</summary>
    public static string Accessible(BoardDefinition board, Localizer loc, IReadOnlyList<OrbHostChoice>? hosts = null)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(loc);
        var separator = loc.UsesLatinScript ? ", " : "，";
        var text = Name(board, loc) + separator + Detail(board, loc, hosts);
        return board.Visible ? text : text + separator + loc["orbs.hidden"];
    }

    public static string Count(BoardsDocument document, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("orbs.count", document.Count, BoardsDocument.MaxBoards);
    }

    public static string AddTooltip(BoardsDocument document, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(loc);
        return document.CanAdd ? loc["boards.add"] : loc.Format("boards.limit", BoardsDocument.MaxBoards);
    }
}
