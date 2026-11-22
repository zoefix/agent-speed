using System.Text.Json;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Harnesses.HarnessJson;

namespace AgentSpeed.Core.OpenBitFun;

internal sealed record OpenBitFunRound(
    string Turn, string Id, string? Model, long? Start, long? End, long? Duration, bool Completed, long Attempts);

// Extract only session metadata and request timing/usage; do not retain prompts or response text.
internal sealed record OpenBitFunLine
{
    public bool Malformed { get; init; }
    public string? Session { get; init; }
    public string? Parent { get; init; }
    public bool Subagent { get; init; }
    public string? Cwd { get; init; }
    public long Created { get; init; }
    public string? Turn { get; init; }
    public string? Operation { get; init; }
    public string? RoundId { get; init; }
    public string? Trace { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public long? Start { get; init; }
    public long? End { get; init; }
    public long? Duration { get; init; }
    public bool Completed { get; init; }
    public TokenCounts? Usage { get; init; }
    public IReadOnlyList<OpenBitFunRound>? Rounds { get; init; }

    public static OpenBitFunLine Read(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var reader = new Utf8JsonReader(bytes, JsonScan.Options);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new() { Malformed = true };
            if (Get(root, "modelRounds") is { ValueKind: JsonValueKind.Array } rounds
                && String(root, "turnId") is { } turn)
            {
                var turnCompleted = String(root, "status") is "completed" or "cancelled" or "error";
                List<OpenBitFunRound> measurements = [];
                foreach (var round in rounds.EnumerateArray())
                {
                    if (String(round, "id") is not { } id) continue;
                    var end = Timestamp(Get(round, "endTime"));
                    var status = String(round, "status");
                    measurements.Add(new(turn, id, String(round, "effectiveModelName") ?? String(round, "modelConfigId"),
                        Timestamp(Get(round, "startTime")), end, Milliseconds(Get(round, "durationMs")),
                        turnCompleted || end is not null || status is "completed" or "failed" or "cancelled" or "interrupted",
                        Count(round, "attemptCount")));
                }
                return new() { Session = String(root, "sessionId"), Turn = turn, Rounds = measurements,
                    Start = Timestamp(Get(root, "startTime")), End = Timestamp(Get(root, "endTime")), Completed = turnCompleted };
            }
            var trace = String(root, "trace_id");
            if (trace is null)
            {
                var relationship = Get(root, "relationship");
                return new()
                {
                    Session = String(root, "sessionId") ?? String(root, "session_id"),
                    Parent = String(relationship, "parentSessionId") ?? String(relationship, "parent_session_id"),
                    Subagent = (String(root, "sessionKind") ?? String(root, "session_kind")) == "subagent"
                        || String(relationship, "kind") == "subagent",
                    Cwd = String(root, "workspacePath") ?? String(root, "workspace_path"),
                    Created = Milliseconds(Get(root, "createdAt")) ?? Milliseconds(Get(root, "created_at")) ?? 0,
                };
            }

            if (Milliseconds(Get(root, "version")) != 1) return new();
            var request = Get(root, "request");
            var timing = Get(root, "timing");
            var response = Get(root, "response");
            var usage = Get(response, "usage");
            var output = Count(usage, "candidatesTokenCount");
            return new()
            {
                Trace = trace, Session = String(root, "session_id"), Turn = String(root, "turn_id"),
                Operation = String(root, "operation_kind"), RoundId = String(root, "operation_id"),
                Model = String(request, "model_id"), Provider = String(request, "provider"),
                Start = Timestamp(Get(timing, "started_at")), End = Timestamp(Get(timing, "finished_at")),
                Duration = Milliseconds(Get(timing, "duration_ms")), Completed = response.ValueKind == JsonValueKind.Object,
                Usage = usage.ValueKind == JsonValueKind.Object
                    ? new TokenCounts(Count(usage, "promptTokenCount"), Count(usage, "cachedContentTokenCount"),
                        Count(usage, "cacheCreationTokenCount"), output, Math.Min(output, Count(usage, "reasoningTokenCount"))) : null,
            };
        }
        catch (JsonException)
        {
            return new() { Malformed = true };
        }
    }
}

internal sealed class OpenBitFunParser(string path, IDeduper deduper) : TranscriptParser(path, deduper, SourceKind.OpenBitFun)
{
    private readonly Dictionary<string, ParsedResponse> _responses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpenBitFunLine> _traces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long? Start, long? End, bool Closed)> _turns = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Turn, string Round), OpenBitFunRound> _rounds = [];
    private Dictionary<(string Turn, string Round), OpenBitFunUsage[]>? _usage;
    private readonly HashSet<(string Turn, string Round)> _baselineRounds = [];
    public string? SessionId { get; private set; }
    public string? ParentSessionId { get; private set; }
    public long CreatedMs { get; private set; }

    public override ILineBatch NewBatch() => new OpenBitFunBatch(this);

    public override void Reset()
    {
        base.Reset();
        SessionId = ParentSessionId = null;
        CreatedMs = 0;
        _responses.Clear();
        _traces.Clear();
        _turns.Clear();
        _rounds.Clear();
        _usage = null;
        _baselineRounds.Clear();
    }

    internal void Apply(OpenBitFunLine line)
    {
        if (line.Malformed) { CountMalformed(); return; }
        if (line.Rounds is { } rounds && line.Turn is { } turn)
        {
            if (line.Session != SessionId) return;
            if (_turns.ContainsKey(turn))
                foreach (var id in _rounds.Keys.Where(key => key.Turn == turn).ToArray()) _rounds.Remove(id);
            foreach (var round in rounds) _rounds[(turn, round.Id)] = round;
            _turns[turn] = (line.Start ?? rounds.Min(round => round.Start),
                line.End ?? rounds.Max(round => round.End ?? round.Start), line.Completed);
        }
        else if (line.Trace is { } trace)
        {
            if (line.Session == SessionId && (line.Completed || _traces.GetValueOrDefault(trace)?.Completed != true))
                _traces[trace] = line;
        }
        else if (line.Session is { } session)
        {
            SessionId = session;
            ParentSessionId = line.Parent;
            CreatedMs = line.Created;
            IsSubagent = line.Subagent;
            SetProjectFromCwd(line.Cwd);
        }
    }

    internal void SetUsage(OpenBitFunUsageSnapshot snapshot)
    {
        var hasUsage = snapshot.Records.Count > 0;
        if (!hasUsage && _usage is not null) _traces.Clear();
        _usage = hasUsage ? snapshot.Records.GroupBy(record => (record.Turn, record.Round))
            .ToDictionary(group => group.Key,
                group => group.GroupBy(record => record.Attempt, StringComparer.Ordinal).Select(attempt => attempt.Last()).ToArray()) : null;
    }

    internal void SetBaseline(IEnumerable<(string Turn, string Round)> rounds) => _baselineRounds.UnionWith(rounds);

    internal void ApplyRounds(IResponseSink sink)
    {
        if (SessionId is null) return;
        var active = new HashSet<string>(StringComparer.Ordinal);
        long? pending = null;
        foreach (var (trace, line) in _traces)
        {
            var key = "openbitfun:" + trace;
            if (_usage is null && line.Start is { } sent)
            {
                var time = line.End ?? sent;
                if (time >= (LastLineMs ?? long.MinValue)) SetModel(line.Model);
                Touch(time);
                if (!line.Completed)
                {
                    bool closed;
                    if (line.Turn is { } turn) closed = _turns.TryGetValue(turn, out var state) && state.Closed;
                    else
                    {
                        var owners = _turns.Values.Where(t => t.Start <= sent && (!t.Closed || sent <= t.End)).Take(2).ToArray();
                        closed = owners.Length == 1 && owners[0].Closed;
                    }
                    if (!closed) pending = Math.Min(pending ?? sent, sent);
                }
            }
            if (!line.Completed || line.End is not { } end || line.Usage is not { } tokens) continue;
            string? group = null;
            if (line.Operation == "model_round" && line.Turn is { } owner && line.RoundId is { } id)
                group = $"openbitfun:round:{SessionId}:{owner}:{id}";
            else if (line.Operation is null && line.Start is { } start)
            {
                var matches = _rounds.Values.Where(r => (line.Turn is null || line.Turn == r.Turn)
                    && r.Start <= start && end <= r.End).Take(2).ToArray();
                if (matches.Length == 1) group = $"openbitfun:round:{SessionId}:{matches[0].Turn}:{matches[0].Id}";
            }
            if (_usage is not null)
            {
                if (_responses.TryGetValue(key, out var old)) Withdraw(old with { TraceRoundKey = group ?? old.TraceRoundKey }, sink);
                continue;
            }
            var begin = line.Duration is { } duration ? (duration <= 900_000 ? end - duration : (long?)null) : line.Start;
            active.Add(key);
            Upsert(new ParsedResponse(key, Source, AgentId, line.Model, begin, end, tokens, Provider: line.Provider)
                { TraceRoundKey = group }, sink);
        }
        foreach (var round in _rounds.Values.OrderBy(round => round.End ?? round.Start ?? 0))
        {
            if (round.Start is { } sent)
            {
                var time = round.End ?? sent;
                if (time >= (LastLineMs ?? long.MinValue)) SetModel(round.Model);
                Touch(time);
                if (!round.Completed) pending = Math.Min(pending ?? sent, sent);
            }
            var identity = (round.Turn, round.Id);
            if (_usage is null || _baselineRounds.Contains(identity) || !round.Completed || round.End is not { } end
                || !_usage.TryGetValue(identity, out var attempts) || attempts.Length < round.Attempts) continue;

            var tokens = new TokenCounts();
            var measured = true;
            foreach (var attempt in attempts)
            {
                if (attempt.Tokens is not { } usage) { measured = false; continue; }
                tokens = new(TokenCounts.Clamp(tokens.Input + usage.Input), TokenCounts.Clamp(tokens.CachedInput + usage.CachedInput),
                    TokenCounts.Clamp(tokens.CacheWriteInput + usage.CacheWriteInput), TokenCounts.Clamp(tokens.Output + usage.Output),
                    TokenCounts.Clamp(tokens.Reasoning + usage.Reasoning));
            }
            var duration = round.Duration ?? (end - round.Start);
            var start = measured && duration is > 0 and <= 900_000 ? end - duration : null;
            var model = attempts.Last().Model ?? round.Model;
            var key = $"openbitfun:round:{SessionId}:{round.Turn}:{round.Id}";
            var record = new ParsedResponse(key, SourceKind.OpenBitFun, AgentId, model, start, end, tokens,
                Provider: attempts.Last().Provider, RequestCount: attempts.Length);
            active.Add(key);
            Upsert(record, sink);
        }
        foreach (var record in _responses.Values.Where(r => r.RequestCount > 0 && !active.Contains(r.Key)).ToArray())
            Withdraw(record, sink);
        if (LastLineMs is { } last)
            SetPhase(pending is not null ? AgentPhase.Generating : AgentPhase.Idle, pending ?? last, pending);
    }

    private void Withdraw(ParsedResponse record, IResponseSink sink) =>
        Upsert(record with { Tokens = default, StartMs = null, RequestCount = 0 }, sink);

    private void Upsert(ParsedResponse record, IResponseSink sink)
    {
        if (_responses.TryGetValue(record.Key, out var old) && old == record) return;
        var completion = Deduper.TryClaim(record.Key, AgentId, out var owner);
        if (!completion && !string.Equals(owner, AgentId, StringComparison.Ordinal)) return;
        _responses[record.Key] = record;
        sink.OnResponse(record, completion && old is null && record.RequestCount > 0);
    }
}

internal sealed class OpenBitFunBatch(OpenBitFunParser owner) : ILineBatch
{
    private readonly List<OpenBitFunLine> _lines = [];
    public OpenBitFunUsageSnapshot? Usage { get; set; }
    public IReadOnlyList<(string Turn, string Round)>? Baseline { get; set; }
    public int Count => _lines.Count + (Usage is not null ? 1 : 0);
    public void OnLine(ReadOnlySpan<byte> line) => _lines.Add(OpenBitFunLine.Read(line));
    public void OnMalformed() => _lines.Add(new() { Malformed = true });
    public void ApplyTo(IResponseSink sink)
    {
        if (Baseline is { } baseline) owner.SetBaseline(baseline);
        if (Usage is null && _lines.Count == 0) return;
        if (Usage is { } usage) owner.SetUsage(usage);
        foreach (var line in _lines) owner.Apply(line);
        owner.ApplyRounds(sink);
    }
    public void Clear() { _lines.Clear(); Usage = null; Baseline = null; }
}
