using AgentSpeed.Core.Perf;

namespace AgentSpeed.Core.Intelligence;

/// <summary>
/// The 性能 readout of a set of agents (METRICS 6.9): the window sums of the live thinking line, in tokens over the live
/// window and additive over sources (an orb adds the sums of its own sources, 6.8), and the set's performance value by the
/// highest rule (<see cref="Score"/>), which merges the same way.
/// </summary>
/// <param name="Output">Σ w: output tokens generating in the window, measured and estimated.</param>
/// <param name="Thinking">Σ ρ: reasoning tokens.</param>
/// <param name="ThinkingBasis">Σ w': the output of sources that record reasoning apart from output.</param>
public readonly record struct IntelligenceSums(double Output = 0, double Thinking = 0, double ThinkingBasis = 0)
{
    public static IntelligenceSums Zero => default;

    /// <summary>
    /// The highest performance score of the set's agents (6.9 <i>Sets and groups</i>): live when some agent is live, else
    /// resting (shown pale), else none; with the agent that supplied it.
    /// </summary>
    public PerfSetValue Score { get; init; }

    /// <summary>Something generated in the window.</summary>
    public bool IsActive => Output > 0;

    public double ThinkingShare => ThinkingBasis > 0 ? Thinking / ThinkingBasis : 0;

    /// <summary><c>liveThinking</c>: reasoning tok/s over the live window.</summary>
    public double LiveThinking => Thinking / (Engine.SpeedEngine.WindowMs / 1000.0);

    /// <summary>The set's score, unrounded; null for none.</summary>
    public double? Index => Score.Score;

    /// <summary>The set's score as shown (rounded half away from zero); null for none (shown as "—").</summary>
    public int? Shown => Score.Shown;

    /// <summary>The score is that of idle agents only (shown pale, DESIGN 10).</summary>
    public bool Resting => Score.Form == PerfForm.Resting;

    /// <summary>The supplying agent's score rests on fewer than ten responses (DESIGN 12).</summary>
    public bool Provisional => Score.Provisional;

    /// <summary>DeepSeek Harness reports reasoning inside its output (METRICS 4.3): it adds its weight, not its share.</summary>
    public static bool RecordsReasoning(SourceKind source) => PerfSources.RecordsReasoning(source);

    /// <summary>Adds output <paramref name="weight"/> of which <paramref name="reasoning"/> is reasoning.</summary>
    public IntelligenceSums Add(SourceKind source, double weight, double reasoning)
    {
        if (weight <= 0)
        {
            return this;
        }

        var separate = RecordsReasoning(source);
        return new IntelligenceSums(Output + weight, Thinking + (separate ? reasoning : 0), ThinkingBasis + (separate ? weight : 0))
        {
            Score = Score,
        };
    }

    public static IntelligenceSums operator +(IntelligenceSums a, IntelligenceSums b) =>
        new(a.Output + b.Output, a.Thinking + b.Thinking, a.ThinkingBasis + b.ThinkingBasis) { Score = PerfSetValue.Merge(a.Score, b.Score) };
}
