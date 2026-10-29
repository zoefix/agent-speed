using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Pi;

/// <summary>Entry classification by <c>type</c> (METRICS 4.13, the omp classes of 4.7 without Pi's absent ones).</summary>
internal enum PiEntryKind : byte
{
    Other,

    /// <summary>The session header.</summary>
    Session,
    Message,

    /// <summary>A side call with its own <c>provider</c>, <c>model</c> and <c>usage</c> (<c>kind</c> such as <c>cache_warm</c>).</summary>
    Usage,

    /// <summary><c>compaction</c> or <c>branch_summary</c>: the summary call's <c>usage</c>, without a model.</summary>
    Summary,
    ThinkingLevelChange,
}

internal enum PiRole : byte
{
    Other,
    User,
    Developer,
    Assistant,
    ToolResult,
}

/// <summary><c>message.stopReason</c>; a missing or non-string value counts as <see cref="Aborted"/>.</summary>
internal enum PiStop : byte
{
    Aborted,
    Error,
    Pending,
    Deferred,
    Stop,
    ToolUse,
    Length,
    Other,
}

/// <summary>The model call fields of an assistant message or an entry: <c>model</c>, <c>provider</c>, <c>api</c>, <c>usage</c>.</summary>
internal struct PiCall
{
    /// <summary>The <c>model</c> string (an empty one included); null when absent or not a string.</summary>
    public string? Model;

    /// <summary><c>provider</c> is a string, an empty one included.</summary>
    public bool HasProvider;

    /// <summary>The <c>provider</c> string; null when it is empty (METRICS 3.2).</summary>
    public string? Provider;
    public bool HasApi;
    public bool HasUsageObject;
    public TokenCounts Tokens;

    /// <summary>One of <c>input</c>, <c>output</c>, <c>cacheRead</c>, <c>cacheWrite</c> is above 0.</summary>
    public bool HasUsage;

    /// <summary><c>api == "cli"</c>: a CLI backend's turn (OpenClaw, METRICS 4.14).</summary>
    public bool ApiIsCli;

    /// <summary><c>usage.reasoningTokens</c>, OpenClaw's reasoning count (METRICS 4.14); Pi reads <c>usage.reasoning</c>.</summary>
    public long ReasoningTokens;

    /// <summary><c>usage.usageReport.state == "unavailable"</c>: provisional stream counters (OpenClaw, METRICS 4.14).</summary>
    public bool UsageUnavailable;

    /// <summary>The guard of METRICS 4.7 step 1: <c>model</c>, <c>provider</c> and <c>api</c> are strings, <c>usage</c> an object.</summary>
    public readonly bool IsGuardedMessage => Model is not null && HasProvider && HasApi && HasUsageObject;

    /// <summary>The guard of a <c>usage</c> entry (METRICS 4.13): the one of step 1 without <c>api</c>.</summary>
    public readonly bool IsGuardedEntry => Model is not null && HasProvider && HasUsageObject;
}

/// <summary>One extracted Pi entry. Conversation text is never kept.</summary>
internal struct PiLine
{
    public LineStatus Status;
    public PiEntryKind Kind;

    /// <summary>The entry <c>id</c> when it is a string.</summary>
    public string? Id;

    /// <summary>The entry (or header) <c>timestamp</c>, ISO 8601.</summary>
    public long? TimestampMs;

    /// <summary>Header <c>cwd</c>.</summary>
    public string? Cwd;

    public PiRole Role;

    /// <summary>The message's call (assistant, tool result) or the entry's own (<c>usage</c>, summaries).</summary>
    public PiCall Call;

    /// <summary><c>message.timestamp</c> truncated, when it is a finite number above 0.</summary>
    public long? StartedMs;

    /// <summary><c>message.durationMs</c> truncated, when it is a finite number of at least 0 (pi-ai 0.99+).</summary>
    public long? DurationMs;

    public PiStop Stop;

    /// <summary>Distinct string ids of the <c>toolCall</c> content blocks.</summary>
    public string[]? ToolCallIds;

    /// <summary>Tool result <c>toolCallId</c> when it is a string.</summary>
    public string? ToolCallId;

    /// <summary>The assistant message's or the <c>thinking_level_change</c> entry's <c>thinkingLevel</c> string.</summary>
    public string? ThinkingLevel;

    /// <summary>The line's tool calls or tool result (METRICS 6.10), read with the omp format's shared reader.</summary>
    public Work.LineTools Tools;

    /// <summary><c>message.__openclaw.mirrorOrigin</c> is a non-empty string: a mirrored delegated turn (METRICS 4.14).</summary>
    public bool Mirrored;



    /// <summary><c>error</c>, <c>aborted</c>, <c>pending</c> and <c>deferred</c>: totals only, phase idle.</summary>
    public readonly bool Failed => Stop is PiStop.Aborted or PiStop.Error or PiStop.Pending or PiStop.Deferred;
}
