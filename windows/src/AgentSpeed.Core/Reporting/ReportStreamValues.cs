namespace AgentSpeed.Core.Reporting;

/// <summary>
/// The values of a tick that make it <i>changed</i> for the stream scheduler when they differ from the last built report's
/// (METRICS 11.9): <c>live.outputTokS</c>, <c>live.totalTokS</c>, <c>live.estimatedShare</c> and the agent counts, rounded as
/// the body writes them (METRICS 11.7); with a score extension also its <c>perfScore</c> or <c>taskScore</c> values (11.10).
/// </summary>
public readonly record struct ReportStreamValues(double OutputTokS, double TotalTokS, double EstimatedShare, int Generating, int Tools, int Active)
{
    /// <summary>The score extension's values; null while none is active.</summary>
    public ReportStreamScores? Scores { get; init; }

    public static ReportStreamValues Of(ReportSample sample, ReportScoreExtension scores = ReportScoreExtension.None)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var agents = default(ReportAgentCounts);
        foreach (var counts in sample.Facts.Agents.Values)
        {
            agents += counts;
        }

        return new ReportStreamValues(TokS(sample.Live.Output), TokS(sample.Live.Total),
            ReportJson.Round(Math.Clamp(double.IsFinite(sample.EstimatedShare) ? sample.EstimatedShare : 0, 0, 1), 3),
            Math.Min(agents.Generating, ReportBody.MaxCount), Math.Min(agents.Tools, ReportBody.MaxCount), Math.Min(agents.Active, ReportBody.MaxCount))
        {
            Scores = scores == ReportScoreExtension.None ? null : ReportStreamScores.Of(sample.Facts.Perf, scores == ReportScoreExtension.LegacyTask),
        };
    }

    private static double TokS(double value) => ReportJson.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, ReportBody.MaxTokS), 1);
}

/// <summary>
/// The <c>perfScore</c> (or legacy <c>taskScore</c>) values of a tick rounded as sent (METRICS 11.10, WebSocket): the
/// top-level value and the value of every model id an agent with a score or a thinking speed has. That covers every entry a
/// report can carry (an <c>other</c> entry merges ids whose values are compared one by one), so a tool result that moves a
/// score counts as a change.
/// </summary>
public sealed class ReportStreamScores : IEquatable<ReportStreamScores>
{
    private readonly (int? Score, string Form, double Thinking) _top;
    private readonly (string Id, (int? Score, string Form, double Thinking) Value)[] _models;

    private ReportStreamScores((int?, string, double) top, (string, (int?, string, double))[] models)
    {
        _top = top;
        _models = models;
    }

    /// <param name="legacy">The retired <c>taskScore</c>: the thinking speeds only.</param>
    public static ReportStreamScores Of(ReportPerfFacts facts, bool legacy = false)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var ids = new SortedSet<string>(facts.ModelThinking.Keys, StringComparer.Ordinal);
        if (!legacy)
        {
            foreach (var agent in facts.Agents)
            {
                ids.Add(agent.ModelId);
            }
        }

        var models = ids.Select(id =>
        {
            var thinking = facts.ModelThinking.GetValueOrDefault(id);
            var value = legacy ? ReportPerfValue.Legacy(thinking) : ReportPerfValue.Of(facts.Agents.Where(a => a.ModelId == id), thinking);
            return (id, value.Sent);
        }).ToArray();
        return new ReportStreamScores(legacy ? ReportPerfValue.Legacy(facts.Thinking).Sent : facts.Top.Sent, models);
    }

    public bool Equals(ReportStreamScores? other) => other is not null && _top == other._top && _models.SequenceEqual(other._models);

    public override bool Equals(object? obj) => Equals(obj as ReportStreamScores);

    public override int GetHashCode() => HashCode.Combine(_top, _models.Length);
}
