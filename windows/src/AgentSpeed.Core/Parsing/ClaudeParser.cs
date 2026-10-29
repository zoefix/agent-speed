namespace AgentSpeed.Core.Parsing;

/// <summary>Claude Code / Claude Desktop transcript state machine (METRICS 4.1).</summary>
internal sealed class ClaudeParser : TranscriptParser<ClaudeLine>
{
    private const int MergeCacheCapacity = 64;

    private readonly bool _pathIsSubagent;
    private readonly Dictionary<string, ParsedResponse> _recent = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();
    private static readonly ExecutionModeFact Ultracode =
        ExecutionModeNative.Effort("ultracode", ExecutionModeEvidence.Recorded, ExecutionModeScope.Request)!;

    private long? _pendingStartMs;
    private bool _preciseStart;

    // Reasoning effort (EXECUTION-MODES, Claude Code): ultracode switches, the latest response's facts and line effort.
    private bool _ultraSession;
    private bool _ultraTurn;
    private ExecutionModes _latestModes = ExecutionModes.Empty;
    private ExecutionModeFact? _lineEffort;

    public ClaudeParser(string agentId, IDeduper deduper)
        : base(agentId, deduper, SourceKind.ClaudeCode)
    {
        _pathIsSubagent = PathText.HasDirectoryComponent(agentId, "subagents");
        SetSubagent(_pathIsSubagent);
    }

    public override void Reset()
    {
        base.Reset();
        _recent.Clear();
        _recentOrder.Clear();
        _pendingStartMs = null;
        _preciseStart = false;
        _ultraSession = false;
        _ultraTurn = false;
        _latestModes = ExecutionModes.Empty;
        _lineEffort = null;
        SetSubagent(_pathIsSubagent);
    }

    public override void Apply(in ClaudeLine line, IResponseSink sink)
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
        Touch(ts);
        if (!SourceDecided && line.Entrypoint != EntrypointKind.None)
        {
            SetSource(line.Entrypoint == EntrypointKind.Desktop ? SourceKind.ClaudeDesktop : SourceKind.ClaudeCode);
        }

        SetProjectFromCwd(line.Cwd);
        if (line.IsSidechain)
        {
            SetSubagent(true);
        }

        ApplyUltracode(in line);
        if (line.Kind == ClaudeLineKind.User)
        {
            RecordTools(in line.Tools, ts);
        }

        switch (line.Kind)
        {
            case ClaudeLineKind.ApiRequest:
                _pendingStartMs = ts;
                _preciseStart = true;
                SetPhase(AgentPhase.Generating, ts, ts);
                break;
            case ClaudeLineKind.User when line.IsInterrupt || line.ToolEndsTurn:
                _pendingStartMs = null;
                _preciseStart = false;
                SetPhase(AgentPhase.Idle, ts, null);
                break;
            case ClaudeLineKind.User:
                if (!_preciseStart)
                {
                    _pendingStartMs = ts;
                }

                SetPhase(AgentPhase.Generating, ts, _pendingStartMs);
                break;
            case ClaudeLineKind.Assistant:
                ApplyAssistant(in line, ts, sink);
                break;
        }
    }

    protected override ClaudeLine Extract(ReadOnlySpan<byte> line) => ClaudeLineReader.Read(line);

    private void ApplyAssistant(in ClaudeLine line, long ts, IResponseSink sink)
    {
        if (string.Equals(line.Model, "<synthetic>", StringComparison.Ordinal) || line.Key is not { } key)
        {
            return;
        }

        ParsedResponse response;
        bool isCompletion;
        var recorded = ExecutionModeNative.Effort(line.RecordedEffort, ExecutionModeEvidence.Recorded, ExecutionModeScope.Request);
        var lineModes = ExecutionModes.Of(IsUltracode ? Ultracode : recorded).Merge(ExecutionModeNative.ClaudeSpeed(line.Speed));
        if (Deduper.TryClaim(key, AgentId, out var owner))
        {
            response = Create(key, in line, ts, lineModes);
            isCompletion = true;
        }
        else if (!string.Equals(owner, AgentId, StringComparison.Ordinal))
        {
            return;
        }
        else if (_recent.TryGetValue(key, out var previous))
        {
            response = previous with
            {
                EndMs = Math.Max(previous.EndMs, ts),
                Tokens = TokenCounts.Max(previous.Tokens, line.Usage),
                Model = line.Model ?? previous.Model,
                FirstMs = Math.Min(previous.FirstMs ?? ts, ts),
                Final = previous.Final || line.StopReason != StopReasonKind.Missing,
                Fast = previous.Fast || line.Fast,
                Modes = previous.Modes.Merge(lineModes),
            };
            isCompletion = false;
        }
        else
        {
            // Seen by this file before a reset or beyond the merge cache: replay it as an update.
            response = Create(key, in line, ts, lineModes);
            isCompletion = false;
        }

        Remember(response);
        SetModel(response.Model);
        RecordTools(in line.Tools, ts);
        _latestModes = response.Modes;
        _lineEffort = recorded ?? _lineEffort;
        RefreshModes();
        sink.OnResponse(response, isCompletion);

        switch (line.StopReason)
        {
            case StopReasonKind.ToolUse:
            case StopReasonKind.Missing when line.HasToolUse:
                SetPhase(AgentPhase.Tools, ts, null);
                break;
            case StopReasonKind.Missing or StopReasonKind.PauseTurn:
                SetPhase(AgentPhase.Generating, ts, response.StartMs ?? ts);
                break;
            default:
                SetPhase(AgentPhase.Idle, ts, null);
                break;
        }
    }

    private ParsedResponse Create(string key, in ClaudeLine line, long ts, ExecutionModes modes)
    {
        var response = new ParsedResponse(
            key, Source, AgentId, line.Model, _pendingStartMs, ts, line.Usage, line.StopReason != StopReasonKind.Missing,
            Fast: line.Fast) { Modes = modes, FirstMs = ts };
        _pendingStartMs = null;
        _preciseStart = false;
        return response;
    }

    private bool IsUltracode => _ultraSession || _ultraTurn;

    /// <summary>
    /// <c>ultra_effort_enter</c> / <c>_exit</c> switch the session's ultracode; <c>workflow_keyword_request</c> turns it on
    /// for the turn, which the next prompt line ends.
    /// </summary>
    private void ApplyUltracode(in ClaudeLine line)
    {
        var before = IsUltracode;
        switch (line.Kind)
        {
            case ClaudeLineKind.Attachment when line.Marker is UltraMarker.SessionEnter or UltraMarker.SessionExit:
                _ultraSession = line.Marker == UltraMarker.SessionEnter;
                break;
            case ClaudeLineKind.Attachment when line.Marker == UltraMarker.WorkflowKeyword:
                _ultraTurn = true;
                break;
            case ClaudeLineKind.User when line.IsPrompt:
                _ultraTurn = false;
                break;
        }

        if (IsUltracode != before)
        {
            RefreshModes();
        }
    }

    /// <summary>The latest response's facts, its effort replaced by ultracode or the latest line's recorded effort.</summary>
    private void RefreshModes() =>
        Modes = _latestModes.Without(f => f.Kind == ExecutionModeKind.Effort).With(IsUltracode ? Ultracode : _lineEffort);

    private void Remember(ParsedResponse response)
    {
        if (!_recent.ContainsKey(response.Key))
        {
            if (_recentOrder.Count >= MergeCacheCapacity && _recentOrder.TryDequeue(out var oldest))
            {
                _recent.Remove(oldest);
            }

            _recentOrder.Enqueue(response.Key);
        }

        _recent[response.Key] = response;
    }
}
