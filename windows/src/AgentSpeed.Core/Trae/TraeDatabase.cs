using System.Text;
using System.Text.Json;
using AgentSpeed.Core.IO.Sqlite;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Harnesses.HarnessJson;

namespace AgentSpeed.Core.Trae;

internal sealed record TraeTurn(string Session, string Turn, string? Project, string? Model, string Status, string? Trace, long? StartMs, long? EndMs);
internal sealed record TraeActivity(string Session, string Turn, string? Project, string? Model, AgentPhase Phase, long? InFlightStartMs, long LastMs)
{
    public IReadOnlyList<ParsedResponse> ReferenceResponses { get; init; } = [];
}

internal sealed record TraeRow(string SessionId, string TurnId, string? Project, string? Model, long EndMs, TokenCounts Tokens)
{
    public string Key => HistoryId is { } history ? "trae:history:" + SessionId + ":" + history : "trae:" + SessionId + ":" + TurnId;
    public string? HistoryId { get; init; }
    public long? StartMs { get; init; }
    public long? TimingEndMs { get; init; }
    public string AgentId(string path) => path + "#" + SessionId;
    // Usage comes from the final fallback or exact per-request metadata; timing also needs native transport evidence.
    public ParsedResponse Response(string path) => new(Key,
        SourceKind.Trae, AgentId(path), Model, StartMs, TimingEndMs ?? EndMs, Tokens);
}

internal sealed class TraeDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _path;
    private TraeDatabase(SqliteConnection connection, string path) { _connection = connection; _path = path; }

    public static TraeDatabase Open(string path)
    {
        var snapshot = TraeSnapshot.Read(path);
        var connection = SqliteConnection.FromSnapshot(snapshot);
        try
        {
            using var schema = connection.Prepare("PRAGMA table_info(chat_turn)");
            var columns = new HashSet<string>(StringComparer.Ordinal);
            while (schema.Step()) if (schema.GetString(1) is { } name) columns.Add(name);
            if (!new[] { "id", "session_id", "turn_id", "turn_status", "context", "deleted_at" }.All(columns.Contains))
                throw new InvalidDataException("Unsupported Trae chat_turn schema.");
            return new TraeDatabase(connection, path);
        }
        catch { connection.Dispose(); throw; }
    }

    public List<TraeRow> Read(long sinceMs = 0, CancellationToken cancellationToken = default, TraeTiming? timing = null)
    {
        var result = new List<TraeRow>();
        using var rows = _connection.Prepare("""
            SELECT id, session_id, turn_id,
                   CASE WHEN length(CAST(context AS BLOB))<=1048576 AND json_valid(context)
                        THEN json_array(json_extract(context,'$.token_usage'), json_extract(context,'$.chat_end_time'),
                            json_object('display_model_name',json_extract(context,'$.persist_user_message_context.model_info.display_model_name'),
                                'model_name',json_extract(context,'$.persist_user_message_context.model_info.model_name'),
                                'config_name',json_extract(context,'$.persist_user_message_context.model_info.config_name')),
                            json_extract(context,'$.workspace_folders[0]')) END
            FROM chat_turn WHERE turn_status='completed' AND coalesce(deleted_at,0)=0 ORDER BY id
            """);
        while (rows.Step())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Nonempty(rows.GetString(1)) is not { } session || rows.GetUtf8(3).IsEmpty) continue;
            try
            {
                var reader = new Utf8JsonReader(rows.GetUtf8(3), JsonScan.Options);
                using var document = JsonDocument.ParseValue(ref reader);
                var fields = document.RootElement;
                if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() != 4) continue;
                var usage = fields[0];
                if (!Integer(fields[1], 253_402_300_799_999, out var end) || end <= 0 || end < sinceMs) continue;
                if (!Counter(usage, "prompt_tokens", false, TokenCounts.MaxValue, out var input)
                    || !Counter(usage, "completion_tokens", false, TokenCounts.MaxValue, out var output)
                    || !Counter(usage, "cache_read_input_tokens", true, TokenCounts.MaxValue, out var cacheRead)
                    || !Counter(usage, "cache_creation_input_tokens", true, TokenCounts.MaxValue, out var cacheWrite)
                    || !Counter(usage, "reasoning_tokens", true, TokenCounts.MaxValue, out var reasoning)
                    || cacheRead + cacheWrite > input || reasoning > output || input + output == 0) continue;
                var total = Get(usage, "total_tokens");
                if (total.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null
                    && (!Counter(usage, "total_tokens", false, 2 * TokenCounts.MaxValue, out var sum) || sum != input + output)) continue;
                var info = fields[2];
                var model = Nonempty(String(info, "display_model_name")) ?? Nonempty(String(info, "model_name")) ?? Nonempty(String(info, "config_name"));
                var project = Nonempty(Text(fields[3]));
                result.Add(new TraeRow(session, Nonempty(rows.GetString(2)) ?? rows.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    project is null ? null : PathText.Basename(project), model, end,
                    new TokenCounts(input, cacheRead, cacheWrite, output, reasoning)));
            }
            catch (JsonException) { }
        }
        (timing ?? new TraeTiming()).Apply(_path, result, ReadTimingProofs(cancellationToken), cancellationToken);
        return result;
    }

    private Dictionary<string, TraeTurnEvidence> ReadTimingProofs(CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<(string? History, string? ServerId, string? Source, long Created, TraeTimingProof? Proof, string? Run, bool Expand)>>(StringComparer.Ordinal);
        var histories = new Dictionary<(string Session, string History), int>();
        var servers = new Dictionary<(string Session, string Server), int>();
        try
        {
            var hasRuns = HasColumn("server_history_info", "agent_run_id") && HasColumn("history_v2", "agent_run_id");
            var hasServerId = HasColumn("server_history_info", "history_id");
            // Select only counters and identities. Do not materialize history messages or provider credentials.
            // LEFT JOIN retains newer client rows without server metadata: they must block stale final proofs.
            using var rows = _connection.Prepare("""
                SELECT t.session_id,t.turn_id,t.id,json_extract(t.context,'$.trace_id'),v.history_v2_id,v.created_at,
                  CASE WHEN length(CAST(h.extra_info AS BLOB))<=65536 AND json_valid(h.extra_info) THEN json_extract(h.extra_info,
                    '$.exact_usage_v1','$.exact_token_semantics_v1','$.exact_prompt_tokens_v1','$.exact_output_tokens_v1',
                    '$.exact_total_tokens_v1','$.exact_reasoning_tokens_v1','$.exact_cache_read_input_tokens_v1','$.config_name') END,
                    v.content_source,h.created_at,typeof(v.created_at),typeof(h.created_at)
                """ + (hasRuns ? ",h.agent_run_id,v.agent_run_id" : ",NULL,NULL") + """
                ,json_extract(t.context,'$.chat_start_time','$.chat_end_time')
                """ + (hasServerId ? ",h.history_id " : ",NULL ") + """
                FROM chat_turn t JOIN history_v2 v ON v.session_id=t.session_id AND v.message_id=t.response_message_id
                LEFT JOIN server_history_info h ON h.client_history_id=v.history_v2_id
                  AND h.source='llm_default' AND h.history_source='server' AND coalesce(h.is_deleted,0)=0
                WHERE t.turn_status='completed' AND coalesce(t.deleted_at,0)=0 AND json_valid(t.context)
                  AND coalesce(v.deleted_at,0)=0
                """);
            while (rows.Step())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Nonempty(rows.GetString(0)) is not { } session) continue;
                var turn = Nonempty(rows.GetString(1)) ?? rows.GetInt64(2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var key = "trae:" + session + ":" + turn;
                var created = rows.GetString(9) == "integer" && rows.GetInt64(5) is > 0 and <= 253_402_300_799
                    ? rows.GetInt64(5) : long.MaxValue;
                var serverCreated = rows.GetString(10) == "integer" && rows.GetInt64(8) is > 0 and <= 253_402_300_799
                    ? rows.GetInt64(8) * 1000 : (long?)null;
                var history = Nonempty(rows.GetString(4));
                var server = Nonempty(rows.GetString(14));
                if (history is not null) histories[(session, history)] = histories.GetValueOrDefault((session, history)) + 1;
                if (server is not null) servers[(session, server)] = servers.GetValueOrDefault((session, server)) + 1;
                TraeTimingProof? proof = null;
                if (history is not null && serverCreated is { } ended && !rows.GetUtf8(6).IsEmpty)
                {
                    try
                    {
                        using var data = JsonDocument.Parse(rows.GetUtf8(6).ToArray());
                        var values = data.RootElement;
                        if (values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 8
                            && Text(values[0]) == "chat_completion" && Text(values[1]) == "provider_raw_usage_completion_includes_reasoning"
                            && Decimal(values[2], TokenCounts.MaxValue, out var input) && Decimal(values[3], TokenCounts.MaxValue, out var output)
                            && Decimal(values[4], 2 * TokenCounts.MaxValue, out var total) && Decimal(values[5], TokenCounts.MaxValue, out var reasoning)
                            && Decimal(values[6], TokenCounts.MaxValue, out var cached) && total == input + output && total > 0 && reasoning <= output && cached <= input
                            && Nonempty(Text(values[7])) is { } config)
                            proof = new TraeTimingProof(session, turn, history, config, Nonempty(rows.GetString(3)), new TokenCounts(input, cached, 0, output, reasoning), ended);
                    }
                    catch (JsonException) { }
                }
                if (!groups.TryGetValue(key, out var group)) groups[key] = group = [];
                var run = Nonempty(rows.GetString(11));
                var expand = server is not null && run is not null && run == Nonempty(rows.GetString(12)) && created is > 0 and < long.MaxValue && serverCreated is not null;
                try
                {
                    using var times = JsonDocument.Parse(rows.GetUtf8(13).ToArray());
                    var bounds = times.RootElement;
                    expand &= bounds.ValueKind == JsonValueKind.Array && bounds.GetArrayLength() == 2
                        && Integer(bounds[0], 253_402_300_799_999, out var start) && start > 0
                        && Integer(bounds[1], 253_402_300_799_999, out var end) && end >= start
                        && created >= start / 1000 && created <= (end + 999) / 1000
                        && serverCreated / 1000 >= start / 1000 && serverCreated / 1000 <= (end + 999) / 1000;
                }
                catch (JsonException) { expand = false; }
                group.Add((history, server, rows.GetString(7), created, proof, run, expand));
            }
        }
        catch (SqliteException) { return []; } // Optional metadata schema is absent in older/exported stores.
        var result = new Dictionary<string, TraeTurnEvidence>(StringComparer.Ordinal);
        foreach (var (key, candidates) in groups)
        {
            var finals = candidates.Where(candidate => candidate.Source == "llm_default").ToArray();
            if (finals.Length == 0) continue;
            var latest = finals.Max(candidate => candidate.Created);
            var final = finals.Where(candidate => candidate.Created == latest).ToArray();
            if (latest == long.MaxValue || final.Length != 1 || final[0].Proof is not { } proof || final[0].ServerId is not { } finalServer
                || candidates.Count(candidate => candidate.History == proof.History) != 1
                || candidates.Count(candidate => candidate.ServerId == finalServer) != 1) continue;
            var uniqueAnchor = histories.GetValueOrDefault((proof.Session, proof.History)) == 1
                && final[0].ServerId is { } anchorServer && servers.GetValueOrDefault((proof.Session, anchorServer)) == 1;
            var prior = candidates.GroupBy(candidate => candidate.History, StringComparer.Ordinal)
                .Where(group => group.Key is not null && group.Key != proof.History && group.Count() == 1)
                .Select(group => group.Single()).Where(candidate => uniqueAnchor && final[0].Expand && candidate.Expand && candidate.Run == final[0].Run
                    && candidate.Created <= latest && candidate.Proof is { } earlier && earlier.CreatedMs <= proof.CreatedMs
                    && histories.GetValueOrDefault((earlier.Session, earlier.History)) == 1
                    && candidate.ServerId is { } serverId && servers.GetValueOrDefault((earlier.Session, serverId)) == 1)
                .Select(candidate => candidate.Proof!).ToArray();
            result[key] = new TraeTurnEvidence(proof, prior);
        }
        return result;
    }

    private bool HasColumn(string table, string column)
    {
        using var schema = _connection.Prepare("PRAGMA table_info(" + table + ")");
        while (schema.Step()) if (schema.GetString(1) == column) return true;
        return false;
    }

    public List<TraeActivity> ReadActivities(TraeTiming timing, long nowMs)
    {
        var turns = new List<TraeTurn>();
        var unsafeTurns = new List<(string Session, long? CreatedUpperMs)>();
        using var rows = _connection.Prepare("""
            SELECT id,session_id,turn_id,turn_status,
              CASE WHEN length(CAST(context AS BLOB))<=1048576 AND json_valid(context) THEN json_extract(context,
                '$.chat_start_time','$.chat_end_time','$.trace_id','$.workspace_folders[0]',
                '$.persist_user_message_context.model_info.display_model_name',
                '$.persist_user_message_context.model_info.model_name','$.persist_user_message_context.model_info.config_name') END,
              created_at,typeof(created_at)
            FROM chat_turn WHERE coalesce(deleted_at,0)=0
            """);
        while (rows.Step())
        {
            if (Nonempty(rows.GetString(1)) is not { } session) continue;
            var created = rows.GetInt64(5);
            long? createdUpperMs = rows.GetString(6) == "integer" && created is > 0 and < 253_402_300_799 ? (created + 1) * 1000 : null;
            if (Nonempty(rows.GetString(3)) is not { } status || rows.GetUtf8(4).IsEmpty) { unsafeTurns.Add((session, createdUpperMs)); continue; }
            try
            {
                using var document = JsonDocument.Parse(rows.GetUtf8(4).ToArray());
                var values = document.RootElement;
                if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != 7) { unsafeTurns.Add((session, createdUpperMs)); continue; }
                var start = Integer(values[0], 253_402_300_799_999, out var started) && started > 0 ? started : (long?)null;
                if (start is null || (values[1].ValueKind is not JsonValueKind.Null && !Integer(values[1], 253_402_300_799_999, out _))) { unsafeTurns.Add((session, createdUpperMs)); continue; }
                var end = Integer(values[1], 253_402_300_799_999, out var ended) && ended > 0 ? ended : (long?)null;
                var project = Nonempty(Text(values[3]));
                turns.Add(new TraeTurn(session, Nonempty(rows.GetString(2)) ?? rows.GetInt64(0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    project is null ? null : PathText.Basename(project), Nonempty(Text(values[4])) ?? Nonempty(Text(values[5])) ?? Nonempty(Text(values[6])),
                    status, Nonempty(Text(values[2])), start, end));
            }
            catch (JsonException) { unsafeTurns.Add((session, createdUpperMs)); }
        }
        var starts = turns.GroupBy(turn => turn.Session).ToDictionary(group => group.Key, group => group.Max(turn => turn.StartMs));
        var unsafeSessions = unsafeTurns.Where(turn => turn.CreatedUpperMs is null
            || !starts.TryGetValue(turn.Session, out var start) || start < turn.CreatedUpperMs).Select(turn => turn.Session).ToHashSet(StringComparer.Ordinal);
        turns.RemoveAll(turn => unsafeSessions.Contains(turn.Session));
        var activities = timing.Activities(_path, turns, nowMs);
        var active = activities.Where(activity => activity.Phase != AgentPhase.Idle).Select(activity => (activity.Session, activity.Turn)).ToHashSet();
        if (active.Count == 0) return activities;
        var references = timing.References(_path, ReadLiveProofs(turns.Where(turn => active.Contains((turn.Session, turn.Turn))).ToArray(), nowMs), nowMs);
        return activities.Select(activity => activity with
        { ReferenceResponses = references.GetValueOrDefault((activity.Session, activity.Turn)) ?? [] }).ToList();
    }

    // Unlike accounting expansion, a speed reference does not need a final-turn usage anchor. Every candidate
    // still needs exact native counters, unique identities, matching run ids and a completed transport interval.
    private List<TraeTimingProof> ReadLiveProofs(IReadOnlyList<TraeTurn> active, long nowMs)
    {
        var candidates = new List<(string Session, string? History, string? Server, TraeTimingProof? Proof)>();
        var turns = active.Where(turn => turn.Status == "in_progress" && turn.EndMs is null && turn.StartMs <= nowMs)
            .GroupBy(turn => (turn.Session, turn.Turn)).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());
        if (turns.Count == 0) return [];
        try
        {
            if (!HasColumn("history_v2", "agent_run_id") || !HasColumn("server_history_info", "agent_run_id")) return [];
            // Include all joined identities when checking uniqueness, even if a row is not a valid reference.
            using var rows = _connection.Prepare("""
                SELECT t.session_id,t.turn_id,t.id,v.history_v2_id,h.history_id,v.created_at,h.created_at,
                    typeof(v.created_at),typeof(h.created_at),v.agent_run_id,h.agent_run_id,
                    CASE WHEN length(CAST(h.extra_info AS BLOB))<=1048576 AND json_valid(h.extra_info) THEN json_extract(h.extra_info,
                      '$.exact_usage_v1','$.exact_token_semantics_v1','$.exact_prompt_tokens_v1','$.exact_output_tokens_v1',
                      '$.exact_total_tokens_v1','$.exact_reasoning_tokens_v1','$.exact_cache_read_input_tokens_v1','$.config_name') END
                FROM chat_turn t JOIN history_v2 v ON v.session_id=t.session_id AND v.message_id=t.response_message_id
                LEFT JOIN server_history_info h ON h.client_history_id=v.history_v2_id
                    AND h.source='llm_default' AND h.history_source='server' AND coalesce(h.is_deleted,0)=0
                WHERE coalesce(t.deleted_at,0)=0 AND coalesce(v.deleted_at,0)=0
                """);
            while (rows.Step())
            {
                if (Nonempty(rows.GetString(0)) is not { } session) continue;
                var turnId = Nonempty(rows.GetString(1)) ?? rows.GetInt64(2).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var history = Nonempty(rows.GetString(3));
                var server = Nonempty(rows.GetString(4));
                TraeTimingProof? proof = null;
                if (turns.TryGetValue((session, turnId), out var turn) && turn.StartMs is { } start
                    && history is not null && server is not null && Nonempty(rows.GetString(9)) is { } run && run == Nonempty(rows.GetString(10))
                    && rows.GetString(7) == "integer" && rows.GetString(8) == "integer"
                    && rows.GetInt64(5) >= start / 1000 && rows.GetInt64(5) <= (nowMs + 999) / 1000
                    && rows.GetInt64(6) >= start / 1000 && rows.GetInt64(6) <= (nowMs + 999) / 1000
                    && rows.GetInt64(5) > 0 && rows.GetInt64(6) > 0 && !rows.GetUtf8(11).IsEmpty)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(rows.GetUtf8(11).ToArray());
                        var values = document.RootElement;
                        if (values.ValueKind == JsonValueKind.Array && values.GetArrayLength() == 8
                            && Text(values[0]) == "chat_completion" && Text(values[1]) == "provider_raw_usage_completion_includes_reasoning"
                            && Decimal(values[2], TokenCounts.MaxValue, out var input) && Decimal(values[3], TokenCounts.MaxValue, out var output)
                            && Decimal(values[4], 2 * TokenCounts.MaxValue, out var total) && Decimal(values[5], TokenCounts.MaxValue, out var reasoning)
                            && Decimal(values[6], TokenCounts.MaxValue, out var cached) && total == input + output && output > 0
                            && reasoning <= output && cached <= input && Nonempty(Text(values[7])) is { } config)
                            proof = new TraeTimingProof(session, turnId, history, config, turn.Trace,
                                new TokenCounts(input, cached, 0, output, reasoning), rows.GetInt64(6) * 1000);
                    }
                    catch (JsonException) { }
                }
                candidates.Add((session, history, server, proof));
            }
        }
        catch (SqliteException) { return []; }
        var clients = candidates.Where(candidate => candidate.History is not null).GroupBy(candidate => (candidate.Session, candidate.History))
            .ToDictionary(group => group.Key, group => group.Count());
        var servers = candidates.Where(candidate => candidate.Server is not null).GroupBy(candidate => (candidate.Session, candidate.Server))
            .ToDictionary(group => group.Key, group => group.Count());
        return candidates.Where(candidate => candidate.Proof is not null && candidate.Server is not null
            && clients.GetValueOrDefault((candidate.Session, candidate.History)) == 1
            && servers.GetValueOrDefault((candidate.Session, candidate.Server)) == 1).Select(candidate => candidate.Proof!).ToList();
    }

    private static bool Decimal(JsonElement value, long maximum, out long result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: >= 1 and <= 13 } text
            && text.All(char.IsAsciiDigit) && long.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out result) && result <= maximum;
    }

    private static bool Counter(JsonElement usage, string name, bool optional, long maximum, out long result)
    {
        result = 0;
        var value = Get(usage, name);
        if (optional && value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        return Integer(value, maximum, out result);
    }

    private static bool Integer(JsonElement value, long maximum, out long result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number < 0 || number > maximum || number != Math.Truncate(number)) return false;
        result = (long)number;
        return true;
    }

    private static string? Nonempty(string? text) => !string.IsNullOrWhiteSpace(text) && Encoding.UTF8.GetByteCount(text) <= 4096 ? text : null;

    public void Dispose() => _connection.Dispose();
}
