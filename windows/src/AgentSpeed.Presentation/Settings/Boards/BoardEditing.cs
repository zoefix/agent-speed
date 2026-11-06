using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Settings.Boards;

/// <summary>
/// The edits of the board menu and Settings → 懸浮窗, Boards (DESIGN 2c, Interaction and window). Pure; an unknown id
/// leaves the document unchanged, a refused edit returns null.
/// </summary>
public static class BoardEditing
{
    public static BoardsDocument SetStyle(BoardsDocument document, string id, BoardStyle style) => Update(document, id, b => b with { Style = style });

    public static BoardsDocument SetSize(BoardsDocument document, string id, BoardSize size) => Update(document, id, b => b with { Size = size });

    public static BoardsDocument SetAllSources(BoardsDocument document, string id) => Update(document, id, b => b with { Sources = OrbSources.All });

    public static BoardsDocument SetSources(BoardsDocument document, string id, OrbSources sources) => Update(document, id, b => b with { Sources = sources });

    /// <summary>Checks or unchecks one source (an all-sources board becomes that source); null for the last tracked source.</summary>
    public static BoardsDocument? ToggleSource(BoardsDocument document, string id, SourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Find(id)?.Sources.Toggling(kind) is { } sources ? Update(document, id, b => b with { Sources = sources }) : null;
    }

    /// <summary>Whether unchecking <paramref name="kind"/> is allowed (false for the last tracked source).</summary>
    public static bool CanUncheck(BoardDefinition board, SourceKind kind)
    {
        ArgumentNullException.ThrowIfNull(board);
        return board.Sources.Unchecking(kind) is not null;
    }

    /// <summary>The hosts a board counts (DESIGN 13).</summary>
    public static BoardsDocument SetHosts(BoardsDocument document, string id, OrbHosts hosts) => Update(document, id, b => b with { Hosts = hosts });

    /// <summary>The pickers' rule over the hosts this computer knows, in settings order; null when refused (the last host).</summary>
    public static BoardsDocument? ToggleHost(BoardsDocument document, string id, string hostId, IReadOnlyList<string> known)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Boards.FirstOrDefault(b => b.Id == id)?.Hosts.Toggling(hostId, known) is { } next ? SetHosts(document, id, next) : null;
    }

    /// <summary>A host removed from the settings leaves every board's selection; one left without hosts tracks all (REMOTE 2.4).</summary>
    public static BoardsDocument RemovingHost(BoardsDocument document, string hostId)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Boards.Any(b => !b.Hosts.IsAll && b.Hosts.Tracks(hostId))
            ? document with { Boards = [.. document.Boards.Select(b => b with { Hosts = b.Hosts.Removing(hostId) })] }
            : document;
    }

    public static BoardsDocument SetOpacity(BoardsDocument document, string id, double opacity) =>
        Update(document, id, b => b with { Opacity = OrbDefinition.CleanOpacity(opacity) });

    public static BoardsDocument SetAlwaysOnTop(BoardsDocument document, string id, bool value) => Update(document, id, b => b with { AlwaysOnTop = value });

    public static BoardsDocument SetClickThrough(BoardsDocument document, string id, bool value) => Update(document, id, b => b with { ClickThrough = value });

    public static BoardsDocument SetFadeWhenIdle(BoardsDocument document, string id, bool value) => Update(document, id, b => b with { FadeWhenIdle = value });

    /// <summary>Turns one live metrics switch on or off (DESIGN 2c, Live metrics: Switches); the others stay as they are.</summary>
    public static BoardsDocument SetReadout(BoardsDocument document, string id, BoardReadout readout, bool on) =>
        Update(document, id, b => b with { Readouts = b.Readouts.With(readout, on) });

    public static BoardsDocument SetVisible(BoardsDocument document, string id, bool value) => Update(document, id, b => b with { Visible = value });

    /// <summary><c>boards.show</c> / <c>boards.hide</c> of the quick menu: every board at once.</summary>
    public static BoardsDocument SetAllVisible(BoardsDocument document, bool visible)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Boards.All(b => b.Visible == visible) ? document : document with { Boards = [.. document.Boards.Select(b => b with { Visible = visible })] };
    }

    /// <summary>The quick menu offers <c>boards.hide</c> while any board shows, else <c>boards.show</c>.</summary>
    public static bool AnyVisible(BoardsDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Boards.Any(b => b.Visible);
    }

    public static BoardsDocument? Remove(BoardsDocument document, string id)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.IndexOf(id) < 0 ? null : document.Remove(id);
    }

    private static BoardsDocument Update(BoardsDocument document, string id, Func<BoardDefinition, BoardDefinition> change)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Find(id) is { } board ? document.Replace(change(board)) : document;
    }
}
