using System.Globalization;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Pi;

/// <summary>
/// Pi session state machine (METRICS 4.13): the omp rules of 4.7 with Pi's root, <c>durationMs</c>, side usage (usage
/// entries, tool results, compaction and branch summaries) and thinking levels (EXECUTION-MODES, Pi).
/// </summary>
internal sealed class PiParser : TranscriptParser<PiLine>
{
    /// <summary>Entries this much older than the header were copied from another session (forks, branches).</summary>
    public const long CopiedHistoryMs = 2000;

    private const int HeaderProbeLines = 2;

    private readonly HashSet<string> _pendingToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParsedResponse> _responses = new(StringComparer.Ordinal);
    private ExecutionModes _context = ExecutionModes.Empty;
    private bool _headerSeen;
    private bool _headerProbed;

    /// <param name="parentPath">The session file a package sub-agent belongs to (<see cref="PiPaths.ParentFileOf"/>); null for a main session.</param>
    public PiParser(string agentId, IDeduper deduper, string? parentPath)
        : base(agentId, deduper, SourceKind.Pi)
    {
        ParentPath = parentPath;
        RestorePathState();
    }

    /// <summary>A package sub-agent's session file (<c>&lt;stem&gt;.jsonl</c> next to its folder); null for a main session.</summary>
    public string? ParentPath { get; }

    /// <summary>The header <c>timestamp</c>; null until a header with a parseable timestamp was read.</summary>
    public long? HeaderMs { get; private set; }

    public bool HeaderSeen => _headerSeen;

    /// <summary>A file in a folder named after a session file is that session's sub-agent (METRICS 4.13).</summary>
    public static PiParser ForPath(string path, IDeduper deduper) => new(path, deduper, PiPaths.ParentFileOf(path));

    public override void Reset()
    {
        base.Reset();
        _pendingToolCalls.Clear();
        _responses.Clear();
        _context = ExecutionModes.Empty;
        _headerSeen = false;
        _headerProbed = false;
        HeaderMs = null;
        RestorePathState();
    }

    /// <summary>
    /// Reading that starts at an offset above 0 first takes the header from the file's first two lines (METRICS 4.7).
    /// Runs on the reading thread; the header is applied before the batch's lines.
    /// </summary>
    public override void PrepareBatch(ILineBatch batch, long startOffset)
    {
        if (startOffset <= 0 || _headerSeen || _headerProbed || batch is not PiBatch pi)
        {
            return;
        }

        _headerProbed = true;
        pi.Header = ReadHeader(AgentId);
    }

    public override ILineBatch NewBatch() => new PiBatch(this);

    public override void Apply(in PiLine line, IResponseSink sink)
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

        if (line.Kind == PiEntryKind.Session)
        {
            ApplyHeader(in line);
            return;
        }

        if (!_headerSeen || line.Id is null || line.TimestampMs is not { } ts
            || (HeaderMs is { } header && ts < header - CopiedHistoryMs))
        {
            return;
        }

        Touch(ts);
        switch (line.Kind)
        {
            case PiEntryKind.Message:
                ApplyMessage(in line, ts, sink);
                break;
            case PiEntryKind.Usage:
                if (line.Call.IsGuardedEntry)
                {
                    ApplySide(line.Id, ts, in line.Call, line.Call.Model, line.Call.Provider, sink);
                }

                break;
            case PiEntryKind.Summary:
                ApplySide(line.Id, ts, in line.Call, null, null, sink);
                break;
            case PiEntryKind.ThinkingLevelChange:
                SelectEffort(line.ThinkingLevel, ExecutionModeEvidence.Selected, ExecutionModeScope.Session);
                break;
        }
    }

    protected override PiLine Extract(ReadOnlySpan<byte> line) => PiLineReader.Read(line);

    /// <summary>The first <c>session</c> line among the first two lines of the file, else a blank line.</summary>
    internal static PiLine ReadHeader(string path)
    {
        try
        {
            using var handle = TranscriptTailer.Open(path);
            var length = RandomAccess.GetLength(handle);
            var buffer = new byte[(int)Math.Min(length, 64 * 1024)];
            var read = RandomAccess.Read(handle, buffer, 0);
            var span = buffer.AsSpan(0, read);
            for (var i = 0; i < HeaderProbeLines && !span.IsEmpty; i++)
            {
                var newline = span.IndexOf((byte)'\n');
                var raw = newline < 0 ? span : span[..newline];
                var line = PiLineReader.Read(raw);
                if (line is { Status: LineStatus.Valid, Kind: PiEntryKind.Session })
                {
                    return line;
                }

                if (newline < 0)
                {
                    break;
                }

                span = span[(newline + 1)..];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return new PiLine { Status = LineStatus.Blank };
    }

    private void ApplyHeader(in PiLine line)
    {
        if (_headerSeen)
        {
            return;
        }

        _headerSeen = true;
        HeaderMs = line.TimestampMs;
        SetProjectFromCwd(line.Cwd);
        MarkChanged();
        if (line.TimestampMs is { } ts)
        {
            Touch(ts);
        }
    }

    private void ApplyMessage(in PiLine line, long ts, IResponseSink sink)
    {
        switch (line.Role)
        {
            case PiRole.User or PiRole.Developer:
                _pendingToolCalls.Clear();
                SetPhase(AgentPhase.Generating, ts, ts);
                break;
            case PiRole.Assistant:
                ApplyAssistant(in line, ts, sink);
                break;
            case PiRole.ToolResult:
                RecordTools(in line.Tools, ts);

                // A tool's own model calls (METRICS 4.13); tool results never end a run in Pi (no yield).
                ApplySide(line.Id!, ts, in line.Call, null, null, sink);
                if (line.ToolCallId is { } callId)
                {
                    _pendingToolCalls.Remove(callId);
                }

                if (Phase == AgentPhase.Tools && _pendingToolCalls.Count == 0)
                {
                    SetPhase(AgentPhase.Generating, ts, ts);
                }

                break;
        }
    }

    private void ApplyAssistant(in PiLine line, long ts, IResponseSink sink)
    {
        var key = Key(line.Id!, line.StartedMs ?? ts);
        if (Deduper.TryGetOwner(key, out var owner) && !string.Equals(owner, AgentId, StringComparison.Ordinal))
        {
            return;
        }

        var call = line.Call;
        if (!call.IsGuardedMessage)
        {
            return;
        }

        var end = line.StartedMs is { } started && line.DurationMs is { } duration
            ? (duration > long.MaxValue - started ? long.MaxValue : started + duration)
            : ts;
        SelectEffort(line.ThinkingLevel, ExecutionModeEvidence.Recorded, ExecutionModeScope.Request);
        if (call.HasUsage)
        {
            Emit(key, line.Failed ? null : line.StartedMs, end, in call, call.Model, call.Provider, _context, sink);
            SetModel(call.Model);
        }

        RecordTools(in line.Tools, ts);

        if (line.Failed)
        {
            _pendingToolCalls.Clear();
            SetPhase(AgentPhase.Idle, ts, null);
        }
        else if (line.ToolCallIds is { Length: > 0 } calls && line.Stop is PiStop.ToolUse or PiStop.Stop or PiStop.Length)
        {
            _pendingToolCalls.Clear();
            _pendingToolCalls.UnionWith(calls);
            SetPhase(AgentPhase.Tools, ts, null);
        }
        else
        {
            SetPhase(AgentPhase.Idle, ts, null);
        }
    }

    // Side usage counts in totals only: no timing, no execution-mode facts, never the agent's model.
    private void ApplySide(string id, long ts, in PiCall call, string? model, string? provider, IResponseSink sink)
    {
        if (!call.HasUsageObject || !call.HasUsage)
        {
            return;
        }

        var key = Key(id, ts);
        if (Deduper.TryGetOwner(key, out var owner) && !string.Equals(owner, AgentId, StringComparison.Ordinal))
        {
            return;
        }

        Emit(key, null, ts, in call, model, provider, ExecutionModes.Empty, sink);
    }

    // The context holds one effort fact; the later of a selection and a recorded level replaces it (EXECUTION-MODES, Pi).
    private void SelectEffort(string? level, ExecutionModeEvidence evidence, ExecutionModeScope scope)
    {
        if (ExecutionModeNative.Effort(level, evidence, scope) is { } fact)
        {
            _context = new ExecutionModes(_context.Facts.Where(f => f.Kind != ExecutionModeKind.Effort).Append(fact));
            Modes = _context;
        }
    }

    private void Emit(string key, long? startMs, long endMs, in PiCall call, string? model, string? provider, ExecutionModes modes,
        IResponseSink sink)
    {
        var isCompletion = Deduper.TryClaim(key, AgentId, out _);
        var response = new ParsedResponse(key, SourceKind.Pi, AgentId, model, startMs, endMs, call.Tokens, Final: true, Provider: provider)
        { Modes = modes };
        if (_responses.TryGetValue(key, out var previous) && previous == response)
        {
            return;
        }

        _responses[key] = response;
        sink.OnResponse(response, isCompletion);
    }

    private static string Key(string id, long stamp) => string.Create(CultureInfo.InvariantCulture, $"pi:{id}:{stamp}");

    private void RestorePathState()
    {
        SetSource(SourceKind.Pi);
        SetSubagent(ParentPath is not null);
    }

    /// <summary>Lines of one read, with the header probed from the file start applied first.</summary>
    private sealed class PiBatch(PiParser owner) : ILineBatch, IPositionedLineSink
    {
        private readonly List<PiLine> _items = [];
        private int _malformed;

        public PiLine? Header { get; set; }

        public int Count => _items.Count;

        public void OnLine(ReadOnlySpan<byte> line) => _items.Add(owner.Extract(line));

        public void OnLine(ReadOnlySpan<byte> line, long offset, int length) => OnLine(line);

        public void OnMalformed() => _malformed++;

        public void ApplyTo(IResponseSink sink)
        {
            if (Header is { } header)
            {
                owner.Apply(in header, sink);
            }

            for (var i = 0; i < _malformed; i++)
            {
                owner.Apply(new PiLine { Status = LineStatus.Malformed }, sink);
            }

            foreach (var item in _items)
            {
                owner.Apply(in item, sink);
            }
        }

        public void Clear()
        {
            Header = null;
            _items.Clear();
            _malformed = 0;
            if (_items.Capacity > 4096)
            {
                _items.Capacity = 4096;
            }
        }
    }
}
