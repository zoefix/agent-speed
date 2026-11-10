using System.Collections.ObjectModel;
using AgentSpeed.Core.Locations;

namespace AgentSpeed.Core;

public enum SourceKind
{
    Codex,
    ClaudeCode,
    ClaudeDesktop,
    DeepSeek,
    OpenCode,
    Gemini,

    /// <summary>omp (oh-my-pi), METRICS 4.7.</summary>
    Omp,

    /// <summary>Google Antigravity (CLI, desktop app, IDE), METRICS 4.8.</summary>
    Antigravity,

    /// <summary>Tencent WorkBuddy, China edition (<c>~/.workbuddy</c>), METRICS 4.9.</summary>
    WorkBuddy,

    /// <summary>WorkBuddy AI, international edition (<c>~/.workbuddy-ai</c>), METRICS 4.9.</summary>
    WorkBuddyAi,
    ZCode,
    Kimi,
    GrokBuild,

    /// <summary>Pi (Pi Coding Agent), the upstream of omp, METRICS 4.13.</summary>
    Pi,

    /// <summary>OpenClaw: agent databases of a Gateway on this computer, one agent per transcript window, METRICS 4.14.</summary>
    OpenClaw,

    /// <summary>Hermes Agent (Nous Research): API-call lines of its <c>logs/agent.log</c>, one agent per session id, METRICS 4.15.</summary>
    Hermes,

    /// <summary>Factory Droid: session transcripts with their settings files, one agent per transcript, METRICS 4.16.</summary>
    Droid,

    /// <summary>Devin CLI (Cognition): its SQLite session database, one agent per session, METRICS 4.17.</summary>
    Devin,

    /// <summary>Qoder IDE (Alibaba): the agent core's SQLite chat database per edition, one agent per chat session, METRICS 4.18.</summary>
    Qoder,

    /// <summary>Qoder CLI: Claude Code's transcript format in its own folders, METRICS 4.19.</summary>
    QoderCli,
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

    public static TokenTotals operator +(TokenTotals a, TokenTotals b) => new(
        a.Requests + b.Requests, a.Input + b.Input, a.CachedInput + b.CachedInput, a.CacheWriteInput + b.CacheWriteInput,
        a.Output + b.Output, a.Reasoning + b.Reasoning);
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
    string? ParentId = null)
{
    public ExecutionModes Modes { get; init; } = ExecutionModes.Empty;

    /// <summary>A session-current fact (EXECUTION-MODES, Current values): the agent's current value only, never a response's.</summary>
    public ExecutionModes SessionModes { get; init; } = ExecutionModes.Empty;

    /// <summary>A value only a config file holds: shown as configured and nothing else.</summary>
    public ExecutionModes ConfiguredModes { get; init; } = ExecutionModes.Empty;

    /// <summary>The first known of the agent's own facts, a session-current fact and a configured value.</summary>
    public ExecutionModeAttribution Current(ExecutionModeKind kind) => ExecutionModes.Current(kind, Modes, SessionModes, ConfiguredModes);

    /// <summary>
    /// The agent's 性能 (METRICS 6.9 <i>Values</i>): its performance score by phase — live, resting (pale) or none — and
    /// its own live thinking sums.
    /// </summary>
    public Intelligence.AgentIntelligence Intelligence { get; init; }

    /// <summary>
    /// The agent's own evaluation for the score card (METRICS 6.9 <i>Facts for the card</i>): its components, sample and
    /// baseline; null before the engine evaluated it.
    /// </summary>
    public Perf.PerfEvaluation? Perf { get; init; }

    /// <summary>What the agent's recent tool calls say (METRICS 6.10).</summary>
    public Work.WorkSignals Work { get; init; } = Core.Work.WorkSignals.None;

    /// <summary><c>local</c>, or the remote host the agent runs on (REMOTE 2.2).</summary>
    public string Host { get; init; } = Remote.RemoteHost.LocalId;

    /// <summary>The agent runs on this computer.</summary>
    public bool IsLocal => Host == Remote.RemoteHost.LocalId;
}

/// <param name="Available">The root folder exists and is readable.</param>
/// <param name="LiveEstimated">The estimated part of <paramref name="Live"/> (METRICS 6.3), for subset shares (6.8).</param>
public sealed record SourceStatus(
    SourceKind Kind,
    bool Enabled,
    string RootPath,
    bool Available,
    int SessionCount,
    Rate Live,
    TokenTotals Today,
    int ActiveAgents,
    Rate LiveEstimated = default)
{
    /// <summary>The live cost rate (METRICS 9.5).</summary>
    public LiveCost LiveCost { get; init; }

    /// <summary>Today's cost (METRICS 9.6).</summary>
    public CostTally TodayCost { get; init; }

    /// <summary>Models of today's responses without a known price, most unpriced requests first.</summary>
    public IReadOnlyList<string> UnpricedModels { get; init; } = [];

    /// <summary>Today's unpriced facts (METRICS 9.6 <c>today.unpricedFacts</c>, 9.10 <i>Live</i>), in the fact order.</summary>
    public IReadOnlyList<Unpriced.UnpricedCount> UnpricedFacts { get; init; } = [];

    /// <summary>The source's 性能 (METRICS 6.9): its live thinking sums and the highest performance score of its listed agents.</summary>
    public Intelligence.IntelligenceSums Intelligence { get; init; }

    public ExecutionModes ConfiguredModes { get; init; } = ExecutionModes.Empty;
    public IReadOnlyList<ExecutionModeUsage> TodayModes { get; init; } = [];
}

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
    DateTimeOffset EndedAt)
{
    /// <summary><c>local</c>, or the remote host the response was read on.</summary>
    public string Host { get; init; } = Remote.RemoteHost.LocalId;
}

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

    /// <summary>The source's data exists but has an unsupported format (an OpenCode or Antigravity database without the expected tables).</summary>
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
    /// <summary>
    /// Today's measured output peak of every source set requested with <see cref="LiveMonitor.SetSubsets"/>
    /// (METRICS 6.8), keyed by the requested set: the maximum 3-bucket trailing average of the enabled members' summed
    /// measured buckets, without the observed live part. Empty while starting.
    /// </summary>
    public IReadOnlyDictionary<SourceSet, double> SubsetPeaks { get; init; } = ReadOnlyDictionary<SourceSet, double>.Empty;

    /// <summary>Local midnight at which this snapshot's "today" starts; <see cref="DateTimeOffset.MinValue"/> when unknown.</summary>
    public DateTimeOffset DayStart { get; init; } = DateTimeOffset.MinValue;

    /// <summary>The live cost rate of every source (METRICS 9.5); subscription marks apply in the UI (9.4).</summary>
    public LiveCost LiveCost { get; init; }

    /// <summary>Today's cost of every source (METRICS 9.6).</summary>
    public CostTally TodayCost { get; init; }

    /// <summary>Live reporting's agent counts and cumulative ledger (METRICS 11.2, 11.4).</summary>
    public Reporting.ReportLiveFacts Report { get; init; } = Reporting.ReportLiveFacts.Empty;
    public IReadOnlyList<ExecutionModeUsage> TodayModes { get; init; } = [];

    /// <summary>
    /// When the earliest read that changed the engine since the previous snapshot began (<see cref="Environment.TickCount64"/>
    /// milliseconds); null when no read changed anything (METRICS 11.9, Observations).
    /// </summary>
    public long? ObservedAtMs { get; init; }

    /// <summary>
    /// Today's measured output peak of every subset with hosts requested with
    /// <see cref="LiveMonitor.SetSubsets(IReadOnlyCollection{SubsetKey})"/> (DESIGN 13: METRICS 6.8 over the selected hosts'
    /// cells); subsets of every host are in <see cref="SubsetPeaks"/>.
    /// </summary>
    public IReadOnlyDictionary<SubsetKey, double> HostSubsetPeaks { get; init; } = ReadOnlyDictionary<SubsetKey, double>.Empty;

    /// <summary>
    /// This computer first, then every configured remote host in settings order (DESIGN 13); empty while no remote host is
    /// configured.
    /// </summary>
    public IReadOnlyList<HostStatus> Hosts { get; init; } = [];

    /// <summary>
    /// The (host, source) cells with something to show, in <see cref="Hosts"/> order and then source order; empty while no
    /// remote host is configured. Per source they add up to <see cref="Sources"/>.
    /// </summary>
    public IReadOnlyList<HostSourceStatus> HostSources { get; init; } = [];

    /// <summary>The measured peak of <paramref name="key"/> from <see cref="SubsetPeaks"/> or <see cref="HostSubsetPeaks"/>; null when not requested.</summary>
    public double? MeasuredPeak(SubsetKey key) => key.HasHosts
        ? HostSubsetPeaks.TryGetValue(key, out var hostPeak) ? hostPeak : null
        : SubsetPeaks.TryGetValue(key.Sources, out var peak) ? peak : null;

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

/// <summary>
/// What the monitor reads: the enabled sources and every source's location list (<see cref="Locations"/>, METRICS 2.1). The
/// single-folder parameters are a convenience (tests, the probe): a non-null one is that folder's whole list, <c>[path]</c>.
/// </summary>
/// <param name="CodexRoot">One Codex sessions folder in place of the Codex list; null reads the list.</param>
/// <param name="ClaudeRoot">One Claude projects folder (Claude Code and Claude Desktop) in place of the Claude list.</param>
/// <param name="DshRoot">One DeepSeek Harness sessions folder in place of its list.</param>
/// <param name="OpenCodeRoot">One OpenCode data folder (the one that contains opencode.db) or the database in place of its list.</param>
/// <param name="GeminiRoot">One Gemini CLI <c>tmp</c> folder in place of its list.</param>
/// <param name="OmpRoot">One omp <c>sessions</c> folder (without profiles) in place of its list.</param>
/// <param name="AntigravityRoot">One Antigravity store folder (or its <c>conversations</c> folder) in place of its list.</param>
/// <param name="WorkBuddyRoot">The WorkBuddy data folder or its <c>projects</c> folder in place of its list.</param>
/// <param name="WorkBuddyAiRoot">The WorkBuddy AI data folder or its <c>projects</c> folder in place of its list.</param>
/// <param name="PiRoot">One Pi sessions folder (without Pi's custom session folder) in place of its list.</param>
/// <param name="OpenClawRoot">An OpenClaw state folder, agent folder or database in place of its list.</param>
/// <param name="HermesRoot">A Hermes home or one <c>logs</c> folder in place of its list.</param>
/// <param name="DroidRoot">The Factory Droid <c>.factory</c> folder or its <c>sessions</c> folder in place of its list.</param>
/// <param name="DevinRoot">A Devin CLI folder that holds <c>sessions.db</c>, or its <c>devin</c> data folder, in place of its list.</param>
/// <param name="QoderRoot">A Qoder IDE data folder, its <c>SharedClientCache</c> folder or the folder that holds <c>local.db</c>, in place of its list.</param>
/// <param name="QoderCliRoot">A Qoder CLI config folder or its <c>projects</c> folder in place of its list.</param>
public sealed record MonitorConfiguration(
    IReadOnlySet<SourceKind> EnabledSources,
    string? CodexRoot = null,
    string? ClaudeRoot = null,
    string? DshRoot = null,
    string? OpenCodeRoot = null,
    string? GeminiRoot = null,
    string? OmpRoot = null,
    string? AntigravityRoot = null,
    string? WorkBuddyRoot = null,
    string? WorkBuddyAiRoot = null,
    string? ZCodeRoot = null,
    string? KimiRoot = null,
    string? GrokBuildRoot = null,
    string? PiRoot = null,
    string? OpenClawRoot = null,
    string? HermesRoot = null,
    string? DroidRoot = null,
    string? DevinRoot = null,
    string? QoderRoot = null,
    string? QoderCliRoot = null)
{
    public static MonitorConfiguration Default { get; } =
        new(new HashSet<SourceKind>(Enum.GetValues<SourceKind>()));

    /// <summary>Every source's location list (METRICS 2.1); remote hosts never use them (REMOTE 3.3).</summary>
    public LocationLists Locations { get; init; } = LocationLists.Automatic;

    /// <summary>The list a folder is read from: its single-folder parameter as <c>[path]</c> when set, else its list.</summary>
    public IReadOnlyList<DataLocation> ListOf(LocationFolder folder) =>
        SingleRoot(folder) is { Length: > 0 } path ? [DataLocation.At(path)] : Locations[folder];

    private string? SingleRoot(LocationFolder folder) => folder switch
    {
        LocationFolder.Codex => CodexRoot,
        LocationFolder.Claude => ClaudeRoot,
        LocationFolder.Dsh => DshRoot,
        LocationFolder.OpenCode => OpenCodeRoot,
        LocationFolder.Gemini => GeminiRoot,
        LocationFolder.Omp => OmpRoot,
        LocationFolder.Antigravity => AntigravityRoot,
        LocationFolder.WorkBuddy => WorkBuddyRoot,
        LocationFolder.WorkBuddyAi => WorkBuddyAiRoot,
        LocationFolder.ZCode => ZCodeRoot,
        LocationFolder.Kimi => KimiRoot,
        LocationFolder.GrokBuild => GrokBuildRoot,
        LocationFolder.Pi => PiRoot,
        LocationFolder.OpenClaw => OpenClawRoot,
        LocationFolder.Hermes => HermesRoot,
        LocationFolder.Droid => DroidRoot,
        LocationFolder.Devin => DevinRoot,
        LocationFolder.Qoder => QoderRoot,
        LocationFolder.QoderCli => QoderCliRoot,
        _ => null,
    };

    public bool Equals(MonitorConfiguration? other) =>
        other is not null
        && EnabledSources.SetEquals(other.EnabledSources)
        && LocationFolders.All.All(folder => ListOf(folder).SequenceEqual(other.ListOf(folder)));

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(EnabledSources.Count);
        foreach (var folder in LocationFolders.All)
        {
            foreach (var entry in ListOf(folder))
            {
                hash.Add(entry);
            }
        }

        return hash.ToHashCode();
    }
}
