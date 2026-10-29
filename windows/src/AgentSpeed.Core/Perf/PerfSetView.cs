namespace AgentSpeed.Core.Perf;

/// <summary>An agent as the set values see it at one instant: its phase as read (staleness applies here) and its evaluation.</summary>
internal sealed record PerfAgentView(string Id, SourceKind Source, AgentPhase Phase, long? LastLineMs, string? ParentId, PerfEvaluation Value);

/// <summary>A set's value: its sources (null: all) and the highest value of its listed agents.</summary>
internal sealed record PerfSetRow(IReadOnlyList<SourceKind>? Sources, PerfSetValue Value);

/// <summary>A board group's value: its members (root first, depth first) and their highest value.</summary>
internal sealed record PerfGroupRow(string Root, IReadOnlyList<string> Members, PerfSetValue Value);

/// <summary>
/// The set values of the dumps (METRICS 6.9 <i>Dumps</i>): the agents list at an instant (6.7 steps 1–4), the value of all
/// sources and of every source with listed agents, and of every board group (DESIGN 2c).
/// </summary>
internal sealed record PerfSetView(IReadOnlyList<string> Listed, IReadOnlyList<PerfSetRow> Sets, IReadOnlyList<PerfGroupRow> Groups)
{
    public const long ListedMs = 600_000;
    public const long StaleMs = 300_000;

    /// <summary>6.7 step 1: a phase older than 300 s without a new line is idle.</summary>
    public static AgentPhase EffectivePhase(AgentPhase phase, long? lastLineMs, long nowMs) =>
        phase != AgentPhase.Idle && lastLineMs is { } last && nowMs - last > StaleMs ? AgentPhase.Idle : phase;

    public static PerfSetView Of(IReadOnlyList<PerfAgentView> agents, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var tree = ListedTree(agents, nowMs);
        var shown = agents.Where(a => tree.ContainsKey(a.Id)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
        var sets = new List<PerfSetRow> { new(null, Highest(shown)) };
        foreach (var source in Enum.GetValues<SourceKind>())
        {
            var members = shown.Where(a => a.Source == source).ToList();
            if (members.Count > 0)
            {
                sets.Add(new PerfSetRow([source], Highest(members)));
            }
        }

        var byId = shown.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var groups = BoardGroups(shown.Select(a => (a.Id, tree[a.Id])).ToList())
            .Select(g => new PerfGroupRow(g.Root, g.Members, Highest(g.Members.Select(m => byId[m]))))
            .OrderBy(g => g.Root, StringComparer.Ordinal)
            .ToList();
        return new PerfSetView([.. shown.Select(a => a.Id)], sets, groups);
    }

    public static PerfSetValue Highest(IEnumerable<PerfAgentView> agents) =>
        PerfSetValue.Highest(agents.Select(a => new PerfCandidate(a.Id, a.LastLineMs ?? 0, a.Value.Form, a.Value.Score)));

    /// <summary>
    /// 6.7 steps 1–4: agents generating (after staleness) or with a line in the last 10 minutes, with every ancestor;
    /// each listed agent's tree parent (null when its parent is not listed).
    /// </summary>
    public static Dictionary<string, string?> ListedTree(IReadOnlyList<PerfAgentView> agents, long nowMs)
    {
        var byId = new Dictionary<string, PerfAgentView>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            byId[agent.Id] = agent;
        }

        var chosen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            if (EffectivePhase(agent.Phase, agent.LastLineMs, nowMs) == AgentPhase.Generating
                || (agent.LastLineMs is { } last && nowMs - last <= ListedMs))
            {
                chosen.Add(agent.Id);
            }
        }

        foreach (var id in chosen.ToList())
        {
            var seen = new HashSet<string>(StringComparer.Ordinal) { id };
            var current = byId[id].ParentId;
            while (current is not null && byId.TryGetValue(current, out var parent) && seen.Add(current))
            {
                chosen.Add(current);
                current = parent.ParentId;
            }
        }

        return chosen.ToDictionary(id => id, id => byId[id].ParentId is { } p && chosen.Contains(p) ? p : null, StringComparer.Ordinal);
    }

    /// <summary>
    /// DESIGN 2c groups: every root (no parent, or a parent that is not in the list) with all its descendants, the root
    /// first and then depth first in list order.
    /// </summary>
    public static List<(string Root, List<string> Members)> BoardGroups(IReadOnlyList<(string Id, string? ParentId)> agents)
    {
        var ids = agents.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var (id, parent) in agents)
        {
            if (parent is not null && ids.Contains(parent) && parent != id)
            {
                (children.TryGetValue(parent, out var list) ? list : children[parent] = []).Add(id);
            }
            else
            {
                roots.Add(id);
            }
        }

        var groups = new List<(string, List<string>)>();
        foreach (var root in roots)
        {
            var members = new List<string>();
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                members.Add(id);
                if (children.TryGetValue(id, out var kids))
                {
                    for (var i = kids.Count - 1; i >= 0; i--)
                    {
                        stack.Push(kids[i]);
                    }
                }
            }

            groups.Add((root, members));
        }

        return groups;
    }
}
