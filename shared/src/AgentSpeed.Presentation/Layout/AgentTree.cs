using AgentSpeed.Core;

namespace AgentSpeed.Presentation.Layout;

/// <summary>What a row draws in one connector column.</summary>
public enum TreeGuide : byte
{
    None,

    /// <summary>A line passing by on its way to a later sibling.</summary>
    Through,

    /// <summary>A line passing by with a branch into this row.</summary>
    Branch,

    /// <summary>A line ending in a branch into this row (the last sibling).</summary>
    Last,
}

/// <summary>Connector columns of one row, outermost first; levels past the indent cap share <see cref="Deeper"/>.</summary>
public readonly record struct TreeGuides(TreeGuide Level1, TreeGuide Level2, TreeGuide Deeper)
{
    public bool IsEmpty => this == default;

    internal TreeGuides With(int level, TreeGuide guide) => level switch
    {
        1 => this with { Level1 = Merge(Level1, guide) },
        2 => this with { Level2 = Merge(Level2, guide) },
        _ => this with { Deeper = Merge(Deeper, guide) },
    };

    private static TreeGuide Merge(TreeGuide a, TreeGuide b) =>
        a == TreeGuide.None || a == b ? b : b == TreeGuide.None ? a : TreeGuide.Branch;
}

/// <param name="Depth">Depth among the shown rows; 0 for a root, even when the agent is a sub-agent.</param>
public sealed record AgentTreeRow(AgentStatus Agent, int Depth, TreeGuides Guides)
{
    /// <summary>Visual indent in levels, capped at <see cref="AgentTree.MaxIndent"/>.</summary>
    public int Indent => Math.Min(Depth, AgentTree.MaxIndent);
}

/// <param name="Hidden">Agents left out: the total minus the shown rows.</param>
public sealed record AgentTreeLayout(IReadOnlyList<AgentTreeRow> Rows, int Hidden);

/// <summary>
/// Shows the engine's grouped agent list (METRICS 6.7) as an indented tree. Groups (a root with all its descendants)
/// keep the engine's order and every agent is followed by its subtree. An agent whose parent is not listed is a root;
/// one listed before its parent is left out.
/// </summary>
public static class AgentTree
{
    public const int MaxIndent = 2;

    /// <summary>
    /// Shows the first <paramref name="maxRows"/> rows of the depth-first order; parents come before their children,
    /// so a child is never shown without its parent.
    /// </summary>
    public static AgentTreeLayout Arrange(IReadOnlyList<AgentStatus> agents, int maxRows)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var nodes = Order(agents);
        return Layout(nodes, Fit(nodes, maxRows), agents.Count);
    }

    /// <summary>Every agent that can be placed in the tree.</summary>
    public static AgentTreeLayout Arrange(IReadOnlyList<AgentStatus> agents) => Arrange(agents, int.MaxValue);

    internal readonly record struct Node(AgentStatus Agent, int Parent, int Depth);

    /// <summary>Depth-first rows of every placeable agent; roots and siblings keep the input order.</summary>
    internal static List<Node> Order(IReadOnlyList<AgentStatus> agents)
    {
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            listed.Add(agent.Id);
        }

        var placed = new Dictionary<string, int>(StringComparer.Ordinal);
        var children = new List<List<int>>();
        var roots = new List<int>();
        var members = new List<AgentStatus>();
        foreach (var agent in agents)
        {
            var parent = -1;
            if (agent.ParentId is { } parentId && listed.Contains(parentId)
                && !string.Equals(parentId, agent.Id, StringComparison.Ordinal)
                && !placed.TryGetValue(parentId, out parent))
            {
                continue;
            }

            if (!placed.TryAdd(agent.Id, members.Count))
            {
                continue;
            }

            (parent < 0 ? roots : children[parent]).Add(members.Count);
            members.Add(agent);
            children.Add([]);
        }

        var nodes = new List<Node>(members.Count);
        var pending = new Stack<(int Member, int Parent, int Depth)>();
        foreach (var root in roots)
        {
            pending.Push((root, -1, 0));
            while (pending.TryPop(out var item))
            {
                nodes.Add(new Node(members[item.Member], item.Parent, item.Depth));
                var row = nodes.Count - 1;
                var kids = children[item.Member];
                for (var k = kids.Count - 1; k >= 0; k--)
                {
                    pending.Push((kids[k], row, item.Depth + 1));
                }
            }
        }

        return nodes;
    }

    /// <summary>How many leading depth-first rows to show.</summary>
    internal static int Fit(List<Node> nodes, int maxRows) => Math.Clamp(maxRows, 0, nodes.Count);

    /// <summary>Rows and connector guides for the first <paramref name="count"/> nodes (a depth-first prefix).</summary>
    internal static AgentTreeLayout Layout(List<Node> nodes, int count, int total)
    {
        var hasLaterSibling = new bool[count];
        var parentsSeen = new HashSet<int>();
        for (var i = count - 1; i >= 0; i--)
        {
            hasLaterSibling[i] = !parentsSeen.Add(nodes[i].Parent);
        }

        var result = new AgentTreeRow[count];
        for (var i = 0; i < count; i++)
        {
            var guides = default(TreeGuides);
            var node = i;
            for (var level = nodes[i].Depth; level >= 1; level--)
            {
                var guide = node == i
                    ? hasLaterSibling[node] ? TreeGuide.Branch : TreeGuide.Last
                    : hasLaterSibling[node] ? TreeGuide.Through : TreeGuide.None;
                guides = guides.With(level, guide);
                node = nodes[node].Parent;
            }

            result[i] = new AgentTreeRow(nodes[i].Agent, nodes[i].Depth, guides);
        }

        return new AgentTreeLayout(result, total - count);
    }
}
