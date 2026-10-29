namespace AgentSpeed.Core.Engine;

/// <param name="ParentId">The parent's id when the parent is listed too (METRICS 6.7).</param>
internal sealed record AgentFrame(AgentInfo Info, AgentPhase Phase, Rate Live, double? RecentSpeed, string? ParentId = null)
{
    /// <summary>The agent's 性能 (METRICS 6.9 <i>Values</i>): its performance value by phase and its own thinking sums.</summary>
    public Intelligence.AgentIntelligence Intelligence { get; init; }

    /// <summary>The agent's own evaluation with the facts of its score card (METRICS 6.9 <i>Facts for the card</i>).</summary>
    public Perf.PerfEvaluation? Perf { get; init; }

    /// <summary>What the agent's recent tool calls say (METRICS 6.10).</summary>
    public Work.WorkSignals Work { get; init; } = Core.Work.WorkSignals.None;
}

/// <summary>Everything the engine computes for one instant (METRICS 6).</summary>
/// <param name="SourceLiveEstimated">The estimated part of each <paramref name="SourceLive"/> (METRICS 6.3, 6.8).</param>
internal sealed record EngineFrame(
    long NowMs,
    Rate Live,
    double EstimatedShare,
    Rate[] SourceLive,
    Rate[] SourceLiveEstimated,
    Rate PeakToday,
    long? PeakTodayAtMs,
    double? AverageToday,
    TokenTotals Today,
    TokenTotals[] SourceToday,
    IReadOnlyList<AgentFrame> Agents,
    int GeneratingCount,
    int ToolsCount,
    IReadOnlyList<HistoryPoint> History,
    ResponseSummary? LastResponse,
    IReadOnlyList<SpeedEvent> Events,
    int UnfinalizedToday)
{
    public IReadOnlyList<ExecutionModeUsage> TodayModes { get; init; } = [];

    /// <summary>The live cost rate (METRICS 9.5) in total.</summary>
    public LiveCost LiveCost { get; init; }

    /// <summary>The live cost rate of each source, indexed by <see cref="SourceKind"/>.</summary>
    public LiveCost[] SourceLiveCost { get; init; } = [];

    /// <summary>Today's cost (METRICS 9.6) in total.</summary>
    public CostTally TodayCost { get; init; }

    /// <summary>Today's cost of each source, indexed by <see cref="SourceKind"/>.</summary>
    public CostTally[] SourceTodayCost { get; init; } = [];

    /// <summary>Each source's models with unpriced responses today, most unpriced first, then by name.</summary>
    public IReadOnlyList<string>[] SourceUnpricedModels { get; init; } = [];

    /// <summary>Each source's unpriced facts of today (METRICS 9.10 <i>Live</i>), indexed by <see cref="SourceKind"/>.</summary>
    public IReadOnlyList<Unpriced.UnpricedCount>[] SourceUnpricedFacts { get; init; } = [];

    /// <summary>The live thinking sums and highest performance value of each source (METRICS 6.9), indexed by <see cref="SourceKind"/>.</summary>
    public Intelligence.IntelligenceSums[] SourceIntelligence { get; init; } = [];

    /// <summary>
    /// The model sums of the live thinking line (METRICS 6.9 <i>Live thinking</i>): per raw model and provider, a response's
    /// contribution for its own model, an in-flight estimate for its agent's current model.
    /// </summary>
    public IReadOnlyDictionary<ModelSumKey, Intelligence.IntelligenceSums> ModelIntelligence { get; init; } =
        new Dictionary<ModelSumKey, Intelligence.IntelligenceSums>();

    /// <summary>Live reporting's view of this instant (METRICS 11.2, 11.4, 11.10).</summary>
    public Reporting.ReportLiveFacts Report { get; init; } = Reporting.ReportLiveFacts.Empty;
}

/// <summary>A raw model (empty when unknown) and its provider, which together resolve to a report id (METRICS 11.3).</summary>
internal readonly record struct ModelSumKey(string Model, string? Provider);
