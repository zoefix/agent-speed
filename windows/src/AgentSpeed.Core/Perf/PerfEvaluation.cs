namespace AgentSpeed.Core.Perf;

/// <summary>Which of its three forms a performance value takes (METRICS 6.9 <i>Values</i>).</summary>
public enum PerfForm
{
    /// <summary>No score (fewer than three measured responses in the window): shown as "—".</summary>
    None,

    /// <summary>The agent is idle and has a score: shown pale.</summary>
    Resting,

    /// <summary>The agent is working and has a score.</summary>
    Live,
}

/// <summary>
/// One agent's performance score at one instant with the facts of its score card (METRICS 6.9 <i>Facts for the card</i>):
/// the sample size, the completed tool calls, the active time, the newest sample response's model, every component and,
/// with a history, the baseline of the agent's source and model.
/// </summary>
/// <param name="Score">The unrounded score; null for none.</param>
/// <param name="Tools"><c>|C|</c>: the completed tool calls inside the sample's span (工具調用次數).</param>
/// <param name="SpanStartMs">The smallest widened start of the sample; null without a sample.</param>
/// <param name="Model">The model of the newest sample response, as logged.</param>
public sealed record PerfEvaluation(
    SourceKind Source,
    PerfForm Form,
    double? Score,
    int N,
    int Tools,
    long ActiveMs,
    long? SpanStartMs,
    string? Model,
    IReadOnlyList<PerfComponent> Components)
{
    /// <summary>No sample: every component absent.</summary>
    public static PerfEvaluation Empty(SourceKind source)
    {
        Span<(double?, int)> measures = stackalloc (double?, int)[7];
        return new PerfEvaluation(source, PerfForm.None, null, 0, 0, 0, null, null, PerfFormula.Combine(source, measures).Components);
    }

    /// <summary>The score as shown: rounded half away from zero; null for none.</summary>
    public int? Shown => Score is { } score ? PerfFormula.Round(score) : null;

    /// <summary>Few samples: <c>3 ≤ n &lt; 10</c> (DESIGN 12 marks it).</summary>
    public bool Provisional => Score is not null && N < PerfFormula.ProvisionalBelow;

    /// <summary>The pooled score of the agent's source and model over the 14 days before today (6.11); null without one.</summary>
    public double? Baseline { get; init; }

    /// <summary><c>score / baseline − 1</c>; null without a baseline.</summary>
    public double? VsUsual => Score is { } score && Baseline is { } baseline ? score / baseline - 1 : null;

    /// <summary><c>100 × vsUsual</c> rounded half away from zero (shown as <c>+8%</c>, <c>−12%</c>, <c>±0%</c>).</summary>
    public int? VsUsualPercent => VsUsual is { } vs ? PerfFormula.Round(100 * vs) : null;

    public PerfComponent Component(PerfComponentId id) => Components[(int)id];

    /// <summary>Equal by value, components included, so an unchanged evaluation is an unchanged frame.</summary>
    public bool Equals(PerfEvaluation? other) =>
        other is not null && Source == other.Source && Form == other.Form && Nullable.Equals(Score, other.Score) && N == other.N
        && Tools == other.Tools && ActiveMs == other.ActiveMs && SpanStartMs == other.SpanStartMs
        && string.Equals(Model, other.Model, StringComparison.Ordinal) && Nullable.Equals(Baseline, other.Baseline)
        && Components.SequenceEqual(other.Components);

    public override int GetHashCode() => HashCode.Combine(Source, Form, Score, N, Tools, ActiveMs, Model);
}
