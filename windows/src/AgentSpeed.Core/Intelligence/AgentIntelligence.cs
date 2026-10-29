using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.Intelligence;

/// <summary>
/// One agent's (or a board group's) 性能 readout (METRICS 6.9): its performance value by phase — live, resting or none —
/// and its own live thinking sums. A sub-agent's never count for its parent; a group takes the highest member score.
/// </summary>
/// <param name="Sums">The agent's own window sums of the thinking line (a group: its members' added); their
/// <see cref="IntelligenceSums.Score"/> is <paramref name="Score"/>, so adding agents' sums gives the highest score.</param>
/// <param name="Score">Its value, with the agent that supplied it (itself, or a group's member) and that agent's evaluation.</param>
public readonly record struct AgentIntelligence(IntelligenceSums Sums, PerfSetValue Score)
{
    public static AgentIntelligence None => default;

    /// <summary>
    /// A board group's value (6.9 <i>Sets and groups</i>): the highest score among the root and its sub-agents, with the
    /// evaluation of the member that supplied it; the thinking sums of every member added.
    /// </summary>
    public static AgentIntelligence Group(AgentStatus root, IReadOnlyList<AgentStatus> subs)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(subs);
        var sums = root.Intelligence.Sums;
        var score = root.Intelligence.Score;
        for (var i = 0; i < subs.Count; i++)
        {
            sums += subs[i].Intelligence.Sums;
            score = PerfSetValue.Merge(score, subs[i].Intelligence.Score);
        }

        return new AgentIntelligence(sums with { Score = score }, score);
    }

    public PerfForm Form => Score.Form;

    /// <summary>The unrounded score; null for none.</summary>
    public double? Value => Score.Score;

    /// <summary>The score as shown, rounded half away from zero; null for none.</summary>
    public int? Shown => Score.Shown;

    /// <summary>The supplying agent's score rests on fewer than ten responses (DESIGN 12 marks it).</summary>
    public bool Provisional => Score.Provisional;

    /// <summary>The supplying agent's evaluation, for the score card; null for none.</summary>
    public PerfEvaluation? Evaluation => Score.Evaluation;

    /// <summary>The agent's own reasoning tok/s.</summary>
    public double LiveThinking => Sums.LiveThinking;

    /// <summary>The agent's own reasoning share.</summary>
    public double ThinkingShare => Sums.ThinkingShare;
}
