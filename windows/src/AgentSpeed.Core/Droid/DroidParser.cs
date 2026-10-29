using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Droid;

/// <summary>
/// One Factory Droid session (METRICS 4.16): calls, their timing, model and effort from the transcript, their tokens from
/// the growth of the sidecar's own counters. Each read cycle feeds the new lines, then the sidecar the tailer read after
/// them, then allocates the growth waiting in <c>P</c> to the open calls, which are emitted then.
/// </summary>
internal sealed class DroidParser : TranscriptParser
{
    private const string KeyPrefix = "droid:";

    private readonly List<OpenCall> _open = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private ExecutionModes _context = ExecutionModes.Empty;
    private string? _sessionModel;
    private string? _callModel;
    private long? _pendingStartMs;
    private long? _compactionOffset;
    private long _readFrom;
    private bool _readStarted;
    private bool _headerSeen;
    private bool _baselinePending;
    private DroidSidecarSignature? _sidecar;

    public DroidParser(string agentId, IDeduper deduper)
        : base(agentId, deduper, SourceKind.Droid)
    {
        SetSource(SourceKind.Droid);
    }

    /// <summary><c>session_start.parent</c>: the session a fork or successor started from, whose counters it subtracts.</summary>
    public string? ParentSessionId { get; private set; }

    /// <summary><c>session_start.callingSessionId</c>: the session whose Task tool started this worker.</summary>
    public string? CallingSessionId { get; private set; }

    /// <summary>A worker's parent transcript, <c>&lt;folder&gt;/&lt;callingSessionId&gt;.jsonl</c> (METRICS 6.7).</summary>
    public string? ParentPath => CallingSessionId is { } calling ? DroidPaths.SiblingTranscript(AgentId, calling) : null;

    /// <summary><c>H</c>: the own counters already accounted, a high-water mark.</summary>
    public DroidCounters Mark { get; private set; }

    /// <summary><c>P</c>: growth not yet allocated to a call.</summary>
    public DroidCounters Pending { get; private set; }

    public int OpenCallCount => _open.Count;

    /// <summary>The signature of the sidecar version last applied; null until one is, and after an unreadable read or unknown own counters.</summary>
    public DroidSidecarSignature? KnownSidecar => _sidecar;

    /// <summary>
    /// The signature of the parent sidecar the last attempt found unreadable: found again unchanged, the parent gives 0
    /// (METRICS 4.16 Own counters). The history analysis carries it from one analysis to the next.
    /// </summary>
    public DroidSidecarSignature? UnreadableParent { get; set; }

    /// <summary>The last sidecar read left the own counters unknown (the parent sidecar was unreadable).</summary>
    public bool CountersUnknown { get; private set; }

    /// <summary>
    /// The session's transcript starts at its end: the first sidecar read with known own counters only sets <c>H</c>;
    /// a missing sidecar leaves it 0 (METRICS 4.16).
    /// </summary>
    public void StartAtEnd() => _baselinePending = true;

    public override ILineBatch NewBatch() => new DroidBatch(this);

    public override void PrepareBatch(ILineBatch batch, long startOffset)
    {
        if (batch is DroidBatch droid)
        {
            droid.StartOffset = startOffset;
        }
    }

    /// <summary>A transcript that shrank or was replaced: <c>H</c>, <c>P</c> and the open calls start over, read from 0.</summary>
    public override void Reset()
    {
        base.Reset();
        _open.Clear();
        _seen.Clear();
        _context = ExecutionModes.Empty;
        _sessionModel = null;
        _callModel = null;
        _pendingStartMs = null;
        _compactionOffset = null;
        _readFrom = 0;
        _readStarted = true;
        _headerSeen = false;
        _baselinePending = false;
        _sidecar = null;
        UnreadableParent = null;
        CountersUnknown = false;
        ParentSessionId = null;
        CallingSessionId = null;
        Mark = default;
        Pending = default;
        SetSource(SourceKind.Droid);
    }

    /// <summary>One read cycle: the lines, then the sidecar read after them, then the allocation.</summary>
    internal void ApplyCycle(List<DroidLine> lines, int malformed, long startOffset, DroidSidecarRead? sidecar, IResponseSink sink)
    {
        if (!_readStarted)
        {
            _readFrom = startOffset;
            _readStarted = true;
        }

        for (var i = 0; i < malformed; i++)
        {
            CountMalformed();
        }

        foreach (var line in lines)
        {
            Feed(in line);
        }

        if (sidecar is { } read)
        {
            ApplySidecar(in read);
        }

        Allocate(sink);
    }

    /// <summary>Applies one line (step 1 of a read cycle); calls stay open until tokens are allocated to them.</summary>
    public void Feed(in DroidLine line)
    {
        if (line.Status == LineStatus.Malformed)
        {
            CountMalformed();
            return;
        }

        if (line.Status != LineStatus.Valid)
        {
            return;
        }

        switch (line.Kind)
        {
            case DroidLineKind.SessionStart:
                ApplyHeader(in line);
                return;
            case DroidLineKind.TurnOutcome:
                _pendingStartMs = null;
                SetPhase(AgentPhase.Idle, LastLineMs ?? 0, null);
                return;
        }

        if (line.TimestampMs is not { } ts)
        {
            return;
        }

        Touch(ts);
        switch (line.Kind)
        {
            case DroidLineKind.Message when line.Role == DroidRole.User:
                RecordTools(in line.Tools, ts);
                if (!line.UserOnly)
                {
                    _pendingStartMs = ts;
                    SetPhase(AgentPhase.Generating, ts, ts);
                }

                break;
            case DroidLineKind.Message when line.Role == DroidRole.Assistant:
                ApplyCall(in line, ts);
                break;
            case DroidLineKind.Compaction:
                _compactionOffset = line.Offset;
                if (Phase == AgentPhase.Generating)
                {
                    _pendingStartMs = ts;
                    SetPhase(AgentPhase.Generating, ts, ts);
                }

                break;
        }
    }

    /// <summary>
    /// History only (METRICS 4.16): after the history rule, own counters left without a call become one response of the
    /// session, <c>droid:&lt;session id&gt;:session</c>, at the session's last line.
    /// </summary>
    public void FinishHistory(IResponseSink sink)
    {
        if (Pending.IsZero || LastLineMs is not { } last)
        {
            return;
        }

        var key = KeyPrefix + DroidPaths.SessionIdOf(AgentId) + ":session";
        var first = Deduper.TryClaim(key, AgentId, out _);
        Emit(key, null, last, Pending, null, ExecutionModes.Empty, first && _emitted.Add(key), sink);
        Pending = default;
    }

    private void ApplyHeader(in DroidLine line)
    {
        if (_headerSeen)
        {
            return;
        }

        _headerSeen = true;
        SetProjectFromCwd(line.Cwd);
        ParentSessionId = line.Parent;
        CallingSessionId = line.CallingSessionId;
        SetSubagent(line.CallingSessionId is not null);
        MarkChanged();
    }

    private void ApplyCall(in DroidLine line, long ts)
    {
        if (line.Id is not { } id)
        {
            return;
        }

        var key = KeyPrefix + id;
        if (Deduper.TryGetOwner(key, out var owner) && !string.Equals(owner, AgentId, StringComparison.Ordinal))
        {
            return;
        }

        if (_seen.Contains(key))
        {
            RecordTools(in line.Tools, ts);
            return;
        }

        Deduper.TryClaim(key, AgentId, out _);
        _seen.Add(key);
        _context = ExecutionModes.Of(ExecutionModeNative.Effort(line.Effort, ExecutionModeEvidence.Recorded, ExecutionModeScope.Request));
        Modes = _context;
        var start = _compactionOffset ?? _readFrom;
        _open.Add(new OpenCall(
            key,
            _pendingStartMs,
            ts,
            line.ModelId,
            _context,
            Math.Min(DroidAllocation.ContextBase + line.Offset - start, DroidAllocation.MaxWeight),
            Math.Min(line.Length, DroidAllocation.MaxWeight)));
        _callModel = line.ModelId;
        SetAgentModel(line.ModelId ?? _sessionModel);
        _pendingStartMs = null;
        SetPhase(line.HasToolUse ? AgentPhase.Tools : AgentPhase.Idle, ts, null);
        RecordTools(in line.Tools, ts);
    }

    /// <summary>
    /// Step 2: a readable sidecar with known own counters sets the session model and its growth above <c>H</c> waits in
    /// <c>P</c>. An unreadable sidecar, or unknown own counters, changes nothing and is read again next cycle.
    /// </summary>
    private void ApplySidecar(in DroidSidecarRead read)
    {
        CountersUnknown = false;
        switch (read.Status)
        {
            case DroidSidecarStatus.Missing:
                _baselinePending = false;
                return;
            case DroidSidecarStatus.Unreadable:
                _sidecar = null;
                return;
            case DroidSidecarStatus.Unchanged:
                return;
        }

        DroidCounters? own = null;
        if (read.Counters is { } counters)
        {
            own = Own(counters);
            if (own is null)
            {
                _sidecar = null;
                CountersUnknown = true;
                return;
            }
        }

        _sidecar = read.Signature;
        _sessionModel = read.Model;
        SetAgentModel(_callModel ?? _sessionModel);
        var baseline = _baselinePending;
        _baselinePending = false;
        if (own is not { } known)
        {
            return;
        }

        if (!baseline)
        {
            Pending = DroidCounters.Add(Pending, DroidCounters.Minus(known, Mark));
        }

        Mark = DroidCounters.Max(Mark, known);
    }

    /// <summary>
    /// A fork or successor starts from its parent's counters, read from <c>&lt;parent&gt;.settings.json</c> in the same folder:
    /// none, or no counters, gives 0; an unreadable one leaves the own counters unknown (null) unless the last attempt
    /// found it unreadable with the same signature (then 0). Thinking is capped at the output.
    /// </summary>
    private DroidCounters? Own(in DroidCounters counters)
    {
        var own = counters;
        if (ParentSessionId is { } parent)
        {
            var read = DroidSidecar.Read(DroidPaths.SiblingSidecar(AgentId, parent));
            switch (read.Status)
            {
                case DroidSidecarStatus.Missing:
                    UnreadableParent = null;
                    break;
                case DroidSidecarStatus.Readable:
                    UnreadableParent = null;
                    if (read.Counters is { } inherited)
                    {
                        own = DroidCounters.Minus(counters, inherited);
                    }

                    break;
                default:
                    if (read.Signature is not { } signature || signature != UnreadableParent)
                    {
                        UnreadableParent = read.Signature;
                        return null;
                    }

                    break;
            }
        }

        return own with { Thinking = Math.Min(own.Thinking, own.Output) };
    }

    /// <summary>Step 3: <c>P</c> goes to the open calls by their weights, and they are emitted.</summary>
    private void Allocate(IResponseSink sink)
    {
        if (_open.Count == 0 || Pending.IsZero)
        {
            return;
        }

        var fields = new long[_open.Count, DroidCounters.FieldCount];
        var weights = new long[_open.Count];
        for (var field = 0; field < DroidCounters.FieldCount; field++)
        {
            for (var i = 0; i < _open.Count; i++)
            {
                weights[i] = DroidCounters.IsInputField(field) ? _open[i].InputWeight : _open[i].OutputWeight;
            }

            var shares = DroidAllocation.Shares(Pending[field], weights);
            for (var i = 0; i < shares.Length; i++)
            {
                fields[i, field] = shares[i];
            }
        }

        Span<long> share = stackalloc long[DroidCounters.FieldCount];
        for (var i = 0; i < _open.Count; i++)
        {
            for (var field = 0; field < DroidCounters.FieldCount; field++)
            {
                share[field] = fields[i, field];
            }

            var call = _open[i];
            Emit(call.Key, call.StartMs, call.EndMs, DroidCounters.FromFields(share), call.ModelId, call.Modes, _emitted.Add(call.Key), sink);
        }

        _open.Clear();
        Pending = default;
        MarkChanged();
    }

    private void Emit(string key, long? startMs, long endMs, in DroidCounters share, string? modelId, ExecutionModes modes, bool isCompletion,
        IResponseSink sink)
    {
        var model = modelId ?? _sessionModel;
        var response = new ParsedResponse(key, SourceKind.Droid, AgentId, model, startMs, endMs, share.ToTokens(), Final: true,
            Provider: IsCustom(model) ? "custom" : null) { Modes = modes };
        sink.OnResponse(response, isCompletion);
    }

    /// <summary>The user's own models (BYOK): <c>custom:</c> ids, ASCII case-insensitive (METRICS 3.2, 4.16).</summary>
    private static bool IsCustom(string? model) => model is not null && model.StartsWith("custom:", StringComparison.OrdinalIgnoreCase);

    // Unlike SetModel, the agent's model can become unknown again (a call without modelId and no session model).
    private void SetAgentModel(string? model)
    {
        if (!string.Equals(Model, model, StringComparison.Ordinal))
        {
            Model = model;
            MarkChanged();
        }
    }

    /// <param name="InputWeight"><c>wIn</c>: 64 KiB plus the bytes since the latest compaction (or the read start).</param>
    /// <param name="OutputWeight"><c>wOut</c>: the bytes of the call's own line.</param>
    private sealed record OpenCall(
        string Key, long? StartMs, long EndMs, string? ModelId, ExecutionModes Modes, long InputWeight, long OutputWeight);
}

/// <summary>The lines of one read with their offsets, and the sidecar as the tailer read it right after them.</summary>
internal sealed class DroidBatch(DroidParser owner) : ILineBatch, IPositionedLineSink
{
    private readonly List<DroidLine> _lines = [];
    private int _malformed;
    private long _next = -1;

    /// <summary>The offset the read started at; the input weight of a session read from there counts from it.</summary>
    public long StartOffset { get; set; }

    /// <summary>The sidecar read after the lines; null when it was not read.</summary>
    public DroidSidecarRead? Sidecar { get; set; }

    /// <summary>The sidecar version the parser last applied, which the tailer does not read again.</summary>
    public DroidSidecarSignature? KnownSidecar => owner.KnownSidecar;

    public int Count => _lines.Count;

    public void OnLine(ReadOnlySpan<byte> line) => OnLine(line, _next < 0 ? StartOffset : _next, line.Length);

    public void OnLine(ReadOnlySpan<byte> line, long offset, int length)
    {
        _lines.Add(DroidLineReader.Read(line, offset, length));
        _next = offset + length + 1;
    }

    public void OnMalformed() => _malformed++;

    public void ApplyTo(IResponseSink sink) => owner.ApplyCycle(_lines, _malformed, StartOffset, Sidecar, sink);

    public void Clear()
    {
        _lines.Clear();
        _malformed = 0;
        _next = -1;
        Sidecar = null;
        if (_lines.Capacity > 4096)
        {
            _lines.Capacity = 4096;
        }
    }
}
