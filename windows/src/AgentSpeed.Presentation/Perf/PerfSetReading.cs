using AgentSpeed.Core;
using AgentSpeed.Core.Intelligence;
using AgentSpeed.Core.Perf;
using AgentSpeed.Presentation.Localization;

namespace AgentSpeed.Presentation.Perf;

/// <summary>
/// A score as its surface reads it (DESIGN 12, <i>Values and forms</i>): an agent's own, or a set's or group's highest —
/// live before resting — with the supplying agent's provisional mark and evaluation, and N, the number of the set's agents
/// with a score in that form, for the words and the hero card's eyebrow.
/// </summary>
/// <param name="Agent">The supplying agent (METRICS 6.9: the same score and form, latest line, then smallest id).</param>
public sealed record PerfSetReading(PerfForm Form, int? Shown, bool Provisional, int Count, AgentStatus? Agent, PerfEvaluation? Evaluation)
{
    public static PerfSetReading None { get; } = new(PerfForm.None, null, false, 0, null, null);

    public bool HasScore => Shown is not null;

    public bool IsResting => HasScore && Form == PerfForm.Resting;

    /// <summary>Drawn with the provisional mark: the supplying agent has 3 to 9 timed requests.</summary>
    public bool IsProvisional => HasScore && Provisional;

    public string Full => PerfNumber.Full(Shown);

    public string Compact => PerfNumber.Compact(Shown);

    /// <summary>One agent's own value (a dashboard row, a board sub row).</summary>
    public static PerfSetReading Of(AgentStatus agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        var value = agent.Intelligence;
        return value.Shown is { } shown && value.Form != PerfForm.None
            ? new PerfSetReading(value.Form, shown, value.Provisional, 1, agent, value.Evaluation ?? agent.Perf)
            : None with { Agent = agent, Evaluation = agent.Perf };
    }

    /// <summary>The set's value from the engine's <paramref name="value"/> and the set's <paramref name="agents"/>.</summary>
    public static PerfSetReading Of(PerfSetValue value, IEnumerable<AgentStatus> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);
        if (value.Shown is not { } shown || value.Form == PerfForm.None)
        {
            return None;
        }

        var count = 0;
        AgentStatus? supplier = null;
        foreach (var agent in agents)
        {
            if (agent.Intelligence.Shown is not null && agent.Intelligence.Form == value.Form)
            {
                count++;
            }

            if (supplier is null && string.Equals(agent.Id, value.AgentId, StringComparison.Ordinal))
            {
                supplier = agent;
            }
        }

        var evaluation = value.Evaluation ?? supplier?.Perf;
        return new PerfSetReading(value.Form, shown, evaluation?.Provisional ?? value.Provisional, Math.Max(1, count), supplier, evaluation);
    }

    public static PerfSetReading Of(IntelligenceSums sums, IEnumerable<AgentStatus> agents) => Of(sums.Score, agents);

    /// <summary>A board group's value: its highest member, among the root and its sub-agents.</summary>
    public static PerfSetReading Group(AgentStatus root, IReadOnlyList<AgentStatus> subs)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(subs);
        return Of(AgentIntelligence.Group(root, subs).Score, [root, .. subs]);
    }

    /// <summary>The score words (<c>1,540，閒置中，2 個 agent 中最高</c>, without 性能); null for none.</summary>
    public string? Words(Localizer loc) => Shown is { } shown ? PerfText.Words(shown, IsProvisional, Form, Count, loc) : null;

    /// <summary>
    /// The 性能 sentence of a set's label: <c>a11y.orb.intel</c> with the thinking words, <c>a11y.orb.intel.only</c> while
    /// 思考 is hidden (<paramref name="thinking"/> null); while none, <c>intel.none</c> takes the 性能 words' place.
    /// </summary>
    public string Sentence(string? thinking, Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (Words(loc) is { } words)
        {
            return thinking is null ? loc.Format("a11y.orb.intel.only", words) : loc.Format("a11y.orb.intel", words, thinking);
        }

        if (thinking is null)
        {
            return loc["intel.none"];
        }

        const string Marker = "";
        var full = loc.Format("a11y.orb.intel", Marker, thinking);
        var only = loc.Format("a11y.orb.intel.only", Marker);
        var at = full.IndexOf(only, StringComparison.Ordinal);
        return at < 0 ? loc["intel.none"] : string.Concat(full.AsSpan(0, at), loc["intel.none"], full.AsSpan(at + only.Length));
    }

    /// <summary>The tooltip line <c>perf.tooltip.top</c>: the full score and the supplying agent's project (its source without one); null for none.</summary>
    public string? Tooltip(Localizer loc)
    {
        ArgumentNullException.ThrowIfNull(loc);
        if (Shown is null)
        {
            return null;
        }

        var name = Agent is null ? string.Empty
            : string.IsNullOrWhiteSpace(Agent.Project) ? loc[Formatting.DisplayText.SourceKey(Agent.Source)] : Agent.Project;
        return name.Length == 0 ? null : loc.Format("perf.tooltip.top", Full, name);
    }
}
