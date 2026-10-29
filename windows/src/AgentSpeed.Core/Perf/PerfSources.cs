namespace AgentSpeed.Core.Perf;

/// <summary>What each source records for the performance score (METRICS 6.9 <i>Absent components</i>).</summary>
public static class PerfSources
{
    /// <summary>Claude (both) and Codex log when a response's first content was written (METRICS 3.3 <c>firstMs</c>).</summary>
    public static bool RecordsLatency(SourceKind source) =>
        source is SourceKind.Codex or SourceKind.ClaudeCode or SourceKind.ClaudeDesktop;

    /// <summary>The sources whose tool calls are actions (METRICS 6.10).</summary>
    public static bool RecordsActions(SourceKind source) =>
        source is SourceKind.Codex or SourceKind.ClaudeCode or SourceKind.ClaudeDesktop or SourceKind.Omp or SourceKind.Pi
            or SourceKind.OpenClaw or SourceKind.Droid;

    /// <summary>DeepSeek Harness reports reasoning inside its output (METRICS 4.3).</summary>
    public static bool RecordsReasoning(SourceKind source) => source != SourceKind.DeepSeek;

    /// <summary>Hermes Agent states a cache read only when the provider reported one (METRICS 4.15).</summary>
    public static bool RecordsCache(SourceKind source) => source != SourceKind.Hermes;

    /// <summary>Whether the source never records what <paramref name="id"/> measures.</summary>
    public static bool NotRecorded(PerfComponentId id, SourceKind source) => id switch
    {
        PerfComponentId.L => !RecordsLatency(source),
        PerfComponentId.N or PerfComponentId.U or PerfComponentId.S => !RecordsActions(source),
        PerfComponentId.R => !RecordsReasoning(source),
        PerfComponentId.H => !RecordsCache(source),
        _ => false,
    };
}
