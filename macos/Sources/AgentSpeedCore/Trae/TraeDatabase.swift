import Foundation

/// Trae persists only the final model call's usage in each completed turn. Whole-turn clocks include tools and must
/// never be treated as model duration. Timing is available only when optional exact metadata and native logs corroborate
/// the same request. Reconciled optional per-call metadata expands earlier usage; history remains partial.
final class TraeDatabase {
    static let analysisRevision = 2
    struct Row: Sendable {
        var record: ResponseRecord
        var sessionId: String
        var project: String?
        var cwd: String?
    }
    let path: String
    private let connection: SQLiteConnection

    init(path: String) throws {
        self.path = path
        connection = try SQLiteConnection.snapshot(TraeSnapshot.read(path: path))
        _ = try connection.prepare("SELECT id, session_id, turn_id, turn_status, context, deleted_at FROM chat_turn LIMIT 0")
    }

    static func databasePath(forRoot root: String) -> String {
        if root.hasSuffix(".db") || FileStat.lstat(root)?.isRegularFile == true { return root }
        for name in ["database.db", "ai-agent/database.db", "ModularData/ai-agent/database.db", "ai-chat/database.db", "ModularData/ai-chat/database.db"] {
            let path = (root as NSString).appendingPathComponent(name)
            if FileStat.lstat(path)?.isRegularFile == true { return path }
        }
        return (root as NSString).appendingPathComponent("ModularData/ai-agent/database.db")
    }

    func read(sinceMs: Int64? = nil) throws -> [Row] {
        let query = try connection.prepare("""
            SELECT id, session_id, turn_id,
                   CASE WHEN length(CAST(context AS BLOB)) <= 1048576 AND json_valid(context)
                        THEN json_extract(context, '$.token_usage', '$.chat_end_time',
                            '$.persist_user_message_context.model_info.display_model_name',
                            '$.persist_user_message_context.model_info.model_name',
                            '$.persist_user_message_context.model_info.config_name', '$.workspace_folders[0]') END
            FROM chat_turn WHERE turn_status = 'completed' AND coalesce(deleted_at,0) = 0 ORDER BY id
            """)
        var rows: [Row] = []
        try query.forEachRow { row in
            guard let session = Self.nonempty(row.string(1)),
                  let fields = row.withText(3, { try? JSONParser.parse(Data($0)) })?.array, fields.count == 6,
                  let end = Self.integer(fields[1], maximum: 253_402_300_799_999), DayLabeler.hasDay(Int64(end)), end > 0,
                  sinceMs.map({ Int64(end) >= $0 }) ?? true else { return }
            let usage = fields[0]
            guard usage.object != nil,
                  let input = Self.integer(usage["prompt_tokens"]), let output = Self.integer(usage["completion_tokens"]),
                  let cached = Self.optionalCount(usage["cache_read_input_tokens"]),
                  let created = Self.optionalCount(usage["cache_creation_input_tokens"]),
                  let reasoning = Self.optionalCount(usage["reasoning_tokens"]),
                  cached + created <= input, reasoning <= output,
                  input > 0 || output > 0 else { return }
            if let total = usage["total_tokens"], total != .null {
                guard Self.integer(total, maximum: 2_000_000_000_000) == input + output else { return }
            }
            let turn = Self.nonempty(row.string(2)) ?? String(row.int64(0))
            // Native model_info may also contain credentials. Only the three name scalars reach this decoder.
            let model = Self.nonempty(fields[2].string) ?? Self.nonempty(fields[3].string) ?? Self.nonempty(fields[4].string)
            let cwd = Self.nonempty(fields[5].string)
            let project = cwd.map { ($0 as NSString).lastPathComponent }
            let record = ResponseRecord(key: "trae:" + session + ":" + turn, source: .trae, agentId: agentId(session),
                model: model, startMs: nil, endMs: Int64(end),
                usage: TokenUsage(input: input, cachedInput: cached, cacheWriteInput: created, output: output, reasoning: reasoning))
            rows.append(Row(record: record, sessionId: session, project: project, cwd: cwd))
        }
        let proofs = exactHistories()
        if !proofs.isEmpty {
            let graphs = TraeTiming.graphs(database: path)
            var earlier: [Row] = []
            for index in rows.indices {
                let record = rows[index].record
                guard record.usage.cacheWriteInput == 0,
                      let history = proofs[record.key], history.final.proof.usage == record.usage else { continue }
                if record.usage.output > 0,
                   let interval = TraeTiming.interval(proof: history.final.proof, end: record.endMs, graphs: graphs) {
                    rows[index].record.startMs = interval.start
                    rows[index].record.endMs = interval.end
                }
                // Counts depend on exact database metadata, never on log retention or timing eligibility.
                for item in history.earlier {
                    let proof = item.proof
                    let interval = proof.usage.output > 0
                        ? TraeTiming.interval(proof: proof, end: record.endMs, graphs: graphs, final: false) : nil
                    let expanded = ResponseRecord(key: "trae:history:" + proof.session + ":" + proof.history,
                        source: .trae, agentId: record.agentId, model: proof.config,
                        startMs: interval?.start, endMs: interval?.end ?? item.serverCreated * 1000, usage: proof.usage)
                    earlier.append(Row(record: expanded, sessionId: rows[index].sessionId,
                                       project: rows[index].project, cwd: rows[index].cwd))
                }
            }
            rows += earlier
        }
        return rows
    }

    /// A turn's declared state and identity are independent of whether exact completed usage is available.
    /// Only a unique latest turn with a valid start may drive an agent's live phase.
    func activities(nowMs: Int64) throws -> [TraeTiming.Activity] {
        let query = try connection.prepare("""
            SELECT session_id, turn_id, id, turn_status,
                CASE WHEN length(CAST(context AS BLOB)) <= 1048576 AND json_valid(context)
                    THEN json_extract(context, '$.chat_start_time', '$.chat_end_time', '$.trace_id',
                        '$.persist_user_message_context.model_info.display_model_name',
                        '$.persist_user_message_context.model_info.model_name',
                        '$.persist_user_message_context.model_info.config_name', '$.workspace_folders[0]') END, created_at
            FROM chat_turn WHERE coalesce(deleted_at,0)=0
            """)
        var groups: [String: [TraeTiming.Turn]] = [:]
        var malformed: [String: [Int64?]] = [:]
        try query.forEachRow { row in
            guard let session = Self.nonempty(row.string(0)) else { return }
            guard let fields = row.withText(4, { try? JSONParser.parse(Data($0)) })?.array, fields.count == 7,
                  let start = Self.integer(fields[0], maximum: 253_402_300_799_999), start > 0,
                  DayLabeler.hasDay(Int64(start)), let status = Self.nonempty(row.string(3)) else {
                malformed[session, default: []].append(Self.validSeconds(row.optionalInt64(5)))
                return
            }
            let end = Self.integer(fields[1], maximum: 253_402_300_799_999)
            let invalidEnd = fields[1] != .null && end == nil
            if invalidEnd { malformed[session, default: []].append(Self.validSeconds(row.optionalInt64(5))) }
            let model = Self.nonempty(fields[3].string) ?? Self.nonempty(fields[4].string) ?? Self.nonempty(fields[5].string)
            groups[session, default: []].append(TraeTiming.Turn(session: session,
                turn: Self.nonempty(row.string(1)) ?? String(row.int64(2)),
                status: invalidEnd ? "unknown" : status, trace: Self.nonempty(fields[2].string), model: model,
                cwd: Self.nonempty(fields[6].string), start: Int64(start), end: end.map(Int64.init)))
        }
        let unsafeSessions = Set(malformed.compactMap { session, createdTimes -> String? in
            guard let latest = groups[session]?.map(\.start).max() else { return session }
            // A malformed ancient row must not disable all future turns. Same-second/undated rows remain ambiguous.
            return createdTimes.contains { $0.map { ($0 + 1) * 1000 > latest } ?? true } ? session : nil
        })
        var latest: [TraeTiming.Turn] = []
        var idle: [TraeTiming.Activity] = []
        for session in unsafeSessions {
            let known = groups[session]?.max { $0.start < $1.start }
            idle.append(TraeTiming.Activity(session: session, turn: known?.turn ?? "", model: known?.model,
                cwd: known?.cwd, lastLine: min(nowMs, known?.start ?? 0)))
        }
        for (session, turns) in groups where !unsafeSessions.contains(session) {
            guard let start = turns.map(\.start).max() else { continue }
            let matches = turns.filter { $0.start == start }
            if matches.count == 1 { latest.append(matches[0]) }
            else { idle.append(TraeTiming.Activity(session: session, turn: "", model: nil, cwd: nil, lastLine: min(nowMs, start))) }
        }
        let graphs = latest.contains(where: { $0.status == "in_progress" && ($0.end ?? 0) == 0 })
            ? TraeTiming.graphs(database: path) : []
        let references = activeReferences(turns: latest, nowMs: nowMs, graphs: graphs)
        return idle + latest.map { turn in
            var activity = TraeTiming.activity(turn: turn, nowMs: nowMs, graphs: graphs)
            if activity.phase != .idle { activity.referenceResponses = references[turn.session] ?? [] }
            return activity
        }
    }

    /// Exact, completed requests can arrive before the enclosing turn finishes. These records are private
    /// reference samples only: `read()` remains the authority for counted usage and stable completion identities.
    private func activeReferences(turns: [TraeTiming.Turn], nowMs: Int64, graphs: [TraeTiming.Graph]) -> [String: [ResponseRecord]] {
        let active = Dictionary(uniqueKeysWithValues: turns.filter {
            $0.status == "in_progress" && ($0.end ?? 0) == 0 && $0.start <= nowMs
        }.map { ($0.session, $0) })
        guard !active.isEmpty, !graphs.isEmpty,
              hasColumn(table: "history_v2", column: "agent_run_id"),
              hasColumn(table: "server_history_info", column: "agent_run_id"),
              let query = try? connection.prepare("""
                SELECT t.session_id, t.turn_id, t.id, v.history_v2_id, h.history_id,
                    v.created_at, h.created_at, v.agent_run_id, h.agent_run_id,
                    CASE WHEN length(CAST(h.extra_info AS BLOB)) <= 1048576 AND json_valid(h.extra_info)
                        THEN json_extract(h.extra_info, '$.exact_usage_v1', '$.exact_token_semantics_v1',
                            '$.exact_prompt_tokens_v1', '$.exact_output_tokens_v1', '$.exact_total_tokens_v1',
                            '$.exact_reasoning_tokens_v1', '$.exact_cache_read_input_tokens_v1', '$.config_name') END
                FROM chat_turn t
                JOIN history_v2 v ON v.session_id=t.session_id AND v.message_id=t.response_message_id
                LEFT JOIN server_history_info h ON h.client_history_id=v.history_v2_id
                    AND h.source='llm_default' AND h.history_source='server' AND coalesce(h.is_deleted,0)=0
                WHERE coalesce(t.deleted_at,0)=0 AND coalesce(v.deleted_at,0)=0
                """) else { return [:] }
        struct Identity: Hashable { var session: String; var id: String }
        struct Candidate { var proof: TraeTiming.Proof; var server: String }
        var clients: [Identity: Int] = [:], servers: [Identity: Int] = [:], candidates: [Candidate] = []
        do {
            try query.forEachRow { row in
                guard let session = Self.nonempty(row.string(0)) else { return }
                let client = Self.nonempty(row.string(3)), server = Self.nonempty(row.string(4))
                // Include non-current turns in uniqueness checks: copied/reused identities are ambiguous.
                if let client { clients[Identity(session: session, id: client), default: 0] += 1 }
                if let server { servers[Identity(session: session, id: server), default: 0] += 1 }
                guard let turn = active[session],
                      (Self.nonempty(row.string(1)) ?? String(row.int64(2))) == turn.turn,
                      let client, let server,
                      let created = Self.validSeconds(row.optionalInt64(5)),
                      let serverCreated = Self.validSeconds(row.optionalInt64(6)),
                      (turn.start / 1000...(nowMs + 999) / 1000).contains(created),
                      (turn.start / 1000...(nowMs + 999) / 1000).contains(serverCreated),
                      let run = Self.nonempty(row.string(7)), run == Self.nonempty(row.string(8)),
                      let fields = row.withText(9, { try? JSONParser.parse(Data($0)) })?.array, fields.count == 8,
                      fields[0].string == "chat_completion", fields[1].string == "provider_raw_usage_completion_includes_reasoning",
                      let input = Self.exactCount(fields[2]), let output = Self.exactCount(fields[3]), output > 0,
                      let total = Self.exactCount(fields[4], maximum: 2_000_000_000_000),
                      let reasoning = Self.exactCount(fields[5]), let cached = Self.exactCount(fields[6]),
                      total == input + output, reasoning <= output, cached <= input,
                      let config = Self.nonempty(fields[7].string) else { return }
                candidates.append(Candidate(proof: TraeTiming.Proof(session: session, turn: turn.turn,
                    trace: turn.trace, history: client, config: config,
                    usage: TokenUsage(input: input, cachedInput: cached, output: output, reasoning: reasoning)), server: server))
            }
        } catch { return [:] }
        var result: [String: [ResponseRecord]] = [:]
        for candidate in candidates {
            let proof = candidate.proof
            guard clients[Identity(session: proof.session, id: proof.history)] == 1,
                  servers[Identity(session: proof.session, id: candidate.server)] == 1,
                  let interval = TraeTiming.interval(proof: proof, end: nowMs, graphs: graphs, final: false),
                  interval.start >= active[proof.session]!.start,
                  interval.end >= nowMs - SpeedEngine.fallbackWindowMs else { continue }
            result[proof.session, default: []].append(ResponseRecord(key: "trae:history:" + proof.session + ":" + proof.history,
                source: .trae, agentId: agentId(proof.session), model: proof.config,
                startMs: interval.start, endMs: interval.end, usage: proof.usage))
        }
        return result.mapValues { records in
            Array(records.sorted { $0.endMs == $1.endMs ? $0.key < $1.key : $0.endMs > $1.endMs }.prefix(SpeedEngine.fallbackCount))
        }
    }

    /// These optional tables/metadata exist only in some Trae versions. A LEFT JOIN deliberately retains a newer
    /// LLM row with no server counterpart: it must prevent using an older request's counters as final-turn evidence.
    private struct ExactHistory {
        var proof: TraeTiming.Proof
        var serverId: String
        var clientCreated: Int64
        var serverCreated: Int64
        var run: String?
        var turnStart: Int64?
        var turnEnd: Int64?
    }
    private struct TurnHistory { var final: ExactHistory; var earlier: [ExactHistory] }

    private func exactHistories() -> [String: TurnHistory] {
        let hasRuns = hasColumn(table: "history_v2", column: "agent_run_id") && hasColumn(table: "server_history_info", column: "agent_run_id")
        let runProjection = hasRuns ? "v.agent_run_id, h.agent_run_id" : "NULL, NULL"
        guard let query = try? connection.prepare("""
            SELECT t.session_id, t.turn_id, t.id,
                CASE WHEN length(CAST(t.context AS BLOB)) <= 1048576 AND json_valid(t.context)
                    THEN json_extract(t.context, '$.trace_id') END,
                v.history_v2_id, v.created_at,
                CASE WHEN length(CAST(h.extra_info AS BLOB)) <= 1048576 AND json_valid(h.extra_info)
                    THEN json_extract(h.extra_info, '$.exact_usage_v1', '$.exact_token_semantics_v1',
                        '$.exact_prompt_tokens_v1', '$.exact_output_tokens_v1', '$.exact_total_tokens_v1',
                        '$.exact_reasoning_tokens_v1', '$.exact_cache_read_input_tokens_v1', '$.config_name') END,
                v.content_source, h.history_id, h.created_at, \(runProjection),
                CASE WHEN length(CAST(t.context AS BLOB)) <= 1048576 AND json_valid(t.context)
                    THEN json_extract(t.context, '$.chat_start_time', '$.chat_end_time') END
            FROM chat_turn t
            JOIN history_v2 v ON v.session_id=t.session_id AND v.message_id=t.response_message_id
            LEFT JOIN server_history_info h ON h.client_history_id=v.history_v2_id
                AND h.source='llm_default' AND h.history_source='server' AND coalesce(h.is_deleted,0)=0
            WHERE t.turn_status='completed' AND coalesce(t.deleted_at,0)=0
                AND coalesce(v.deleted_at,0)=0
            """) else { return [:] }
        struct Candidate { var session: String; var client: String?; var server: String?; var created: Int64; var isLLM: Bool; var exact: ExactHistory? }
        var groups: [String: [Candidate]] = [:]
        do {
            try query.forEachRow { row in
                guard let session = Self.nonempty(row.string(0)) else { return }
                let turn = Self.nonempty(row.string(1)) ?? String(row.int64(2))
                let key = "trae:" + session + ":" + turn
                let created = Self.validSeconds(row.optionalInt64(5))
                let serverCreated = Self.validSeconds(row.optionalInt64(9))
                let history = Self.nonempty(row.string(4)), server = Self.nonempty(row.string(8))
                var exact: ExactHistory?
                if let fields = row.withText(6, { try? JSONParser.parse(Data($0)) })?.array, fields.count == 8,
                   fields[0].string == "chat_completion", fields[1].string == "provider_raw_usage_completion_includes_reasoning",
                   let input = Self.exactCount(fields[2]), let output = Self.exactCount(fields[3]),
                   let total = Self.exactCount(fields[4], maximum: 2_000_000_000_000),
                   let reasoning = Self.exactCount(fields[5]), let cached = Self.exactCount(fields[6]),
                   total == input + output, input > 0 || output > 0, reasoning <= output, cached <= input,
                   let config = Self.nonempty(fields[7].string), let history, let server, let created, let serverCreated {
                    let proof = TraeTiming.Proof(session: session, turn: turn, trace: Self.nonempty(row.string(3)),
                        history: history, config: config, usage: TokenUsage(input: input, cachedInput: cached, output: output, reasoning: reasoning))
                    let clientRun = Self.nonempty(row.string(10)), serverRun = Self.nonempty(row.string(11))
                    let bounds = row.withText(12, { try? JSONParser.parse(Data($0)) })?.array
                    let start = bounds?.count == 2 ? Self.integer(bounds?[0], maximum: 253_402_300_799_999).map(Int64.init) : nil
                    let end = bounds?.count == 2 ? Self.integer(bounds?[1], maximum: 253_402_300_799_999).map(Int64.init) : nil
                    exact = ExactHistory(proof: proof, serverId: server, clientCreated: created, serverCreated: serverCreated,
                                         run: clientRun == serverRun ? clientRun : nil, turnStart: start, turnEnd: end)
                }
                groups[key, default: []].append(Candidate(session: session, client: history, server: server, created: created ?? Int64.max,
                                                        isLLM: row.string(7) == "llm_default", exact: exact))
            }
        } catch { return [:] }
        struct Identity: Hashable { var session: String; var id: String }
        var globalClients: [Identity: Int] = [:], globalServers: [Identity: Int] = [:]
        for candidate in groups.values.joined() {
            if let client = candidate.client { globalClients[Identity(session: candidate.session, id: client), default: 0] += 1 }
            if let server = candidate.server { globalServers[Identity(session: candidate.session, id: server), default: 0] += 1 }
        }
        var result: [String: TurnHistory] = [:]
        for (key, candidates) in groups {
            let llm = candidates.filter(\.isLLM)
            guard let latest = llm.map(\.created).max(), latest != Int64.max else { continue }
            let final = llm.filter { $0.created == latest }
            let clients = Dictionary(grouping: candidates, by: \.client)
            let servers = Dictionary(grouping: candidates, by: \.server)
            guard final.count == 1, let exact = final[0].exact,
                  clients[exact.proof.history]?.count == 1, servers[exact.serverId]?.count == 1 else { continue }
            var earlier: [ExactHistory] = []
            if let run = exact.run, let start = exact.turnStart, let end = exact.turnEnd, start > 0, start <= end,
               globalClients[Identity(session: exact.proof.session, id: exact.proof.history)] == 1,
               globalServers[Identity(session: exact.proof.session, id: exact.serverId)] == 1 {
                let firstSecond = start / 1000, lastSecond = (end + 999) / 1000
                if (firstSecond...lastSecond).contains(exact.clientCreated), (firstSecond...lastSecond).contains(exact.serverCreated) {
                    earlier = candidates.compactMap(\.exact).filter {
                        $0.run == run && $0.proof.history != exact.proof.history
                            && (firstSecond...exact.clientCreated).contains($0.clientCreated)
                            && (firstSecond...exact.serverCreated).contains($0.serverCreated)
                            && clients[$0.proof.history]?.count == 1 && servers[$0.serverId]?.count == 1
                            && globalClients[Identity(session: $0.proof.session, id: $0.proof.history)] == 1
                            && globalServers[Identity(session: $0.proof.session, id: $0.serverId)] == 1
                    }.sorted { $0.serverCreated == $1.serverCreated ? $0.proof.history < $1.proof.history : $0.serverCreated < $1.serverCreated }
                }
            }
            result[key] = TurnHistory(final: exact, earlier: earlier)
        }
        return result
    }

    private static func validSeconds(_ value: Int64?) -> Int64? {
        guard let value, value > 0, value <= 253_402_300_799 else { return nil }
        return value
    }

    private func hasColumn(table: String, column: String) -> Bool {
        guard let query = try? connection.prepare("SELECT 1 FROM pragma_table_info(?1) WHERE name=?2") else { return false }
        query.bind(1, table)
        query.bind(2, column)
        return (try? query.step()) == true
    }

    private static func exactCount(_ value: JSONValue, maximum: Int = 1_000_000_000_000) -> Int? {
        guard let text = value.string, !text.isEmpty, text.utf8.count <= 13,
              text.utf8.allSatisfy({ (48...57).contains($0) }), let count = Int(text), count <= maximum else { return nil }
        return count
    }

    func agentId(_ session: String) -> String { path + "#" + session }

    private static func integer(_ value: JSONValue?, maximum: Int = 1_000_000_000_000) -> Int? {
        guard let number = value?.literal?.double, number.isFinite, number >= 0, number <= Double(maximum),
              number.rounded(.towardZero) == number else { return nil }
        return Int(number)
    }

    private static func optionalCount(_ value: JSONValue?) -> Int? {
        value == nil || value == .null ? 0 : integer(value)
    }

    private static func nonempty(_ value: String?) -> String? {
        guard let value, !value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, value.utf8.count <= 4096 else { return nil }
        return value
    }
}
