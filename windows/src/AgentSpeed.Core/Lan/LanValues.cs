using AgentSpeed.Core.Reporting;

namespace AgentSpeed.Core.Lan;

/// <summary>One scope's live values of a tick before rounding (METRICS 11.2 <c>live.*</c>, overall or for one source).</summary>
public readonly record struct LanLiveValues(double OutputTokS, double EstimatedShare, long Generating, long Tools, long Active)
{
    public static LanLiveValues Zero => default;
}

/// <summary>A tick's live values: overall and per enabled source, in no particular order (the frame sorts them).</summary>
public sealed record LanTickValues(LanLiveValues Live, IReadOnlyDictionary<SourceKind, LanLiveValues> Sources)
{
    public static LanTickValues Empty { get; } = new(LanLiveValues.Zero, new Dictionary<SourceKind, LanLiveValues>());

    /// <summary>
    /// The report's values of the tick (METRICS 11.2, the computation <see cref="ReportBody"/> writes): every enabled source of
    /// every host, the agent counts of the base set; per source the same restricted to it.
    /// </summary>
    public static LanTickValues From(ReportSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var agents = default(ReportAgentCounts);
        foreach (var counts in sample.Facts.Agents.Values)
        {
            agents += counts;
        }

        var sources = new Dictionary<SourceKind, LanLiveValues>();
        foreach (var (kind, source) in sample.Sources)
        {
            var own = sample.Facts.Agents.GetValueOrDefault(kind);
            var estimated = source.Live.Output > 0 ? source.LiveEstimated.Output / source.Live.Output : 0;
            sources[kind] = new LanLiveValues(source.Live.Output, estimated, own.Generating, own.Tools, own.Active);
        }

        return new LanTickValues(new LanLiveValues(sample.Live.Output, sample.EstimatedShare, agents.Generating, agents.Tools, agents.Active), sources);
    }
}
