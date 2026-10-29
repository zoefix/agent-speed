namespace AgentSpeed.Core.Perf;

/// <summary>One agent of a set, for <see cref="PerfSetValue.Highest"/>.</summary>
public readonly record struct PerfCandidate(string AgentId, long LastLineMs, PerfForm Form, double? Score);

/// <summary>
/// The performance value of an agent, a set or a board group (METRICS 6.9 <i>Sets and groups</i>): the highest live score,
/// else the highest resting score, else none, with the agent that supplied it, so values of disjoint parts of a set merge
/// into the value of the whole (<see cref="Merge"/>).
/// </summary>
public readonly record struct PerfSetValue(PerfForm Form, double? Score, string? AgentId)
{
    public static PerfSetValue None => default;

    /// <summary>The supplying agent's <c>lastLineMs</c> (ties prefer the latest).</summary>
    public long LastLineMs { get; init; }

    /// <summary>The supplying agent's evaluation, for the score card; null for none.</summary>
    public PerfEvaluation? Evaluation { get; init; }

    /// <summary>The score as shown: rounded half away from zero; null for none.</summary>
    public int? Shown => Score is { } score ? PerfFormula.Round(score) : null;

    /// <summary>The supplying agent's score is provisional (<c>3 ≤ n &lt; 10</c>).</summary>
    public bool Provisional => Evaluation?.Provisional == true;

    public bool Resting => Form == PerfForm.Resting;

    /// <summary>One agent's own value as a set of one.</summary>
    public static PerfSetValue Of(string agentId, long lastLineMs, PerfEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        return evaluation.Score is null
            ? None
            : new PerfSetValue(evaluation.Form, evaluation.Score, agentId) { LastLineMs = lastLineMs, Evaluation = evaluation };
    }

    /// <summary>The highest rule over two disjoint parts: the value of their union.</summary>
    public static PerfSetValue Merge(PerfSetValue a, PerfSetValue b)
    {
        if (b.Score is null)
        {
            return a;
        }

        if (a.Score is null)
        {
            return b;
        }

        if (a.Form != b.Form)
        {
            return a.Form == PerfForm.Live ? a : b;
        }

        return Better(a.AsCandidate(), b.AsCandidate()) == 0 ? a : b;
    }

    /// <summary>
    /// <c>highest</c>: the highest live value, else the highest resting value, else none. Among the agents with that exact
    /// score in that form, the one with the latest <c>lastLineMs</c>, then the smallest id (ordinal).
    /// </summary>
    public static PerfSetValue Highest(IEnumerable<PerfCandidate> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        PerfCandidate? live = null;
        PerfCandidate? resting = null;
        foreach (var agent in agents)
        {
            if (agent.Score is null)
            {
                continue;
            }

            switch (agent.Form)
            {
                case PerfForm.Live:
                    live = live is { } l && Better(l, agent) == 0 ? l : agent;
                    break;
                case PerfForm.Resting:
                    resting = resting is { } r && Better(r, agent) == 0 ? r : agent;
                    break;
            }
        }

        return (live ?? resting) is { } best
            ? new PerfSetValue(best.Form, best.Score, best.AgentId) { LastLineMs = best.LastLineMs }
            : None;
    }

    private PerfCandidate AsCandidate() => new(AgentId ?? string.Empty, LastLineMs, Form, Score);

    /// <summary>0 when <paramref name="held"/> stays the better of the two, 1 when <paramref name="agent"/> replaces it.</summary>
    private static int Better(PerfCandidate held, PerfCandidate agent)
    {
        var score = agent.Score!.Value;
        var heldScore = held.Score!.Value;
        if (score != heldScore)
        {
            return score > heldScore ? 1 : 0;
        }

        if (agent.LastLineMs != held.LastLineMs)
        {
            return agent.LastLineMs > held.LastLineMs ? 1 : 0;
        }

        return string.CompareOrdinal(agent.AgentId, held.AgentId) < 0 ? 1 : 0;
    }
}
