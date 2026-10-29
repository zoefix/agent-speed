using System.Globalization;

namespace AgentSpeed.Core.Parsing;

/// <summary>Codex rollout state machine (METRICS 4.2) including the copied-history rule (3.5).</summary>
internal sealed class CodexParser : TranscriptParser<CodexLine>
{
    private const long CopiedHistoryToleranceMs = 2000;
    private const string RootAgentPath = "/root";

    private readonly bool _pathIsSubagent;
    private long? _fileCreatedMs;

    /// <summary>Lines of a paginated sub-agent below this ordinal copy its parent's records (METRICS 3.5).</summary>
    private long? _subagentStartOrdinal;
    private string _myAgentPath = RootAgentPath;
    private string? _metaThreadId;
    private string? _parentThreadId;

    /// <summary><c>model_provider</c> of the first <c>session_meta</c> (METRICS 3.2).</summary>
    private string? _provider;
    private long? _pendingStartMs;
    private bool _streaming;

    /// <summary>The output line that started the current response (METRICS 4.2 <c>firstOutputMs</c>).</summary>
    private long? _firstOutputMs;
    private bool _sawModernUsage;
    private CodexUsage? _previousLegacyTotals;

    /// <summary>Facts that apply from their time on (EXECUTION-MODES, Codex); also the agent's current facts.</summary>
    private ExecutionModes _context = ExecutionModes.Empty;

    public CodexParser(string agentId, IDeduper deduper)
        : base(agentId, deduper, SourceKind.Codex)
    {
        _pathIsSubagent = PathText.HasDirectoryComponent(agentId, "subagents");
        SetSubagent(_pathIsSubagent);
    }

    /// <summary>Time of the first <c>session_meta</c>, i.e. when the rollout was created.</summary>
    public long? CreatedMs => _fileCreatedMs;

    /// <summary><c>payload.id</c> of the first <c>session_meta</c>.</summary>
    public string? MetaThreadId => _metaThreadId;

    /// <summary>The spawning thread named by the first <c>session_meta</c> (METRICS 6.7).</summary>
    public string? ParentThreadId => _parentThreadId;

    public override void Reset()
    {
        base.Reset();
        _fileCreatedMs = null;
        _subagentStartOrdinal = null;
        _myAgentPath = RootAgentPath;
        _metaThreadId = null;
        _parentThreadId = null;
        _provider = null;
        _pendingStartMs = null;
        _streaming = false;
        _firstOutputMs = null;
        _sawModernUsage = false;
        _previousLegacyTotals = null;
        _context = ExecutionModes.Empty;
        SetSubagent(_pathIsSubagent);
    }

    public override void Apply(in CodexLine line, IResponseSink sink)
    {
        if (line.Status == LineStatus.Malformed)
        {
            CountMalformed();
            return;
        }

        if (line.Status != LineStatus.Valid || !line.HasTimestamp)
        {
            return;
        }

        var ts = line.TimestampMs;
        if (line.Kind == CodexLineKind.SessionMeta)
        {
            ApplySessionMeta(in line, ts);
            return;
        }

        if (_fileCreatedMs is { } created && ts < created - CopiedHistoryToleranceMs)
        {
            return;
        }

        if (_subagentStartOrdinal is { } start && line.Ordinal is { } ordinal && ordinal < start)
        {
            return;
        }

        Touch(ts);
        RecordTools(in line.Tools, ts);
        switch (line.Kind)
        {
            case CodexLineKind.TurnContext:
                SetModel(line.Model);
                SetProjectFromCwd(line.Cwd);
                ApplyTurnFacts(in line);
                break;
            case CodexLineKind.ResponseItem:
                ApplyResponseItem(in line, ts);
                break;
            case CodexLineKind.EventMsg:
                ApplyEvent(in line, ts, sink);
                break;
            case CodexLineKind.TokenUsageRecord:
                ApplyModernUsage(in line, ts, sink);
                break;
        }
    }

    protected override CodexLine Extract(ReadOnlySpan<byte> line) => CodexLineReader.Read(line);

    // Only the first session_meta applies; sub-agents copy their parent's afterwards.
    private void ApplySessionMeta(in CodexLine line, long ts)
    {
        if (_fileCreatedMs is not null)
        {
            return;
        }

        _fileCreatedMs = ts;
        _subagentStartOrdinal = line.SubagentStartOrdinal;
        Touch(ts);
        SetProjectFromCwd(line.Cwd);
        _myAgentPath = line.AgentPath ?? RootAgentPath;
        _metaThreadId = line.Id ?? _metaThreadId;
        _parentThreadId = line.ParentThreadId ?? line.SpawnParentThreadId;
        _provider = line.ModelProvider;
        MarkChanged();
        SetSubagent(IsSubagent || line.ThreadSourceIsSubagent || line.SourceHasSubagent);
    }

    private void ApplyResponseItem(in CodexLine line, long ts)
    {
        switch (line.PayloadKind)
        {
            case CodexPayloadKind.Reasoning or CodexPayloadKind.CallSuffix:
            case CodexPayloadKind.Message when line.Role == CodexRole.Assistant:
                if (!_streaming)
                {
                    _firstOutputMs = ts;
                }

                _streaming = true;
                SetPhase(AgentPhase.Generating, ts, _pendingStartMs ?? ts);
                break;
            case CodexPayloadKind.OutputSuffix:
            case CodexPayloadKind.Message when line.Role is CodexRole.User or CodexRole.Developer or CodexRole.System:
                OnInput(ts);
                break;
            case CodexPayloadKind.AgentMessage
                when string.Equals(line.Recipient, _myAgentPath, StringComparison.Ordinal):
                if (!_streaming)
                {
                    _pendingStartMs = ts;
                }

                break;
        }
    }

    private void OnInput(long ts)
    {
        if (_streaming)
        {
            return;
        }

        _pendingStartMs = ts;
        SetPhase(AgentPhase.Generating, ts, _pendingStartMs);
    }

    private void ApplyEvent(in CodexLine line, long ts, IResponseSink sink)
    {
        switch (line.PayloadKind)
        {
            case CodexPayloadKind.TaskStarted or CodexPayloadKind.UserMessage:
                OnInput(ts);
                break;
            case CodexPayloadKind.TokenCount when _sawModernUsage && _streaming:
                AfterUsage(ts);
                break;
            case CodexPayloadKind.TokenCount:
                ApplyLegacyUsage(in line, ts, sink);
                break;
            case CodexPayloadKind.TaskComplete or CodexPayloadKind.TurnAborted:
                _pendingStartMs = null;
                _streaming = false;
                SetPhase(AgentPhase.Idle, ts, null);
                break;
            case CodexPayloadKind.ThreadSettingsApplied:
                ApplyThreadSettings(in line);
                break;
        }
    }

    /// <summary>A turn context starts a turn: turn facts are replaced by its effort and selected tier.</summary>
    private void ApplyTurnFacts(in CodexLine line)
    {
        var context = _context.Without(f => f.Scope == ExecutionModeScope.Turn)
            .With(ExecutionModeNative.Effort(line.Effort ?? line.CollaborationEffort, ExecutionModeEvidence.Recorded, ExecutionModeScope.Turn));
        if (line.ServiceTier is { } tier)
        {
            context = context.Merge(ExecutionModeNative.OpenAiTier(tier, ExecutionModeEvidence.Selected, ExecutionModeScope.Turn));
        }

        SetContext(context);
    }

    /// <summary><c>thread_settings_applied</c>: the thread's selected tier and effort from now on; a non-string clears one.</summary>
    private void ApplyThreadSettings(in CodexLine line)
    {
        var context = _context;
        if (line.TierSetting.Present)
        {
            context = context.Without(f => IsSessionSelection(f, ExecutionModeKind.ServiceTier, "openai"))
                .Merge(ExecutionModeNative.OpenAiTier(line.TierSetting.Value, ExecutionModeEvidence.Selected, ExecutionModeScope.Session));
        }

        if (line.EffortSetting.Present)
        {
            context = context.Without(f => IsSessionSelection(f, ExecutionModeKind.Effort, null))
                .With(ExecutionModeNative.Effort(line.EffortSetting.Value, ExecutionModeEvidence.Selected, ExecutionModeScope.Session));
        }

        SetContext(context);
    }

    private static bool IsSessionSelection(ExecutionModeFact fact, ExecutionModeKind kind, string? provider) =>
        fact.Kind == kind && fact.Evidence == ExecutionModeEvidence.Selected && fact.Scope == ExecutionModeScope.Session
        && string.Equals(fact.Provider, provider, StringComparison.Ordinal);

    private void SetContext(ExecutionModes context)
    {
        _context = context;
        Modes = context;
    }

    private string ThreadId => _metaThreadId ?? AgentId;

    private void ApplyModernUsage(in CodexLine line, long ts, IResponseSink sink)
    {
        _sawModernUsage = true;
        var key = line.ResponseId
            ?? string.Create(CultureInfo.InvariantCulture, $"codex:{line.ThreadId ?? ThreadId}:{ts}");
        if (!Deduper.TryClaim(key, AgentId, out _))
        {
            return;
        }

        // A recorded request tier belongs to this response's snapshot only, never to the context.
        var modes = line.ServiceTier is { } tier
            ? _context.Merge(ExecutionModeNative.OpenAiTier(tier, ExecutionModeEvidence.Recorded, ExecutionModeScope.Request))
            : _context;
        Emit(key, line.Usage, ts, sink, modes);
    }

    private void ApplyLegacyUsage(in CodexLine line, long ts, IResponseSink sink)
    {
        if (_sawModernUsage || line.Info != CodexInfoKind.Object || !line.HasTotal)
        {
            return;
        }

        var total = line.Total;
        TokenCounts tokens;
        if (_previousLegacyTotals is not { } previous)
        {
            tokens = line.HasLast ? line.Last.Tokens : total.Tokens;
        }
        else
        {
            var delta = total.Minus(previous);
            if (delta.TotalTokens == 0)
            {
                return;
            }

            tokens = delta.AnyNegative ? line.Last.Tokens : delta.Tokens;
        }

        _previousLegacyTotals = total;
        var key = string.Create(
            CultureInfo.InvariantCulture, $"codex-legacy:{ThreadId}:{total.TotalTokens}");
        if (!Deduper.TryClaim(key, AgentId, out _))
        {
            return;
        }

        Emit(key, tokens, ts, sink, _context);
    }

    private void Emit(string key, TokenCounts tokens, long ts, IResponseSink sink, ExecutionModes modes)
    {
        sink.OnResponse(new ParsedResponse(key, SourceKind.Codex, AgentId, Model, _pendingStartMs, ts, tokens, Provider: _provider)
        {
            Modes = modes,
            FirstMs = _streaming ? _firstOutputMs : null,
        }, true);
        AfterUsage(ts);
    }

    private void AfterUsage(long ts)
    {
        _pendingStartMs = ts;
        _streaming = false;
        SetPhase(AgentPhase.Tools, ts, null);
    }
}
