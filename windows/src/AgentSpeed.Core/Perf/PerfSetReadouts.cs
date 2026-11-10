namespace AgentSpeed.Core.Perf;

/// <summary>A set's first-content latency (首字): the median in ms, null with fewer than three values, and how many values there were.</summary>
public readonly record struct PerfSetLatency(double? Ms, int N);

/// <summary>One model of a set's running agents (METRICS 6.9 <i>Readouts of a set</i>).</summary>
/// <param name="Model">The exact model string its agents report.</param>
/// <param name="Speed">The highest <c>recentSpeed</c> (6.2, tok/s) of its agents; null when none of them has one.</param>
/// <param name="Agents">How many of the set's running agents use it.</param>
/// <param name="Source">The source of the agent that supplied the speed (else the group's first agent), for the vendor.</param>
public sealed record PerfRunningModel(string Model, double? Speed, int Agents, SourceKind Source)
{
    /// <summary>The speed as shown: rounded half away from zero; null without one.</summary>
    public int? Shown => Speed is { } speed ? PerfFormula.Round(speed) : null;
}

/// <summary>An agent as the running models see it: its model, its phase after the staleness rule, its own recent speed.</summary>
public readonly record struct PerfRunner(string? Model, AgentPhase Phase, double? RecentSpeed, SourceKind Source);

/// <summary>
/// The readouts of an orb's set <c>A</c> (METRICS 6.9 <i>Readouts of a set</i>, DESIGN 2a.6): the first-content latency
/// pooled over the agents' samples and the running models, each once with its fastest agent's speed. They never drive
/// heat, titles, peaks or the character choice.
/// </summary>
public static class PerfSetReadouts
{
    /// <summary>The median over the union of the agents' sample latencies (an even count: the mean of the middle two).</summary>
    public static PerfSetLatency FirstLatency(IEnumerable<IReadOnlyList<long>> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var values = new List<long>();
        foreach (var sample in samples)
        {
            values.AddRange(sample);
        }

        if (values.Count < PerfFormula.MinSamples)
        {
            return new PerfSetLatency(null, values.Count);
        }

        values.Sort();
        var mid = values.Count / 2;
        var median = values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2.0;
        return new PerfSetLatency(median, values.Count);
    }

    /// <summary>The set's agents (the agents list filtered to the orb's sources): their evaluations' sample latencies.</summary>
    public static PerfSetLatency FirstLatency(IEnumerable<AgentStatus> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return FirstLatency(agents.Select(a => a.Perf?.Latencies ?? []));
    }

    /// <summary>
    /// The agents that are not idle and report a model, grouped by that exact string; each group's highest recent speed,
    /// fastest first, groups without a speed last, ties by model (ordinal).
    /// </summary>
    public static IReadOnlyList<PerfRunningModel> RunningModels(IEnumerable<PerfRunner> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        var rows = new Dictionary<string, PerfRunningModel>(StringComparer.Ordinal);
        foreach (var agent in agents)
        {
            if (agent.Phase == AgentPhase.Idle || string.IsNullOrEmpty(agent.Model))
            {
                continue;
            }

            var speed = agent.RecentSpeed is { } s && double.IsFinite(s) ? s : (double?)null;
            if (!rows.TryGetValue(agent.Model, out var row))
            {
                rows[agent.Model] = new PerfRunningModel(agent.Model, speed, 1, agent.Source);
                continue;
            }

            var faster = speed is { } own && (row.Speed is not { } held || own > held);
            rows[agent.Model] = faster
                ? row with { Speed = speed, Agents = row.Agents + 1, Source = agent.Source }
                : row with { Agents = row.Agents + 1 };
        }

        var ordered = rows.Values.ToList();
        ordered.Sort(Fastest);
        return ordered;
    }

    public static IReadOnlyList<PerfRunningModel> RunningModels(IEnumerable<AgentStatus> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        return RunningModels(agents.Select(a => new PerfRunner(a.Model, a.Phase, a.RecentSpeed, a.Source)));
    }

    private static int Fastest(PerfRunningModel a, PerfRunningModel b)
    {
        if (a.Speed is { } x && b.Speed is { } y && x != y)
        {
            return y.CompareTo(x);
        }

        if (a.Speed is null != b.Speed is null)
        {
            return a.Speed is null ? 1 : -1;
        }

        return string.CompareOrdinal(a.Model, b.Model);
    }
}
