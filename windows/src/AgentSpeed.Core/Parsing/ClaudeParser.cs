namespace AgentSpeed.Core.Parsing;

/// <summary>
/// Claude Code / Claude Desktop transcript state machine (METRICS 4.1); with <see cref="ForQoderCli"/> the same machine with the
/// differences of Qoder CLI (METRICS 4.19).
/// </summary>
internal sealed class ClaudeParser : TranscriptParser<ClaudeLine>
{
    private const int MergeCacheCapacity = 64;

    /// <summary>Keys a Qoder CLI line claimed without usage, remembered so that a later line with usage completes them.</summary>
    private const int ClaimedCapacity = 4096;

    private const string QoderCliKeyPrefix = "qodercli:";

    private readonly bool _pathIsSubagent;
    private readonly bool _qoderCli;
    private readonly HashSet<string> _claimedOnly = new(StringComparer.Ordinal);
    private readonly Queue<string> _claimedOrder = new();
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
        : this(agentId, deduper, qoderCli: false)
    {
    }

    private ClaudeParser(string agentId, IDeduper deduper, bool qoderCli)
        : base(agentId, deduper, qoderCli ? SourceKind.QoderCli : SourceKind.ClaudeCode)
    {
        _qoderCli = qoderCli;
        _pathIsSubagent = PathText.HasDirectoryComponent(agentId, "subagents");
        SetSubagent(_pathIsSubagent);
    }

    /// <summary>
    /// A Qoder CLI transcript (METRICS 4.19): always <see cref="SourceKind.QoderCli"/>, no <c>api-request</c> lines, keys prefixed
    /// <c>qodercli:</c>, assistant lines without usage only claim their key, and no effort, speed or actions.
    /// </summary>
    public static ClaudeParser ForQoderCli(string agentId, IDeduper deduper) => new(agentId, deduper, qoderCli: true);

    public override void Reset()
    {
        base.Reset();
        _recent.Clear();
        _recentOrder.Clear();
        _claimedOnly.Clear();
        _claimedOrder.Clear();
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
        if (!_qoderCli && !SourceDecided && line.Entrypoint != EntrypointKind.None)
        {
            SetSource(line.Entrypoint == EntrypointKind.Desktop ? SourceKind.ClaudeDesktop : SourceKind.ClaudeCode);
        }

        SetProjectFromCwd(line.Cwd);
        if (line.IsSidechain)
        {
            SetSubagent(true);
        }

        if (!_qoderCli)
        {
            ApplyUltracode(in line);
            if (line.Kind == ClaudeLineKind.User)
            {
                RecordTools(in line.Tools, ts);
            }
        }

        switch (line.Kind)
        {
            // Qoder CLI writes no api-request lines: one would be anything else (METRICS 4.19).
            case ClaudeLineKind.ApiRequest when !_qoderCli:
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
            case ClaudeLineKind.Assistant when _qoderCli:
                ApplyQoderCliAssistant(in line, ts, sink);
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

    /// <summary>
    /// A Qoder CLI assistant line (METRICS 4.19): one line per call, written when it ends (<c>firstMs</c> null), keyed by
    /// <c>message.id</c>, else <c>uuid</c>. A line without usage, or with zero usage, creates no response but claims its key,
    /// ends the pending start and names the agent's model.
    /// </summary>
    private void ApplyQoderCliAssistant(in ClaudeLine line, long ts, IResponseSink sink)
    {
        if (string.Equals(line.Model, "<synthetic>", StringComparison.Ordinal) || (line.MessageId ?? line.Uuid) is not { } id)
        {
            return;
        }

        var key = QoderCliKeyPrefix + id;
        var claimed = Deduper.TryClaim(key, AgentId, out var owner);
        if (!claimed && !string.Equals(owner, AgentId, StringComparison.Ordinal))
        {
            return;
        }

        var start = _pendingStartMs;
        var final = line.StopReason != StopReasonKind.Missing;
        var response = _recent.GetValueOrDefault(key);
        if (QoderCliTokens(in line) is { } tokens)
        {
            bool isCompletion;
            if (response is { } previous)
            {
                response = previous with
                {
                    EndMs = Math.Max(previous.EndMs, ts),
                    Tokens = TokenCounts.Max(previous.Tokens, tokens),
                    Model = line.Model ?? previous.Model,
                    Final = previous.Final || final,
                };
                isCompletion = false;
            }
            else
            {
                // A key claimed by an earlier line without usage has no response yet; one seen before a reset or beyond the
                // merge cache is replayed as an update.
                isCompletion = claimed || ForgetClaimed(key);
                response = new ParsedResponse(key, Source, AgentId, line.Model, start, ts, tokens, final);
                _pendingStartMs = null;
                _preciseStart = false;
            }

            Remember(response);
            SetModel(response.Model);
            sink.OnResponse(response, isCompletion);
        }
        else
        {
            if (response is { Final: false } open && final)
            {
                response = open with { Final = true };
                Remember(response);
                sink.OnResponse(response, isCompletion: false);
            }
            else if (claimed)
            {
                RememberClaimed(key);
            }

            _pendingStartMs = null;
            _preciseStart = false;
            SetModel(line.Model);
        }

        switch (line.StopReason)
        {
            case StopReasonKind.ToolUse:
            case StopReasonKind.Missing when line.HasToolUse:
                SetPhase(AgentPhase.Tools, ts, null);
                break;
            case StopReasonKind.Missing or StopReasonKind.PauseTurn:
                SetPhase(AgentPhase.Generating, ts, response is not null ? response.StartMs ?? ts : start ?? ts);
                break;
            default:
                SetPhase(AgentPhase.Idle, ts, null);
                break;
        }
    }

    /// <summary>
    /// Claude's tokens (METRICS 3.2) except <c>input</c>: Qoder's <c>input_tokens</c> already includes the cache read when it
    /// covers it (its own models), else Claude's sum (a custom Anthropic provider). Null when input and output are both 0.
    /// </summary>
    private static TokenCounts? QoderCliTokens(in ClaudeLine line)
    {
        var usage = line.Usage;
        var input = line.UsageInputTokens >= usage.CachedInput + usage.CacheWriteInput ? line.UsageInputTokens : usage.Input;
        return input == 0 && usage.Output == 0 ? null : usage with { Input = input };
    }

    private void RememberClaimed(string key)
    {
        if (!_claimedOnly.Add(key))
        {
            return;
        }

        _claimedOrder.Enqueue(key);
        if (_claimedOrder.Count > ClaimedCapacity && _claimedOrder.TryDequeue(out var oldest))
        {
            _claimedOnly.Remove(oldest);
        }
    }

    private bool ForgetClaimed(string key) => _claimedOnly.Remove(key);

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
