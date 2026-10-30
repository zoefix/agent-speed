using System.Globalization;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Pi;

namespace AgentSpeed.Core.OpenClaw;

/// <summary>
/// One OpenClaw transcript window (METRICS 4.14): the entries of Pi's format (4.7, 4.13) with OpenClaw's keys, timing and
/// tokens, mirrored delegated turns that count nothing, untimed whole-turn responses, and delegated windows that stay idle.
/// Agent id <c>&lt;database&gt;#&lt;session id&gt;</c>. Not thread-safe.
/// </summary>
internal sealed class OpenClawParser : TranscriptParser<PiLine>
{
    /// <summary>Entries this much older than the header were copied from another window (forks, restores).</summary>
    public const long CopiedHistoryMs = 2000;

    private readonly HashSet<string> _pendingToolCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ParsedResponse> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _stamps = new(StringComparer.Ordinal);
    private readonly List<OpenClawTrace> _traces = [];
    private bool _headerSeen;

    public OpenClawParser(string databasePath, OpenClawWindow window, IDeduper deduper)
        : base(AgentIdOf(databasePath, window.SessionId), deduper, SourceKind.OpenClaw)
    {
        DatabasePath = databasePath;
        Window = window;
        RestoreWindowState();
    }

    public string DatabasePath { get; }

    public string SessionId => Window.SessionId;

    /// <summary>The window row as last read; its runtime decides delegated turns.</summary>
    public OpenClawWindow Window { get; private set; }

    /// <summary>The parent window's agent id in the same database, when tracked; set by the database reader.</summary>
    public string? ParentId { get; private set; }

    /// <summary>The header <c>timestamp</c>; null until a header with a parseable timestamp was read.</summary>
    public long? HeaderMs { get; private set; }

    public bool HeaderSeen => _headerSeen;

    public static string AgentIdOf(string databasePath, string sessionId) => databasePath + "#" + sessionId;

    /// <summary>A new row of the same window: a changed runtime needs a reset first (<see cref="RuntimeChanged"/>).</summary>
    public void UpdateWindow(OpenClawWindow window)
    {
        Window = window;
        SetSubagent(window.IsSubagent);
    }

    /// <summary>The window's latest run used another runtime: its messages are read again under the new one (METRICS 4.14).</summary>
    public bool RuntimeChanged(OpenClawWindow window) =>
        window.Delegated != Window.Delegated || window.MirrorsOnly != Window.MirrorsOnly;

    public void SetParent(string? parentId)
    {
        if (!string.Equals(parentId, ParentId, StringComparison.Ordinal))
        {
            ParentId = parentId;
            MarkChanged();
        }
    }

    public override void Reset()
    {
        base.Reset();
        _pendingToolCalls.Clear();
        _responses.Clear();
        _stamps.Clear();
        _traces.Clear();
        _headerSeen = false;
        HeaderMs = null;
        RestoreWindowState();
    }

    /// <summary>
    /// Takes new <c>trace.metadata</c> rows (EXECUTION-MODES, OpenClaw): the latest row is the current value, and a row read
    /// after the responses it covers updates them like any upsert.
    /// </summary>
    public void ApplyTraces(IReadOnlyCollection<OpenClawTrace> traces, IResponseSink sink)
    {
        if (traces.Count == 0)
        {
            return;
        }

        _traces.AddRange(traces);
        Modes = _traces[^1].Modes;
        foreach (var (key, previous) in _responses.ToList())
        {
            var response = previous with { Modes = ModesAt(_stamps[key]) };
            if (response != previous)
            {
                _responses[key] = response;
                sink.OnResponse(response, isCompletion: false);
            }
        }
    }

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
        if (line.Kind != PiEntryKind.Message)
        {
            return;
        }

        switch (line.Role)
        {
            case PiRole.Assistant:
                ApplyAssistant(in line, ts, sink);
                break;
            case PiRole.User or PiRole.Developer when !Window.Delegated:
                _pendingToolCalls.Clear();
                SetPhase(AgentPhase.Generating, ts, ts);
                break;
            case PiRole.ToolResult:
                RecordTools(in line.Tools, ts);
                if (Window.Delegated)
                {
                    break;
                }

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

    protected override PiLine Extract(ReadOnlySpan<byte> line) => PiLineReader.Read(line);

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

        var failed = line.Stop is not (PiStop.Stop or PiStop.Length or PiStop.ToolUse);
        var provider = call.Provider ?? string.Empty;
        var mirror = line.Mirrored || (call.ApiIsCli && provider == "claude-cli")
            || provider.StartsWith("acp-", StringComparison.Ordinal) || Window.MirrorsOnly;

        if (!mirror && !call.UsageUnavailable && call.HasUsage)
        {
            var timed = !Window.Delegated && !call.ApiIsCli && !failed;
            Emit(key, timed ? line.StartedMs : null, ts, line.StartedMs ?? ts, call.Tokens with { Reasoning = call.ReasoningTokens }, call.Model, call.Provider, sink);
            SetModel(call.Model);
        }

        // METRICS 4.14 Mirrors: a delegated turn mirrored into the transcript adds no actions (6.10).
        if (!mirror)
        {
            RecordTools(in line.Tools, ts);
        }

        if (Window.Delegated)
        {
            return;
        }

        if (failed)
        {
            _pendingToolCalls.Clear();
            SetPhase(AgentPhase.Idle, ts, null);
        }
        else if (line.ToolCallIds is { Length: > 0 } calls)
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

    private void Emit(string key, long? startMs, long endMs, long stamp, TokenCounts tokens, string? model, string? provider, IResponseSink sink)
    {
        var isCompletion = Deduper.TryClaim(key, AgentId, out _);
        var response = new ParsedResponse(key, SourceKind.OpenClaw, AgentId, model, startMs, endMs, tokens, Final: true, Provider: provider)
        { Modes = ModesAt(stamp) };
        if (_responses.TryGetValue(key, out var previous) && previous == response)
        {
            return;
        }

        _responses[key] = response;
        _stamps[key] = stamp;
        sink.OnResponse(response, isCompletion);
    }

    // The window's latest row at or before the stamp: a trimmed row makes its attempt unknown instead of the attempt before it reaching further.
    private ExecutionModes ModesAt(long stamp)
    {
        for (var i = _traces.Count - 1; i >= 0; i--)
        {
            if (_traces[i].CreatedMs <= stamp)
            {
                return _traces[i].Modes;
            }
        }

        return ExecutionModes.Empty;
    }

    private static string Key(string id, long stamp) => string.Create(CultureInfo.InvariantCulture, $"openclaw:{id}:{stamp}");

    private void RestoreWindowState()
    {
        SetSource(SourceKind.OpenClaw);
        SetSubagent(Window.IsSubagent);
    }
}
