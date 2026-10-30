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
    public ExecutionModes Modes { get; init; } = ExecutionModes.Empty;

    /// <summary>A session-current fact (EXECUTION-MODES, Current values): the agent's current value only, never a response's.</summary>
    public ExecutionModes SessionModes { get; init; } = ExecutionModes.Empty;

    /// <summary>A value only a config file holds: shown as configured and nothing else.</summary>
    public ExecutionModes ConfiguredModes { get; init; } = ExecutionModes.Empty;

    public ExecutionModeAttribution CurrentEffort => ExecutionModes.Current(ExecutionModeKind.Effort, Modes, SessionModes, ConfiguredModes);

    /// <summary>The file's latest tool calls (METRICS 6.10), oldest first.</summary>
    public IReadOnlyList<Work.WorkAction> Work { get; init; } = [];
}
