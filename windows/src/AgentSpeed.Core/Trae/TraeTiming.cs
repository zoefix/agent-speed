using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using AgentSpeed.Core.Antigravity;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Parsing;
using Microsoft.Win32.SafeHandles;

namespace AgentSpeed.Core.Trae;

internal sealed record TraeTimingProof(string Session, string Turn, string History, string Config, string? Trace, TokenCounts Tokens, long CreatedMs);
internal sealed record TraeTurnEvidence(TraeTimingProof Final, IReadOnlyList<TraeTimingProof> Earlier);

/// <summary>Joins exact request usage to retained native transport evidence, and projects verified live activity.</summary>
internal sealed partial class TraeTiming
{
    private const string Generation = "call_server_generate_plan_item";
    private const string HttpTarget = "byted_aha_ffi::aha_net::http";
    private const string StreamTarget = "ai_agent::infrastructure::aha_net::stream";
    private const string TimingPrefix = "TimingCost: TimingCostOriginEvent { data: ";
    private readonly Dictionary<string, (LogFile Stamp, Graph? Graph, int Decoded)> _cache = new(StringComparer.Ordinal);
    private sealed record LogFile(string Path, long Length, long Modified);
    internal readonly record struct StoreStamp(long Size, long Fingerprint);

    public static StoreStamp? Stamp(string database)
    {
        if (AntigravityStamp.Of(database) is not { } db) return null;
        var signature = new StringBuilder("trae-timing-v2\n").Append(db.DatabaseLength).Append(':').Append(db.DatabaseWriteUtc.Ticks)
            .Append(':').Append(db.WalLength).Append(':').Append(db.WalWriteUtc.Ticks);
        foreach (var file in Logs(database)) signature.Append('\n').Append(file.Path).Append(':').Append(file.Length).Append(':').Append(file.Modified);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(signature.ToString()));
        return new StoreStamp(db.Length, BinaryPrimitives.ReadInt64LittleEndian(digest) & long.MaxValue);
    }

    private static List<LogFile> Logs(string database)
    {
        var directory = Path.GetDirectoryName(database);
        if (directory is null || Path.GetFileName(directory) is not "ai-agent" and not "ai-chat"
            || Path.GetFileName(Path.GetDirectoryName(directory)) != "ModularData") return [];
        var root = Path.GetDirectoryName(Path.GetDirectoryName(directory));
        if (root is null) return [];
        var logs = Path.Combine(root, "logs");
        try
        {
            if (!Directory.Exists(logs) || !NoLinks(logs)) return [];
            var result = new List<LogFile>();
            var launches = Directory.EnumerateDirectories(logs).Take(1025).ToArray();
            if (launches.Length > 1024) return [];
            foreach (var launch in launches)
            {
                var modular = Path.Combine(launch, "Modular");
                if (!Directory.Exists(modular) || !NoLinks(modular)) continue;
                foreach (var path in Directory.EnumerateFiles(modular, "ai_agent*.alaudalog").Take(65))
                {
                    if (!NoLinks(path)) continue;
                    var file = new FileInfo(path);
                    result.Add(new LogFile(path, file.Length, file.LastWriteTimeUtc.Ticks));
                }
            }
            return [.. result.OrderByDescending(file => file.Modified).ThenBy(file => file.Path, StringComparer.Ordinal).Take(64)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return []; }
    }

    private static bool NoLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static SafeFileHandle OpenLog(string path)
    {
        if (!OperatingSystem.IsMacOS()) return TranscriptTailer.Open(path);
        // Trae holds an advisory flock on its active log. Plain POSIX read-only open does not change that lock;
        // .NET's normal open adds its own flock and fails despite a read/write sharing policy.
        var descriptor = OpenDarwin(path, 0x100 | 0x1000000); // O_RDONLY | O_NOFOLLOW | O_CLOEXEC
        if (descriptor < 0) throw new IOException("Unable to read Trae log.");
        return new SafeFileHandle((IntPtr)descriptor, true);
    }

    [LibraryImport("/usr/lib/libSystem.B.dylib", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenDarwin(string path, int flags);

    public void Apply(string path, List<TraeRow> rows, IReadOnlyDictionary<string, TraeTurnEvidence> proofs, CancellationToken cancellationToken)
    {
        if (proofs.Count == 0) return;
        var graphs = Load(path, cancellationToken);
        var initialCount = rows.Count;
        for (var index = 0; index < initialCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = rows[index];
            if (row.Tokens.CacheWriteInput != 0 || !proofs.TryGetValue(row.Key, out var evidence) || evidence.Final.Tokens != row.Tokens) continue;
            rows[index] = Timed(row, evidence.Final, row.EndMs, true);
            foreach (var earlier in evidence.Earlier)
            {
                var prior = new TraeRow(row.SessionId, row.TurnId, row.Project, earlier.Config, earlier.CreatedMs, earlier.Tokens)
                { HistoryId = earlier.History };
                rows.Add(Timed(prior, earlier, row.EndMs, false));
            }
        }

        TraeRow Timed(TraeRow row, TraeTimingProof proof, long turnEnd, bool final)
        {
            if (row.Tokens.Output <= 0) return row;
            var intervals = graphs.Select(graph => graph.Interval(proof, turnEnd, final)).Where(value => value is not null).Distinct().ToArray();
            return intervals.Length == 1 && intervals[0] is { } interval ? row with { StartMs = interval.Start, TimingEndMs = interval.End } : row;
        }
    }

    private List<Graph> Load(string path, CancellationToken cancellationToken)
    {
        var files = Logs(path);
        var compressedTotal = 0L;
        var decodedTotal = 0L;
        var graphs = new List<Graph>();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Keep the newest evidence within the aggregate budgets; unsupported or older logs
            // must not erase timing already established by a valid recent file.
            if (file.Length is <= 0 or > TraeAlauda.MaxCompressed) continue;
            if (compressedTotal + file.Length > TraeAlauda.MaxCompressed) break;
            compressedTotal += file.Length;
            if (!_cache.TryGetValue(file.Path, out var cached) || cached.Stamp != file)
            {
                Graph? graph = null;
                var decoded = 0;
                try
                {
                    using var handle = OpenLog(file.Path);
                    if (RandomAccess.GetLength(handle) != file.Length) throw new IOException("Trae log changed.");
                    var data = new byte[(int)file.Length];
                    var offset = 0;
                    while (offset < data.Length)
                    {
                        var count = RandomAccess.Read(handle, data.AsSpan(offset), offset);
                        if (count == 0) throw new IOException("Trae log changed.");
                        offset += count;
                    }
                    var after = new FileInfo(file.Path);
                    if (!NoLinks(file.Path) || after.Length != file.Length || after.LastWriteTimeUtc.Ticks != file.Modified)
                        throw new IOException("Trae log changed.");
                    var records = TraeAlauda.Decode(data, out decoded);
                    if (decodedTotal + decoded > TraeAlauda.MaxDecoded) break;
                    graph = new Graph(records);
                }
                catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException or JsonException or RegexMatchTimeoutException) { }
                cached = (file, graph, decoded);
                _cache[file.Path] = cached;
            }
            if (decodedTotal + cached.Decoded > TraeAlauda.MaxDecoded) break;
            decodedTotal += cached.Decoded;
            retained.Add(file.Path);
            if (cached.Graph is { } found) graphs.Add(found);
        }
        foreach (var removed in _cache.Keys.Except(retained).ToArray()) _cache.Remove(removed);
        return graphs;
    }

    public List<TraeActivity> Activities(string path, IReadOnlyList<TraeTurn> turns, long nowMs)
    {
        var graphs = Load(path, CancellationToken.None);
        var result = new List<TraeActivity>();
        foreach (var group in turns.GroupBy(turn => turn.Session))
        {
            var newest = group.Max(turn => turn.StartMs ?? long.MaxValue);
            var latest = group.Where(turn => (turn.StartMs ?? long.MaxValue) == newest).ToArray();
            var turn = latest[0];
            var idle = new TraeActivity(turn.Session, turn.Turn, turn.Project, turn.Model, AgentPhase.Idle, null, turn.EndMs ?? turn.StartMs ?? 0);
            if (latest.Length != 1 || turn.StartMs is null || turn.Status != "in_progress" || turn.EndMs is not null) { result.Add(idle); continue; }
            var states = graphs.Select(graph => graph.Activity(turn, nowMs)).Where(state => state is not null).Distinct().ToArray();
            result.Add(states.Length == 1 && states[0] is { } state ? idle with
            { Phase = state.Phase, InFlightStartMs = state.Start, LastMs = state.Last, Model = state.Model ?? turn.Model } : idle);
        }
        return result;
    }

    public Dictionary<(string Session, string Turn), IReadOnlyList<ParsedResponse>> References(string path, IReadOnlyList<TraeTimingProof> proofs, long nowMs)
    {
        if (proofs.Count == 0) return [];
        var graphs = Load(path, CancellationToken.None);
        var responses = new List<(TraeTimingProof Proof, ParsedResponse Response)>();
        foreach (var proof in proofs)
        {
            var intervals = graphs.Select(graph => graph.Interval(proof, nowMs, false)).Where(value => value is not null).Distinct().ToArray();
            if (intervals.Length != 1 || intervals[0] is not { } interval || interval.End > nowMs
                || interval.End < nowMs - Engine.SpeedEngine.FallbackWindowMs) continue;
            var response = new ParsedResponse("trae:history:" + proof.Session + ":" + proof.History,
                SourceKind.Trae, path + "#" + proof.Session, proof.Config, interval.Start, interval.End, proof.Tokens);
            if (Engine.SpeedMath.TryGetSpeedInterval(response, out _)) responses.Add((proof, response));
        }
        return responses.GroupBy(item => (item.Proof.Session, item.Proof.Turn)).ToDictionary(group => group.Key,
            group => (IReadOnlyList<ParsedResponse>)group.Select(item => item.Response)
                .OrderByDescending(response => response.EndMs).ThenBy(response => response.Key, StringComparer.Ordinal)
                .Take(Engine.SpeedEngine.FallbackCount).ToArray());
    }

    private sealed record ActivityState(AgentPhase Phase, long? Start, long Last, string? Model);

    private sealed class Span(ulong id)
    {
        public ulong Id { get; } = id;
        public ulong? Parent;
        public string? Name;
        public long? Start, End;
        public Dictionary<string, string> Tags { get; } = new(StringComparer.Ordinal);
    }
    private sealed record Send(long? Time, string? Method, string? Path);
    private sealed record Timing(long? Time, string? Config, bool RetryFalse);
    private sealed class Detail
    {
        public List<Send> Sends { get; } = [];
        public List<long?> Done { get; } = [];
        public List<long?> Eof { get; } = [];
        public List<Timing> Timings { get; } = [];
        public List<(long? Time, string Trace)> Traces { get; } = [];
        public List<long?> UnsafeEvents { get; } = [];
    }

    private sealed class Graph
    {
        private readonly Dictionary<ulong, Span> _spans = [];
        private readonly Dictionary<ulong, Detail> _details = [];
        private readonly Dictionary<ulong, List<Span>> _children = [];

        public Graph(List<TraeAlauda.Record> records)
        {
            foreach (var record in records)
            {
                if (record.Sid is not { } id) continue;
                if (!_spans.TryGetValue(id, out var span)) _spans[id] = span = new Span(id);
                Merge(ref span.Name, record.Name); Merge(ref span.Parent, record.Parent);
                Merge(ref span.Start, record.Start); Merge(ref span.End, record.End);
                foreach (var pair in record.Tags)
                {
                    if (span.Tags.TryGetValue(pair.Key, out var old) && old != pair.Value) throw new InvalidDataException("Conflicting Trae span tags.");
                    span.Tags[pair.Key] = pair.Value;
                }
            }
            foreach (var span in _spans.Values)
            {
                if (Ancestor(span.Parent, Generation) is not { } owner) continue;
                if (!_children.TryGetValue(owner.Id, out var children)) _children[owner.Id] = children = [];
                children.Add(span);
            }
            foreach (var record in records)
            {
                if (record.Span is null || Ancestor(record.Span, Generation) is not { } owner) continue;
                if (!_details.TryGetValue(owner.Id, out var detail)) _details[owner.Id] = detail = new Detail();
                if (!string.IsNullOrEmpty(record.Trace)) detail.Traces.Add((record.Time, record.Trace));
                if (record.Unsafe) detail.UnsafeEvents.Add(record.Time);
                var message = record.Message;
                if (record.Target == HttpTarget && message?.StartsWith("[aha_net] send: calling Fetch id=", StringComparison.Ordinal) == true)
                {
                    var match = Regex.Match(message, @"method=([A-Z]+), url=([^, ]+)", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
                    var valid = match.Success && Uri.TryCreate(match.Groups[2].Value, UriKind.Absolute, out _);
                    detail.Sends.Add(new Send(record.Time, valid ? match.Groups[1].Value : null,
                        valid ? new Uri(match.Groups[2].Value).AbsolutePath : null));
                }
                if (record.Target == StreamTarget && message == "DONE") detail.Done.Add(record.Time);
                if (record.Target == StreamTarget && message == "EOF") detail.Eof.Add(record.Time);
                if (record.Target == "ai_agent::domain::plan::simple_service_v2" && message?.StartsWith(TimingPrefix, StringComparison.Ordinal) == true)
                {
                    var bytes = Encoding.UTF8.GetBytes(message[TimingPrefix.Length..]);
                    var reader = new Utf8JsonReader(bytes);
                    using var literal = JsonDocument.ParseValue(ref reader);
                    if (literal.RootElement.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid Trae timing data.");
                    using var data = JsonDocument.Parse(literal.RootElement.GetString()!);
                    var value = data.RootElement;
                    if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid Trae timing object.");
                    detail.Timings.Add(new Timing(record.Time,
                        value.TryGetProperty("config_name", out var model) && model.ValueKind == JsonValueKind.String ? model.GetString() : null,
                        value.TryGetProperty("is_retry", out var retry) && retry.ValueKind == JsonValueKind.False));
                }
            }
        }

        private static void Merge<T>(ref T? stored, T? next) where T : struct
        {
            if (next is null) return;
            if (stored is not null && !EqualityComparer<T?>.Default.Equals(stored, next)) throw new InvalidDataException("Conflicting Trae span.");
            stored = next;
        }
        private static void Merge(ref string? stored, string? next)
        {
            if (next is null) return;
            if (stored is not null && stored != next) throw new InvalidDataException("Conflicting Trae span name.");
            stored = next;
        }
        private Span? Ancestor(ulong? id, string name)
        {
            var seen = new HashSet<ulong>();
            while (id is { } value && seen.Count < 64 && seen.Add(value) && _spans.TryGetValue(value, out var span))
            {
                if (span.Name == name) return span;
                id = span.Parent;
            }
            return null;
        }

        public (long Start, long End)? Interval(TraeTimingProof proof, long chatEnd, bool finalRequest)
        {
            var origins = _spans.Values.Where(span => span.Name == "add_origin_history" && span.Tags.GetValueOrDefault("history_v2_id") == proof.History).ToArray();
            if (origins.Length != 1 || Ancestor(origins[0].Parent, Generation) is not { } call
                || Ancestor(call.Parent, "do_chat") is not { } root || !_details.TryGetValue(call.Id, out var detail)) return null;
            var creates = _spans.Values.Where(span => span.Name == "create_chat_turn_with_id" && span.Tags.GetValueOrDefault("turn_id") == proof.Turn
                && span.Tags.GetValueOrDefault("session_id") == proof.Session && Ancestor(span.Parent, "do_chat") == root).ToArray();
            if (creates.Length != 1 || detail.Sends.Count != 1 || detail.Done.Count != 1 || detail.Eof.Count != 1 || detail.Timings.Count != 1) return null;
            var send = detail.Sends[0];
            var timing = detail.Timings[0];
            if (!Recognized(send) || !timing.RetryFalse || timing.Config != proof.Config) return null;
            var children = _children.GetValueOrDefault(call.Id) ?? [];
            var usages = children.Where(span => span.Name == "end_slardar_event_with_metrics" && span.Tags.GetValueOrDefault("event_name") == "IcubeAiAgentTaskPlanToolTokenUsage").ToArray();
            var updates = children.Where(span => span.Name == "update_turn_context").ToArray();
            if (send.Time is not { } start || detail.Done[0] is not { } end
                || detail.Eof[0] is not { } eof || call.Start is not { } opened || call.End is not { } closed || timing.Time is not { } first
                || origins[0].Start is not { } origin) return null;
            if (detail.UnsafeEvents.Any(at => at is null || (at >= start && at <= end))
                || (!string.IsNullOrEmpty(proof.Trace) && detail.Traces.Any(trace => (trace.Time is null || (trace.Time >= start && trace.Time <= eof)) && trace.Trace != proof.Trace))) return null;
            if (!(opened <= start && start < end && end <= eof && eof <= closed && closed <= chatEnd && end - start <= 900_000
                && eof - end <= 1000 && (!finalRequest || chatEnd - end <= 60_000) && start <= first && first <= end
                && start <= origin && origin <= closed && Math.Abs(origin - end) <= 1000
                && updates.Any(span => ClosedWithin(span, start, Math.Min(closed, end + 1000)))
                && usages.Any(span => ClosedWithin(span, start, Math.Min(closed, end + 5000))))) return null;
            foreach (var other in _spans.Values)
            {
                if (other == call || other.Name != Generation || Ancestor(other.Parent, "do_chat") != root
                    || !_details.TryGetValue(other.Id, out var details) || details.Sends.Count == 0) continue;
                if (details.Sends.Count != 1 || details.Sends[0].Time is not { } otherStart) return null;
                if (!finalRequest && otherStart >= end) continue;
                if (details.Done.Count != 1 || details.Done[0] is not { } otherEnd || otherStart >= otherEnd
                    || (otherStart < end && otherEnd > start) || (finalRequest && otherStart > start)) return null;
            }
            return (start, end);
        }

        private static bool Recognized(Send send) => send.Method == "POST"
            && send.Path is "/api/agent/v3/create_agent_task" or "/api/agent/v3/commit_toolcall_result";
        private static bool ClosedWithin(Span span, long start, long end) =>
            span.Start is { } from && span.End is { } to && start <= from && from <= to && to <= end;

        public ActivityState? Activity(TraeTurn turn, long now)
        {
            var creates = _spans.Values.Where(span => span.Name == "create_chat_turn_with_id"
                && span.Tags.GetValueOrDefault("session_id") == turn.Session && span.Tags.GetValueOrDefault("turn_id") == turn.Turn).ToArray();
            if (creates.Length != 1 || Ancestor(creates[0].Parent, "do_chat") is not { } root) return null;
            ActivityState Idle(long last = 0) => new(AgentPhase.Idle, null, last > 0 ? last : turn.StartMs ?? 0, null);
            if (_spans.Values.Any(span => span.Name == "chat_turn_finish" && span.End is not null && Ancestor(span.Parent, "do_chat") == root)) return Idle();
            if (root.Tags.TryGetValue("session_id", out var session) && session != turn.Session) return Idle();
            var calls = _spans.Values.Where(span => span.Name == Generation && Ancestor(span.Parent, "do_chat") == root && _details.ContainsKey(span.Id))
                .Select(span => (Span: span, Detail: _details[span.Id])).Where(call => call.Detail.Sends.Count > 0).ToArray();
            var sends = calls.SelectMany(call => call.Detail.Sends.Select(send => (call.Span, call.Detail, Send: send))).ToArray();
            if (sends.Length == 0 || sends.Any(item => item.Send.Time is null)) return Idle();
            var latestAt = sends.Max(item => item.Send.Time!.Value);
            var latest = sends.Where(item => item.Send.Time == latestAt).ToArray();
            if (latest.Length != 1) return Idle();
            var current = latest[0];
            var detail = current.Detail;
            var start = latestAt;
            if (!Recognized(current.Send) || detail.Sends.Count != 1 || current.Span.Start is not { } opened
                || opened > start || start < turn.StartMs || start > now || detail.Timings.Count > 1) return Idle();
            foreach (var other in calls)
            {
                if (other.Span == current.Span) continue;
                if (other.Detail.Sends.Count != 1 || other.Detail.Sends[0].Time is not { } began) return Idle();
                var resolved = other.Detail.Done.Count == 1 ? other.Detail.Done[0] : other.Span.End;
                if (resolved is not { } done || done < began || done > start) return Idle();
            }
            var last = start;
            string? model = null;
            if (detail.Timings.Count == 1)
            {
                var timing = detail.Timings[0];
                if (!timing.RetryFalse || timing.Time is not { } at || at < start || at > now) return Idle();
                last = at; model = timing.Config;
            }
            if (detail.Done.Count == 0 && detail.Eof.Count == 0 && current.Span.End is null)
            {
                if (BadBetween(detail, turn.Trace, start, now) || now - last > Engine.SpeedEngine.StaleMs) return Idle(last);
                return new ActivityState(AgentPhase.Generating, start, last, model);
            }
            if (detail.Done.Count == 1 && detail.Eof.Count == 1 && detail.Done[0] is { } end && detail.Eof[0] is { } eof
                && start < end && end <= eof && eof <= now && eof - end <= 1000 && now - eof <= Engine.SpeedEngine.StaleMs
                && end - start <= Engine.SpeedMath.MaxIntervalMs && (current.Span.End is null || current.Span.End >= eof)
                && !BadBetween(detail, turn.Trace, start, end, eof)) return new ActivityState(AgentPhase.Tools, null, eof, model);
            return Idle();
        }

        private static bool BadBetween(Detail detail, string? trace, long start, long end, long? traceEnd = null) =>
            detail.UnsafeEvents.Any(at => at is null || (at >= start && at <= end))
            || (!string.IsNullOrEmpty(trace) && detail.Traces.Any(item => (item.Time is null || (item.Time >= start && item.Time <= (traceEnd ?? end))) && item.Trace != trace));
    }
}
