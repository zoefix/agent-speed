namespace AgentSpeed.Core;

public enum SourceKind
{
    Codex,
    ClaudeCode,
    ClaudeDesktop,
    DeepSeek,
    OpenCode,
    Gemini,
    Antigravity,
    AntigravityCli,
}

public enum AgentPhase
{
    Generating,
    Tools,
    Idle,
}

public enum SpeedMetric
{
    Output,
    Total,
}

public readonly record struct TokenTotals(
    long Requests = 0,
    long Input = 0,
    long CachedInput = 0,
    long CacheWriteInput = 0,
    long Output = 0,
    long Reasoning = 0)
{
    public long Total => Input + Output;

    public static TokenTotals Zero => default;
}

public readonly record struct Rate(double Output = 0, double Total = 0)
{
    public double ValueFor(SpeedMetric metric) => metric == SpeedMetric.Output ? Output : Total;

    public static Rate Zero => default;
}

/// <param name="Time">Start of the one-second bucket.</param>
public sealed record HistoryPoint(DateTimeOffset Time, Rate Measured, Rate Estimated);

/// <param name="Id">Absolute transcript path.</param>
/// <param name="RecentSpeed">Output tok/s of the agent's recent responses (METRICS 6.2).</param>
/// <param name="ParentId">The parent agent's id when the parent is also in the list (METRICS 6.7).</param>
public sealed record AgentStatus(
    string Id,
    SourceKind Source,
    string Project,
    string? Model,
    AgentPhase Phase,
    bool IsSubagent,
    Rate Live,
    double? RecentSpeed,
    DateTimeOffset LastActivity,
    string? ParentId = null);

/// <param name="Available">The root folder exists and is readable.</param>
public sealed record SourceStatus(
    SourceKind Kind,
    bool Enabled,
    string RootPath,
    bool Available,
    int SessionCount,
    Rate Live,
    TokenTotals Today,
    int ActiveAgents);

/// <param name="Duration">Null when the request start is unknown.</param>
/// <param name="Speed">Output tok/s, null when the duration is unknown.</param>
public sealed record ResponseSummary(
    SourceKind Source,
    string Project,
    string? Model,
    long OutputTokens,
    long TotalTokens,
    TimeSpan? Duration,
    double? Speed,
    DateTimeOffset EndedAt);

public enum MonitorStateKind
{
    Starting,
    Running,
    NoSources,
}

public readonly record struct MonitorState(MonitorStateKind Kind, double Progress = 0)
{
    public static MonitorState Running => new(MonitorStateKind.Running, 1);

    public static MonitorState NoSources => new(MonitorStateKind.NoSources, 1);

    public static MonitorState Starting(double progress) => new(MonitorStateKind.Starting, progress);
}

public abstract record MonitorWarning
{
    public sealed record PartialHistory : MonitorWarning;

    public sealed record UnreadableFiles(int Count) : MonitorWarning;

    public sealed record MalformedLines(int Count) : MonitorWarning;

    /// <summary>Claude responses whose final usage was never written (METRICS 3.3); today's output is a lower bound.</summary>
    public sealed record UnfinalizedUsage(int Count) : MonitorWarning;

    /// <summary>The source's data exists but has an unsupported format (an OpenCode database without the expected tables).</summary>
    public sealed record UnsupportedFormat(SourceKind Source) : MonitorWarning;
}

/// <param name="LiveEstimatedShare">Share of <paramref name="Live"/> (0..1) that is an in-flight estimate.</param>
/// <param name="AverageToday">Output tok/s over today's valid responses.</param>
/// <param name="History">Last 300 one-second buckets, oldest first.</param>
public sealed record SpeedSnapshot(
    DateTimeOffset Timestamp,
    MonitorState State,
    Rate Live,
    double LiveEstimatedShare,
    Rate PeakToday,
    DateTimeOffset? PeakTodayAt,
    double? AverageToday,
    TokenTotals Today,
    IReadOnlyList<SourceStatus> Sources,
    IReadOnlyList<AgentStatus> Agents,
    int GeneratingCount,
    int ToolsCount,
    IReadOnlyList<HistoryPoint> History,
    ResponseSummary? LastResponse,
    IReadOnlyList<MonitorWarning> Warnings)
{
    public static SpeedSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue,
        MonitorState.Starting(0),
        Rate.Zero,
        0,
        Rate.Zero,
        null,
        null,
        TokenTotals.Zero,
        [],
        [],
        0,
        0,
        [],
        null,
        []);
}

public abstract record SpeedEvent
{
    public sealed record ResponseCompleted(ResponseSummary Response) : SpeedEvent;

    public sealed record NewPeak(double Value) : SpeedEvent;
}

/// <param name="CodexRoot">Null uses the default Codex sessions folder.</param>
/// <param name="ClaudeRoot">Null uses the default Claude projects folder (shared by Claude Code and Claude Desktop).</param>
/// <param name="DshRoot">Null uses the default DeepSeek Harness sessions folder.</param>
/// <param name="OpenCodeRoot">Null uses the default OpenCode data folder (the one that contains opencode.db).</param>
/// <param name="GeminiRoot">Null uses the default Gemini CLI home folder.</param>
public sealed record MonitorConfiguration(
    IReadOnlySet<SourceKind> EnabledSources,
    string? CodexRoot = null,
    string? ClaudeRoot = null,
    string? DshRoot = null,
    string? OpenCodeRoot = null,
    string? GeminiRoot = null,
    string? AntigravityRoot = null,
    string? AntigravityCliRoot = null)
{
    public static MonitorConfiguration Default { get; } =
        new(new HashSet<SourceKind>(Enum.GetValues<SourceKind>()));

    public bool Equals(MonitorConfiguration? other) =>
        other is not null
        && EnabledSources.SetEquals(other.EnabledSources)
        && CodexRoot == other.CodexRoot
        && ClaudeRoot == other.ClaudeRoot
        && DshRoot == other.DshRoot
        && OpenCodeRoot == other.OpenCodeRoot
        && GeminiRoot == other.GeminiRoot
        && AntigravityRoot == other.AntigravityRoot
        && AntigravityCliRoot == other.AntigravityCliRoot;

    public override int GetHashCode() =>
        HashCode.Combine(EnabledSources.Count, CodexRoot, ClaudeRoot, DshRoot, OpenCodeRoot, GeminiRoot, AntigravityRoot, AntigravityCliRoot);
}
