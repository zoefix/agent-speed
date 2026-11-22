import Foundation

struct OpenBitFunRound: Sendable {
    let id: OpenBitFunRoundID
    let model: String?
    let start: Int64?
    let end: Int64?
    let duration: Int64?
    let completed: Bool
    let attempts: Int

    static func timestamp(_ value: JSONValue?) -> Int64? {
        if let text = value?.string { return Timestamp.parse(text) }
        guard let ms = HarnessJSON.milliseconds(value), (0...253_402_300_799_999).contains(ms) else { return nil }
        return ms
    }

    init?(_ json: JSONValue, turn: String, turnEnded: Bool) {
        guard let round = HarnessJSON.text(json["id"]) else { return nil }
        id = OpenBitFunRoundID(turn: turn, round: round)
        model = HarnessJSON.text(json["effectiveModelName"]) ?? HarnessJSON.text(json["modelConfigId"])
        start = Self.timestamp(json["startTime"])
        end = Self.timestamp(json["endTime"])
        duration = HarnessJSON.milliseconds(json["durationMs"]).flatMap { $0 >= 0 ? $0 : nil }
        completed = turnEnded || end != nil || ["completed", "failed", "cancelled", "interrupted"].contains(json["status"]?.string)
        attempts = HarnessJSON.count(json["attemptCount"])
    }
}

/// Only metadata and usage/timing fields survive decoding; request bodies and response text are discarded.
struct OpenBitFunLine: Sendable {
    var session: String?
    var parent: String?
    var subagent = false
    var cwd: String?
    var created: Int64 = 0
    var rounds: [OpenBitFunRound]?
    var turn: String?
    var operation: String?
    var roundID: String?
    var trace: String?
    var model: String?
    var provider: String?
    var start: Int64?
    var end: Int64?
    var duration: Int64?
    var completed = false
    var usage: TokenUsage?

    static func extract(_ bytes: UnsafeRawBufferPointer) -> LineExtraction<Self> {
        guard let json = try? JSONParser.parse(Data(bytes)), json.object != nil else { return .malformed }
        var line = Self()
        if let turn = HarnessJSON.text(json["turnId"]), let rows = json["modelRounds"]?.array {
            let ended = ["completed", "cancelled", "error"].contains(json["status"]?.string)
            line.session = json["sessionId"]?.string
            line.turn = turn
            line.completed = ended
            line.start = OpenBitFunRound.timestamp(json["startTime"])
            line.end = OpenBitFunRound.timestamp(json["endTime"])
            line.rounds = rows.compactMap { OpenBitFunRound($0, turn: turn, turnEnded: ended) }
            return .line(line)
        }
        if let session = json["sessionId"]?.string ?? json["session_id"]?.string, json["trace_id"] == nil {
            line.session = session
            let relationship = json["relationship"]
            line.parent = relationship?["parentSessionId"]?.string ?? relationship?["parent_session_id"]?.string
            line.subagent = (json["sessionKind"]?.string ?? json["session_kind"]?.string) == "subagent"
                || relationship?["kind"]?.string == "subagent"
            line.cwd = json["workspacePath"]?.string ?? json["workspace_path"]?.string
            line.created = HarnessJSON.milliseconds(json["createdAt"] ?? json["created_at"]) ?? 0
            return .line(line)
        }
        guard HarnessJSON.milliseconds(json["version"]) == 1 else { return .line(line) }
        line.trace = json["trace_id"]?.string
        line.turn = HarnessJSON.text(json["turn_id"])
        line.operation = HarnessJSON.text(json["operation_kind"])
        line.roundID = HarnessJSON.text(json["operation_id"])
        line.session = json["session_id"]?.string
        line.model = json["request"]?["model_id"]?.string
        line.provider = json["request"]?["provider"]?.string
        line.start = json["timing"]?["started_at"]?.string.flatMap(Timestamp.parse)
        line.end = json["timing"]?["finished_at"]?.string.flatMap(Timestamp.parse)
        line.duration = HarnessJSON.milliseconds(json["timing"]?["duration_ms"])
        line.completed = json["response"]?.object != nil
        if let usage = json["response"]?["usage"], usage.object != nil {
            let output = HarnessJSON.count(usage["candidatesTokenCount"])
            line.usage = TokenUsage(input: HarnessJSON.count(usage["promptTokenCount"]),
                                   cachedInput: HarnessJSON.count(usage["cachedContentTokenCount"]),
                                   cacheWriteInput: HarnessJSON.count(usage["cacheCreationTokenCount"]),
                                   output: output, reasoning: min(output, HarnessJSON.count(usage["reasoningTokenCount"])))
        }
        return .line(line)
    }
}

struct OpenBitFunBatch: Sendable {
    var lines: [LineExtraction<OpenBitFunLine>] = []
    var usage: OpenBitFunUsageLedger.Snapshot?
    var baseline: Set<OpenBitFunRoundID> = []
}

struct OpenBitFunParser: Sendable {
    let agentId: String
    let fileID: FileID
    private(set) var state = AgentState(source: .openbitfun)
    private var latest: [String: ResponseRecord] = [:]
    private var traces: [String: OpenBitFunLine] = [:]
    private var traceOrder: [String] = []
    private var turns: [String: (start: Int64?, end: Int64?, closed: Bool)] = [:]
    private var rounds: [OpenBitFunRoundID: OpenBitFunRound] = [:]
    private var usage = OpenBitFunUsageLedger.Snapshot.empty
    private var baseline: Set<OpenBitFunRoundID> = []

    mutating func consume(_ batch: OpenBitFunBatch, deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        guard batch.usage != nil || !batch.lines.isEmpty else { return }
        baseline.formUnion(batch.baseline)
        if let snapshot = batch.usage {
            if snapshot.rounds.isEmpty && !usage.rounds.isEmpty {
                traces.removeAll()
                traceOrder.removeAll()
            }
            usage = snapshot
        }
        for extraction in batch.lines {
            guard case .line(let line) = extraction else {
                if case .malformed = extraction { noteMalformedLine() }
                continue
            }
            if let measurements = line.rounds, let turn = line.turn {
                guard line.session == state.thread?.id else { continue }
                if turns[turn] != nil { rounds = rounds.filter { $0.key.turn != turn } }
                for round in measurements { rounds[round.id] = round }
                let start = line.start ?? measurements.compactMap(\.start).min()
                let end = line.end ?? measurements.compactMap { $0.end ?? $0.start }.max()
                turns[turn] = (start, end, line.completed)
            } else if let trace = line.trace, !trace.isEmpty {
                if line.session == state.thread?.id, line.completed || traces[trace]?.completed != true {
                    if traces.updateValue(line, forKey: trace) == nil { traceOrder.append(trace) }
                }
            } else if let session = line.session {
                state.thread = SessionThread(id: session, parentId: line.parent, createdMs: line.created)
                state.isSubagent = line.subagent
                if let cwd = line.cwd { state.setProject(fromWorkingDirectory: cwd) }
            }
        }
        guard let session = state.thread?.id else { return }
        var pending: Int64?
        var active = Set<String>()
        for trace in traceOrder {
            guard let line = traces[trace] else { continue }
            let key = "openbitfun:" + trace
            if usage.rounds.isEmpty, let start = line.start {
                let time = line.end ?? start
                if time >= (state.lastLineMs ?? .min) { state.model = line.model }
                state.noteLine(at: time)
                if !line.completed {
                    let closed: Bool
                    if let turn = line.turn { closed = turns[turn]?.closed == true }
                    else {
                        let owners = turns.values.filter { turn in
                            turn.start.map { $0 <= start } == true && (!turn.closed || turn.end.map { start <= $0 } == true)
                        }
                        closed = owners.count == 1 && owners[0].closed
                    }
                    if !closed { pending = min(pending ?? start, start) }
                }
            }
            guard line.completed, let end = line.end, let tokens = line.usage else { continue }
            var owner: OpenBitFunRoundID?
            if line.operation == "model_round", let turn = line.turn, let round = line.roundID {
                owner = OpenBitFunRoundID(turn: turn, round: round)
            } else if line.operation == nil, let start = line.start {
                let matches = rounds.values.filter { round in
                    (line.turn == nil || line.turn == round.id.turn) && round.start.map { $0 <= start } == true
                        && round.end.map { end <= $0 } == true
                }
                if matches.count == 1 { owner = matches[0].id }
            }
            let group = owner.map { "openbitfun:round:" + session + ":" + $0.turn + ":" + $0.round }
            if !usage.rounds.isEmpty {
                if var record = latest[key] {
                    record.traceRoundKey = group ?? record.traceRoundKey
                    withdraw(record, deduper: &deduper, emit: emit)
                }
                continue
            }
            var start = line.start
            if let duration = line.duration, duration >= 0 {
                start = duration <= ResponseTiming.maxIntervalMs ? end - duration : nil
            }
            var record = ResponseRecord(key: key, source: .openbitfun, agentId: agentId, model: line.model,
                                        startMs: start, endMs: end, usage: tokens, provider: line.provider)
            record.traceRoundKey = group
            active.insert(key)
            upsert(record, deduper: &deduper, emit: emit)
        }
        for round in rounds.values.sorted(by: { ($0.end ?? $0.start ?? 0) < ($1.end ?? $1.start ?? 0) }) {
            if let start = round.start {
                let time = round.end ?? start
                if time >= (state.lastLineMs ?? .min) { state.model = round.model }
                state.noteLine(at: time)
                if !round.completed { pending = min(pending ?? start, start) }
            }
            guard let end = round.end, let totals = usage.rounds[round.id], totals.count >= round.attempts,
                  !baseline.contains(round.id) else { continue }
            let duration = round.duration ?? round.start.map { end - $0 }
            let start = duration.flatMap { totals.measured && (1...ResponseTiming.maxIntervalMs).contains($0) ? end - $0 : nil }
            let record = ResponseRecord(key: "openbitfun:round:" + session + ":" + round.id.turn + ":" + round.id.round,
                                        source: .openbitfun, agentId: agentId, model: totals.model ?? round.model,
                                        startMs: start, endMs: end, usage: totals.tokens, provider: totals.provider,
                                        requestCount: totals.count)
            active.insert(record.key)
            upsert(record, deduper: &deduper, emit: emit)
        }
        for record in latest.values where record.requestCount > 0 && !active.contains(record.key) {
            withdraw(record, deduper: &deduper, emit: emit)
        }
        if let last = state.lastLineMs {
            state.setPhase(pending == nil ? .idle : .generating, at: pending ?? last, inFlightStartMs: pending)
        }
    }

    mutating func noteMalformedLine() { state.malformedLines += 1 }

    private mutating func withdraw(_ previous: ResponseRecord, deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        var record = previous
        record.startMs = nil
        record.usage = .zero
        record.requestCount = 0
        upsert(record, deduper: &deduper, emit: emit)
    }

    private mutating func upsert(_ record: ResponseRecord, deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        guard latest[record.key] != record else { return }
        let claim = deduper.claim(record.key, for: fileID)
        guard claim != .otherFile else { return }
        let first = claim == .new && latest[record.key] == nil && record.requestCount > 0
        latest[record.key] = record
        emit(ResponseUpsert(record: record, isCompletion: first))
    }
}
