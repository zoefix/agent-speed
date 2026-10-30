import Foundation

/// The parser state of one Hermes Agent log folder (METRICS 4.15): every file of the folder feeds it, one agent per
/// session id, plus the folder's Codex runtime switch.
struct HermesLogSet: Sendable {
    /// The log folder's path as configured; agent ids are `<folder>#<session id>`.
    let folder: String
    let timeZone: TimeZone
    /// Owner of the set's response keys in the dedupe set.
    let fileID: FileID
    private(set) var agents: [String: AgentState] = [:]
    /// Session ids in the order their agents appeared.
    private(set) var sessionOrder: [String] = []
    /// Per session: the latest turn start whose platform is not `subagent` (DESIGN 5 counts these sessions).
    private(set) var mainTurnMs: [String: Int64] = [:]
    private(set) var malformedLines = 0
    private(set) var codexRuntime = false
    /// The profile's configured values, which every agent of the set shows.
    private(set) var configuredModes: ExecutionModes = .empty

    init(folder: String, timeZone: TimeZone, fileID: FileID) {
        self.folder = folder
        self.timeZone = timeZone
        self.fileID = fileID
    }

    func agentId(_ sessionId: String) -> String { Self.agentId(folder: folder, sessionId: sessionId) }

    static func agentId(folder: String, sessionId: String) -> String { folder + "#" + sessionId }

    /// Reads one physical line (without its `\n`). Returns the session id whose agent the line touched, if any.
    @discardableResult
    mutating func consume(_ line: UnsafeRawBufferPointer, deduper: inout Deduper, emit: (ResponseUpsert) -> Void) -> String? {
        guard let record = HermesLine.parse(line, timeZone: timeZone) else { return nil }
        return consume(record, deduper: &deduper, emit: emit)
    }

    @discardableResult
    mutating func consume(_ record: HermesLine, deduper: inout Deduper, emit: (ResponseUpsert) -> Void) -> String? {
        let sessionId = record.sessionId
        switch record.kind {
        case .turn(let model, let platform):
            var state = agent(sessionId, at: record.timeMs)
            if codexRuntime {
                state.setPhase(.idle, at: record.timeMs)
            } else {
                state.setPhase(.generating, at: record.timeMs, inFlightStartMs: record.timeMs)
            }
            if !model.isEmpty { state.model = model }
            state.isSubagent = platform == "subagent"
            if !state.isSubagent { mainTurnMs[sessionId] = max(mainTurnMs[sessionId] ?? record.timeMs, record.timeMs) }
            agents[sessionId] = state
        case .end:
            var state = agent(sessionId, at: record.timeMs)
            state.setPhase(.idle, at: record.timeMs)
            agents[sessionId] = state
        case .call(nil):
            malformedLines += 1
            return nil
        case .call(let call?):
            var state = agent(sessionId, at: record.timeMs)
            defer { agents[sessionId] = state }
            let key = "hermes:" + sessionId + ":" + call.number + ":" + record.stamp
            // A key seen before, in any file, is a copy: only `lastLineMs` counts.
            guard deduper.claim(key, for: fileID) == .new else { return sessionId }
            if !call.model.isEmpty { state.model = call.model }
            if state.phase == .generating { state.inFlightStartMs = record.timeMs }
            if let response = Self.response(call, key: key, agentId: agentId(sessionId), endMs: record.timeMs) {
                emit(ResponseUpsert(record: response, isCompletion: true))
            }
        }
        return sessionId
    }

    /// Sets the switch; turning it on idles every generating agent. Returns the sessions whose phase changed.
    @discardableResult
    mutating func setCodexRuntime(_ on: Bool) -> [String] {
        defer { codexRuntime = on }
        guard on, !codexRuntime else { return [] }
        var changed: [String] = []
        for sessionId in sessionOrder {
            guard var state = agents[sessionId], state.phase == .generating else { continue }
            state.setPhase(.idle, at: state.lastLineMs ?? 0)
            agents[sessionId] = state
            changed.append(sessionId)
        }
        return changed
    }

    /// The profile's configured values. Returns the sessions that changed.
    @discardableResult
    mutating func setConfiguredModes(_ modes: ExecutionModes) -> [String] {
        guard modes != configuredModes else { return [] }
        configuredModes = modes
        for sessionId in sessionOrder { agents[sessionId]?.configuredModes = modes }
        return sessionOrder
    }

    /// A session's session-current fact (EXECUTION-MODES). True when it changed.
    @discardableResult
    mutating func setSessionModes(_ modes: ExecutionModes, of sessionId: String) -> Bool {
        guard agents[sessionId] != nil, agents[sessionId]?.sessionModes != modes else { return false }
        agents[sessionId]?.sessionModes = modes
        return true
    }

    /// Sessions of the set that are not sub-agents and started a turn since `todayStartMs`.
    func mainSessionCount(since todayStartMs: Int64) -> Int {
        mainTurnMs.values.filter { $0 >= todayStartMs }.count
    }

    private mutating func agent(_ sessionId: String, at timeMs: Int64) -> AgentState {
        var state = agents[sessionId] ?? {
            sessionOrder.append(sessionId)
            var state = AgentState(source: .hermes)
            state.configuredModes = configuredModes
            return state
        }()
        state.noteLine(at: timeMs)
        return state
    }

    /// The response of a well-formed API call, or nil without usage (`?`) or with zero input and output.
    static func response(_ call: HermesCall, key: String, agentId: String, endMs: Int64) -> ResponseRecord? {
        guard let input = call.input, let output = call.output else { return nil }
        let usage = TokenUsage(
            input: count(input), cachedInput: call.cacheRead.map(count) ?? 0,
            cacheWriteInput: call.cacheWrite.map(count) ?? 0, output: count(output))
        guard usage.input != 0 || usage.output != 0 else { return nil }
        let startMs: Int64? = call.latency.hasPrefix("-") ? nil : endMs.subtractingSaturating(latencyMs(call.latency))
        let provider = call.provider.isEmpty || call.provider == "unknown" ? nil : call.provider
        return ResponseRecord(
            key: key, source: .hermes, agentId: agentId, model: call.model.isEmpty ? nil : call.model,
            startMs: startMs, endMs: endMs, usage: usage, isFinal: true, provider: provider)
    }

    /// A token count as written (`-?[0-9]+`): negatives 0, capped at 10^12 (METRICS 3.2).
    static func count(_ text: String) -> Int {
        guard !text.hasPrefix("-") else { return 0 }
        var value: Int64 = 0
        for byte in text.utf8 {
            value = value * 10 + Int64(byte - UInt8(ascii: "0"))
            if value > TokenCount.ceiling { return Int(TokenCount.ceiling) }
        }
        return Int(value)
    }

    /// The latency's decimal digits × 1000, truncated to whole milliseconds and saturated (METRICS 3.6).
    static func latencyMs(_ text: String) -> Int64 {
        let parts = text.split(separator: ".", maxSplits: 1, omittingEmptySubsequences: false)
        var seconds: Int64 = 0
        for byte in parts[0].utf8 {
            let (scaled, overflow) = seconds.multipliedReportingOverflow(by: 10)
            guard !overflow else { return .max }
            seconds = scaled.addingSaturating(Int64(byte - UInt8(ascii: "0")))
        }
        var millis: Int64 = 0
        let fraction = parts.count > 1 ? Array(parts[1].utf8.prefix(3)) : []
        for index in 0..<3 {
            millis = millis * 10 + (index < fraction.count ? Int64(fraction[index] - UInt8(ascii: "0")) : 0)
        }
        let (whole, overflow) = seconds.multipliedReportingOverflow(by: 1000)
        return overflow ? .max : whole.addingSaturating(millis)
    }
}

private extension Int64 {
    func addingSaturating(_ other: Int64) -> Int64 {
        let (result, overflow) = addingReportingOverflow(other)
        return overflow ? (other > 0 ? .max : .min) : result
    }

    func subtractingSaturating(_ other: Int64) -> Int64 {
        let (result, overflow) = subtractingReportingOverflow(other)
        return overflow ? (other > 0 ? .min : .max) : result
    }
}
