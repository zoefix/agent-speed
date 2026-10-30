import Foundation

/// The state machine of one OpenClaw transcript window: Pi's rules (METRICS 4.7, 4.13) with OpenClaw's differences
/// (4.14): no duration field, no side usage, mirrors of delegated turns count nothing, untimed CLI and delegated turns,
/// and a delegated window stays idle.
struct OpenClawParser: Sendable {
    static let keyPrefix = "openclaw:"
    /// Recent responses a late trace row can still update.
    static let updatableResponses = 1024

    private struct Emitted: Sendable {
        /// The stamp a trace row's `created_at` is compared with: `message.timestamp`, else the entry's.
        var stampMs: Int64
        var record: ResponseRecord
    }

    let agentId: String
    let fileID: FileID
    let window: OpenClawWindow
    private(set) var state: AgentState

    private var headerSeen = false
    private var headerMs: Int64?
    private var pendingToolCalls: Set<String> = []
    /// The window's trace rows in `seq` order.
    private var traces: [OpenClawTrace] = []
    private var emitted: [Emitted] = []

    init(agentId: String, fileID: FileID, window: OpenClawWindow) {
        self.agentId = agentId
        self.fileID = fileID
        self.window = window
        state = AgentState(source: .openclaw, isSubagent: window.isSubagent)
    }

    mutating func takeToolCompletions() -> [ToolCompletion] {
        state.actions.takeCompletions()
    }

    mutating func setSeeding(_ seeding: Bool) {
        state.actions.marksSeeded = seeding
    }

    mutating func keepCompletionsOnly() {
        state.actions.storesActions = false
    }

    /// Adds rows read after the ones before them. A response at or after a new row takes its facts, like any upsert
    /// (METRICS 4.4): OpenClaw buffers a recorder's rows, so a row is often newer in the database than its first answers.
    mutating func addTraces(_ rows: [OpenClawTrace], emit: (ResponseUpsert) -> Void) {
        guard !rows.isEmpty else { return }
        traces += rows
        state.modes = traces[traces.count - 1].modes
        for index in emitted.indices {
            let modes = modes(at: emitted[index].stampMs)
            guard modes != emitted[index].record.modes else { continue }
            emitted[index].record.modes = modes
            emit(ResponseUpsert(record: emitted[index].record, isCompletion: false))
        }
    }

    /// The facts of the window's latest row at or before `stamp` (EXECUTION-MODES, OpenClaw).
    private func modes(at stamp: Int64) -> ExecutionModes {
        traces.last { $0.createdMs <= stamp }?.modes ?? .empty
    }

    /// The dedupe key of an assistant entry (METRICS 4.14), whose calls another window may own; nil for other entries.
    static func entryKey(_ extraction: LineExtraction<OpenClawLine>) -> String? {
        guard case .line(.entry(let id, let timestamp, .assistant(let assistant))) = extraction else { return nil }
        return keyPrefix + id + ":" + String(assistant.startedMs ?? timestamp)
    }

    mutating func consume(
        _ extraction: LineExtraction<OpenClawLine>,
        tools: [ToolEvent] = [],
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        switch extraction {
        case .blank:
            return
        case .malformed:
            state.malformedLines += 1
        case .line(let line):
            consume(line, tools: tools, deduper: &deduper, emit: emit)
        }
    }

    private mutating func consume(_ line: OpenClawLine, tools: [ToolEvent], deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        switch line {
        case .ignored:
            return
        case .header(let header):
            guard !headerSeen else { return }
            headerSeen = true
            headerMs = header.timestampMs
            state.project = header.cwd.flatMap(OmpParser.basename)
            state.cwdPath = header.cwd.flatMap { $0.isEmpty ? nil : $0 }
            if let timestamp = header.timestampMs { state.noteLine(at: timestamp) }
        case .entry(let id, let timestamp, let record):
            guard headerSeen else { return }
            if let headerMs, timestamp < headerMs - OmpParser.copiedHistoryMs { return }
            state.noteLine(at: timestamp)
            state.actions.recordResults(tools, at: timestamp)
            consume(record, id: id, at: timestamp, tools: tools, deduper: &deduper, emit: emit)
        }
    }

    private mutating func consume(
        _ record: OpenClawRecord,
        id: String,
        at timestamp: Int64,
        tools: [ToolEvent],
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        switch record {
        case .assistant(let assistant):
            consume(assistant, id: id, at: timestamp, tools: tools, deduper: &deduper, emit: emit)
        case .input where !window.isDelegated:
            state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)
            pendingToolCalls.removeAll()
        case .toolResult(let callId) where !window.isDelegated:
            if let callId { pendingToolCalls.remove(callId) }
            if state.phase == .tools, pendingToolCalls.isEmpty {
                state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)
            }
        case .input, .toolResult, .other:
            break
        }
    }

    private mutating func consume(
        _ assistant: OpenClawAssistant,
        id: String,
        at timestamp: Int64,
        tools: [ToolEvent],
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        let key = Self.keyPrefix + id + ":" + String(assistant.startedMs ?? timestamp)
        guard !deduper.isOwnedByOtherFile(key, for: fileID) else { return }
        let isMirror = assistant.isMirror || window.mirrorsTurns
        // A mirror's tools ran in another runtime, which records them in its own log (METRICS 6.10).
        defer { if !isMirror { state.actions.recordCalls(tools, at: timestamp, model: state.model) } }
        if !isMirror, !assistant.usageUnavailable, assistant.hasUsage {
            // A delegated or CLI turn's usage covers the whole turn; failed requests count in totals only.
            let timed = !window.isDelegated && !assistant.isCLI && !assistant.stopIsFailure
            let claim = deduper.claim(key, for: fileID)
            let stamp = assistant.startedMs ?? timestamp
            let record = ResponseRecord(key: key, source: .openclaw, agentId: agentId, model: assistant.model,
                                        startMs: timed ? assistant.startedMs : nil, endMs: timestamp,
                                        usage: assistant.tokens, isFinal: true, provider: assistant.provider,
                                        modes: modes(at: stamp))
            // A repeat in the same window (a rewrite read again) is an update (METRICS 4.4).
            emit(ResponseUpsert(record: record, isCompletion: claim == .new))
            emitted.removeAll { $0.record.key == key }
            emitted.append(Emitted(stampMs: stamp, record: record))
            if emitted.count > Self.updatableResponses { emitted.removeFirst(emitted.count - Self.updatableResponses) }
            state.model = assistant.model
        }
        guard !window.isDelegated else { return }
        if assistant.stopIsFailure {
            state.setPhase(.idle, at: timestamp)
            pendingToolCalls.removeAll()
        } else if !assistant.toolCallIds.isEmpty, assistant.stopRunsTools {
            state.setPhase(.tools, at: timestamp)
            pendingToolCalls = assistant.toolCallIds
        } else {
            state.setPhase(.idle, at: timestamp)
        }
    }
}
