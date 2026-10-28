using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Layout;

/// <summary>
/// The dashboard agents list (DESIGN section 4, item 4): collapsed to the first <see cref="CollapsedRows"/> rows of the
/// depth-first order, or every agent in a viewport of at most <see cref="ViewportRows"/> rows that scrolls beyond that.
/// </summary>
/// <param name="Total">Agents in the snapshot, including any that cannot be placed.</param>
public sealed record AgentList(AgentTreeLayout Collapsed, AgentTreeLayout Expanded, int Total)
{
    public const int CollapsedRows = 6;
    public const int ViewportRows = 12;

    public static AgentList Empty { get; } = new(new AgentTreeLayout([], 0), new AgentTreeLayout([], 0), 0);

    /// <summary>Expanding shows more rows, so the disclosure row is offered.</summary>
    public bool CanExpand => Expanded.Rows.Count > Collapsed.Rows.Count;

    /// <summary>Rows the expanded list shows without scrolling.</summary>
    public int ExpandedViewportRows => Math.Min(Expanded.Rows.Count, ViewportRows);

    public bool ExpandedScrolls => Expanded.Rows.Count > ViewportRows;

    public AgentTreeLayout For(bool expanded) => expanded ? Expanded : Collapsed;

    /// <summary>Rows visible without scrolling in either mode.</summary>
    public int ViewportRowsFor(bool expanded) => expanded ? ExpandedViewportRows : Collapsed.Rows.Count;

    /// <summary>
    /// Viewport height for fixed-height rows: a whole number of rows, so the animation target comes from the row count
    /// alone and never from a measured height.
    /// </summary>
    public double ViewportHeightFor(bool expanded, double rowHeight) =>
        double.IsFinite(rowHeight) && rowHeight > 0 ? ViewportRowsFor(expanded) * rowHeight : 0;

    public static AgentList Arrange(IReadOnlyList<AgentStatus> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var nodes = AgentTree.Order(agents);
        return new AgentList(
            AgentTree.Layout(nodes, AgentTree.Fit(nodes, CollapsedRows), agents.Count),
            AgentTree.Layout(nodes, nodes.Count, agents.Count),
            agents.Count);
    }
}
