using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Boards;

public enum BoardEntryKind
{
    /// <summary>A ranked group: rank, root badge, project, model, capsule, group speed, share.</summary>
    Group,

    /// <summary>One of a group's fastest generating sub-agents.</summary>
    Sub,

    /// <summary><c>boards.moreSubs</c>: the group's other sub-agents and their summed speed.</summary>
    MoreSubs,

    /// <summary><c>boards.moreGroups</c>: the groups that do not fit and their summed speed.</summary>
    MoreGroups,
}

/// <summary>
/// One row of the ranking list at its resting place (y from the list's top, pt/DIP).
/// </summary>
/// <param name="Key">Stable identity across ticks: the group or agent id, <c>group#more</c>, or <c>#rest</c>.</param>
/// <param name="Rank">The group's rank (1-based) for group rows; 0 otherwise.</param>
/// <param name="Agent">The sub-agent of a sub row; the root for a group row; null for the more lines.</param>
/// <param name="Rest">Rows a more line stands for (sub-agents or groups).</param>
/// <param name="RestLive">Their summed live value.</param>
public sealed record BoardEntry(
    BoardEntryKind Kind,
    string Key,
    BoardGroup Group,
    AgentStatus? Agent,
    int Rank,
    double Y,
    double Height,
    int Rest = 0,
    double RestLive = 0)
{
    /// <summary>A group row with its second line (its model; DESIGN 2c, Live metrics, at M and L).</summary>
    public bool TwoLine { get; init; }

    /// <summary>The row's 性能 as worded for this tick; null for the more lines or while the column is off.</summary>
    public BoardRowReadout? Readout { get; init; }
}

/// <summary>
/// Which rows a board shows (DESIGN 2c, Layout per size): groups first, at most the size's count and only as many whole
/// rows as fit with the more line; sub rows then fill the room that remains in rank order, so a sub row never pushes a
/// group out and no row is ever cut in half. An expanded group shows up to 8 of its sub-agents in that room and the other
/// groups' sub rows collapse.
/// </summary>
public static class BoardLayoutPlan
{
    public static IReadOnlyList<BoardEntry> Arrange(
        IReadOnlyList<BoardGroup> ranked, BoardSizeLimits limits, BoardMetrics metrics, SpeedMetric metric, string? expanded = null)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(metrics);
        var rowH = metrics.GroupHeight;
        var subH = metrics.SubHeight;
        var moreH = metrics.MoreHeight;
        var room = metrics.Room;

        var count = Math.Min(Math.Max(0, limits.Groups), ranked.Count);
        double RestRow() => ranked.Count > count ? moreH : 0;
        while (count > 1 && count * rowH + RestRow() > room)
        {
            count--;
        }

        var free = room - count * rowH - RestRow();
        var anyExpanded = expanded is not null && ranked.Take(count).Any(g => g.Id == expanded);
        var entries = new List<BoardEntry>(count * 2 + 1);
        var y = metrics.StartY;
        for (var i = 0; i < count; i++)
        {
            var group = ranked[i];
            var open = anyExpanded && group.Id == expanded;
            var wanted = open ? Math.Min(BoardSizeLimits.ExpandedSubRows, group.Subs.Count) : anyExpanded ? 0 : limits.SubRowsAt(i);
            var keep = new List<AgentStatus>(Math.Max(0, wanted));
            var more = false;
            foreach (var sub in group.Subs)
            {
                if (keep.Count >= wanted)
                {
                    break;
                }

                if (!open && !IsShownSub(sub, metric))
                {
                    continue;
                }

                var needMore = !more && group.Subs.Count - keep.Count - 1 > 0 ? moreH : 0;
                if (free >= subH + needMore)
                {
                    keep.Add(sub);
                    free -= subH;
                    if (needMore > 0)
                    {
                        more = true;
                        free -= moreH;
                    }
                }
            }

            if (more && keep.Count == group.Subs.Count)
            {
                more = false;
                free += moreH;
            }

            entries.Add(new BoardEntry(BoardEntryKind.Group, group.Id, group, group.Root, i + 1, y, rowH) { TwoLine = metrics.TwoLineRows });
            y += rowH;
            var shownLive = 0.0;
            foreach (var sub in keep)
            {
                entries.Add(new BoardEntry(BoardEntryKind.Sub, sub.Id, group, sub, 0, y, subH));
                shownLive += BoardGrouping.LiveOf(sub, metric);
                y += subH;
            }

            if (more)
            {
                entries.Add(new BoardEntry(BoardEntryKind.MoreSubs, group.Id + "#more", group, null, 0, y, moreH, group.Subs.Count - keep.Count,
                    Math.Max(0, group.SubLive - shownLive)));
                y += moreH;
            }
        }

        if (ranked.Count > count && count > 0)
        {
            var restLive = 0.0;
            for (var i = count; i < ranked.Count; i++)
            {
                restLive += ranked[i].Live;
            }

            entries.Add(new BoardEntry(BoardEntryKind.MoreGroups, "#rest", ranked[count], null, 0, y, moreH, ranked.Count - count, restLive));
        }

        return entries;
    }

    /// <summary>A sub-agent gets a row while it generates (in phase generating, or with tokens in the live window).</summary>
    public static bool IsShownSub(AgentStatus sub, SpeedMetric metric) =>
        sub.Phase == AgentPhase.Generating || BoardGrouping.LiveOf(sub, metric) > BoardRanking.Idle;
}
