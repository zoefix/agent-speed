namespace AgentSpeed.Core.Hermes;

/// <summary>One Hermes session of one log folder (METRICS 4.15 Agents and phase). Changed only by its log set.</summary>
internal sealed class HermesAgent(string agentId, string sessionId)
{
    /// <summary>The log folder's path, <c>#</c> and the session id (empty for lines without a session tag).</summary>
    public string AgentId { get; } = agentId;

    public string SessionId { get; } = sessionId;

    public AgentPhase Phase { get; private set; } = AgentPhase.Idle;

    public long? InFlightStartMs { get; private set; }

    public string? Model { get; private set; }

    public bool IsSubagent { get; private set; }

    public long? LastLineMs { get; private set; }

    /// <summary>The latest turn-start line whose platform is not <c>subagent</c>: a main session that day (DESIGN 5).</summary>
    public long? LastMainTurnMs { get; private set; }

    /// <summary>The session's effort in <c>state.db</c>, a session-current fact.</summary>
    public ExecutionModes SessionModes { get; private set; } = ExecutionModes.Empty;

    /// <summary>The effort and tier of the profile's <c>config.yaml</c>.</summary>
    public ExecutionModes ConfiguredModes { get; private set; } = ExecutionModes.Empty;

    /// <summary>Changes whenever agent-visible state changes.</summary>
    public int Version { get; private set; }

    public void SetModes(ExecutionModes session, ExecutionModes configured)
    {
        if (!session.Equals(SessionModes) || !configured.Equals(ConfiguredModes))
        {
            SessionModes = session;
            ConfiguredModes = configured;
            Version++;
        }
    }

    public void Touch(long timestampMs)
    {
        if (LastLineMs is not { } last || timestampMs > last)
        {
            LastLineMs = timestampMs;
            Version++;
        }
    }

    public void StartTurn(long timestampMs, string model, bool subagent, bool codexRuntime)
    {
        SetPhase(codexRuntime ? AgentPhase.Idle : AgentPhase.Generating, codexRuntime ? null : timestampMs);
        SetModel(model);
        if (IsSubagent != subagent)
        {
            IsSubagent = subagent;
            Version++;
        }

        if (!subagent && (LastMainTurnMs is not { } main || timestampMs > main))
        {
            LastMainTurnMs = timestampMs;
        }
    }

    /// <summary>An accepted API call: the next request (or the turn's end) is in flight from here while generating.</summary>
    public void Call(long timestampMs, string model)
    {
        SetModel(model);
        if (Phase == AgentPhase.Generating)
        {
            SetPhase(AgentPhase.Generating, timestampMs);
        }
    }

    public void EndTurn() => SetPhase(AgentPhase.Idle, null);

    private void SetModel(string model)
    {
        if (model.Length > 0 && !string.Equals(model, Model, StringComparison.Ordinal))
        {
            Model = model;
            Version++;
        }
    }

    private void SetPhase(AgentPhase phase, long? inFlightStartMs)
    {
        if (Phase != phase || InFlightStartMs != inFlightStartMs)
        {
            Phase = phase;
            InFlightStartMs = inFlightStartMs;
            Version++;
        }
    }
}
