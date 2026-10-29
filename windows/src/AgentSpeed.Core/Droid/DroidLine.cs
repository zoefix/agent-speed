using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Droid;

/// <summary>A transcript line by its <c>type</c> (METRICS 4.16 Transcript lines).</summary>
internal enum DroidLineKind : byte
{
    Other,
    SessionStart,

    /// <summary><c>message</c> with an object <c>message</c>.</summary>
    Message,

    /// <summary><c>compaction_state</c>: a summary of the conversation so far.</summary>
    Compaction,

    /// <summary><c>agent_turn_outcome</c>, written without a timestamp.</summary>
    TurnOutcome,
}

internal enum DroidRole : byte
{
    Other,
    User,
    Assistant,
}

/// <summary>What the parser needs of one transcript line, with its place in the file (the call weights).</summary>
internal struct DroidLine
{
    public LineStatus Status;
    public DroidLineKind Kind;

    /// <summary>The line's byte offset in the file.</summary>
    public long Offset;

    /// <summary>The line's length in bytes without the <c>\n</c>; a <c>\r</c> before it counts.</summary>
    public int Length;

    /// <summary><c>timestamp</c> (METRICS 3.1); null when absent or not one.</summary>
    public long? TimestampMs;

    /// <summary>The top-level <c>id</c> when it is a non-empty string.</summary>
    public string? Id;
    public DroidRole Role;

    /// <summary><c>message.visibility == "user_only"</c>: shown to the user, never sent to the model.</summary>
    public bool UserOnly;

    /// <summary><c>message.modelId</c> trimmed, when it is a string that is not blank.</summary>
    public string? ModelId;

    /// <summary><c>message.reasoningEffort</c> when it is a string.</summary>
    public string? Effort;

    /// <summary><c>message.content</c> is an array with a block whose <c>type</c> is <c>tool_use</c>.</summary>
    public bool HasToolUse;

    /// <summary><c>session_start.cwd</c> when it is a string.</summary>
    public string? Cwd;

    /// <summary><c>session_start.parent</c> when it is a session id (a fork or successor).</summary>
    public string? Parent;

    /// <summary><c>session_start.callingSessionId</c> when it is a session id (a Task-tool worker).</summary>
    public string? CallingSessionId;

    /// <summary>An assistant message's <c>tool_use</c> blocks and a user message's <c>tool_result</c> blocks (METRICS 6.10).</summary>
    public Work.LineTools Tools;

}
