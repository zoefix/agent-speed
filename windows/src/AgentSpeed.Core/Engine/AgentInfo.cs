using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Engine;

/// <summary>Agent state as seen by the engine; produced from a parser after each batch of lines.</summary>
/// <param name="ParentId">Resolved parent agent id (METRICS 6.7), whether or not the parent is listed.</param>
internal readonly record struct AgentInfo(
    string Id,
    SourceKind Source,
    string? Project,
    string? Model,
    bool IsSubagent,
    AgentPhase Phase,
    long? InFlightStartMs,
    long? LastLineMs,
    string? ParentId = null)
{
    /// <summary>Verified completed calls from an unfinished turn; estimates only, never usage accounting.</summary>
    public IReadOnlyList<ParsedResponse> ReferenceResponses { get; init; } = [];

    public ExecutionModes Modes { get; init; } = ExecutionModes.Empty;

    /// <summary>The file's latest tool calls (METRICS 6.10), oldest first.</summary>
    public IReadOnlyList<Work.WorkAction> Work { get; init; } = [];

    /// <summary>The current task (METRICS 6.9) of a source with a task score; null for every other source.</summary>
    public Tasks.AgentTaskStatus? Task { get; init; }
}
