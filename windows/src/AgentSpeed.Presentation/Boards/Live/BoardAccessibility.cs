using AgentSpeed.Core;
using AgentSpeed.Presentation.Boards.Scene;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Modes;
using AgentSpeed.Presentation.Settings.Boards;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Boards.Live;

/// <summary>
/// What assistive technology reads of a board (DESIGN 2c Accessibility): the board as a list labelled <c>a11y.board</c>
/// (sources, value, title) and each group row as an item labelled <c>a11y.board.row</c>, followed by its 性能 words
/// (<c>a11y.agent.intel</c>, while the column shows), the root's effort and speed (as the dashboard's rows) and
/// <c>boards.subCount</c> when the group has sub-agents; the live metrics add their words to both (DESIGN 2c, Live metrics:
/// Accessibility). A row's score card is its help text (<see cref="BoardScoreCards.Description"/>).
/// </summary>
public static class BoardAccessibility
{
    public static string Board(BoardDefinition board, string valueText, string spokenTitle, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(board);
        ArgumentNullException.ThrowIfNull(loc);
        return loc.Format("a11y.board", OrbNaming.AutomaticName(board.Sources, loc), valueText, spokenTitle);
    }

    /// <summary>
    /// The board's label with what is switched on of its live metrics, in order: <c>a11y.orb.intel</c> (性能 and a shown 思考),
    /// <c>a11y.orb.intel.only</c> (思考 off or hidden) or <c>a11y.board.thinking</c> (性能 off), with the score words of DESIGN 12
    /// (provisional, idle, the highest of N agents) and <c>intel.none</c> in place of the 性能 words while none; then
    /// <c>a11y.board.cost</c>, <c>a11y.board.costUnknown</c> or <c>cost.subscriptionOnly</c>.
    /// </summary>
    public static string Board(BoardDefinition board, BoardTick tick, string spokenTitle, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(tick);
        var label = Board(board, tick.ValueText, spokenTitle, loc);
        var readouts = tick.Readouts;
        if (!readouts.Hero)
        {
            return label;
        }

        var gauge = tick.Gauge;
        var parts = new List<string>(3) { label };
        var thinking = readouts.Thinking && !tick.ThinkingHidden;
        if (readouts.Intelligence)
        {
            parts.Add(gauge.Score.Sentence(thinking ? gauge.SpokenThinking : null, loc));
        }
        else if (thinking)
        {
            parts.Add(loc.Format("a11y.board.thinking", gauge.SpokenThinking));
        }

        if (readouts.Cost)
        {
            parts.Add(gauge.Cost.State switch
            {
                BoardMoneyState.Tag => loc["cost.subscriptionOnly"],
                BoardMoneyState.Unknown => loc["a11y.board.costUnknown"],
                _ => loc.Format("a11y.board.cost", gauge.Cost.SpokenNumber, gauge.Cost.Currency),
            });
        }

        return string.Join(loc.UsesLatinScript ? ", " : "，", parts);
    }

    /// <summary>The group rows of a tick in rank order (sub rows and the more lines are not list items).</summary>
    public static IReadOnlyList<BoardEntry> Items(BoardTick? tick) =>
        tick is null ? [] : [.. tick.Entries.Where(e => e.Kind == BoardEntryKind.Group)];

    public static string Row(BoardEntry entry, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(loc);
        var group = entry.Group;
        var parts = new List<string>(5) { loc.Format("a11y.board.row", entry.Rank, group.Root.Project, SpeedFormat.Speed(group.Live)) };
        if (entry.Readout is { Spoken.Length: > 0 } readout)
        {
            parts.Add(readout.Spoken);
        }

        var capsule = EffortCapsule.Of(group.Root);
        if (capsule.IsKnown)
        {
            parts.Add(capsule.AccessibilityText(loc));
        }

        if (group.Subs.Count > 0)
        {
            parts.Add(loc.Format("boards.subCount", group.Subs.Count));
        }

        return string.Join(loc.UsesLatinScript ? ", " : "，", parts);
    }
}
