import Foundation

/// Per-file Pi state machine: the omp rules of METRICS 4.7 with Pi's differences (METRICS 4.13).
struct PiParser: Sendable {
    let agentId: String
    let fileID: FileID
    private(set) var state: AgentState

    private var headerSeen = false
    private var headerMs: Int64?
    private var pendingToolCalls: Set<String> = []

    init(agentId: String, fileID: FileID) {
        self.agentId = agentId
        self.fileID = fileID
        let parent = Self.parentFile(of: agentId)
        state = AgentState(source: .pi, isSubagent: parent != nil)
        state.thread = SessionThread(id: nil, parentId: parent, createdMs: 0)
    }

    /// A sub-agent lives in a folder named after its session file: that file when it is a regular file, else nil.
    static func parentFile(of path: String) -> String? {
        let candidate = (path as NSString).deletingLastPathComponent + ".jsonl"
        guard candidate != path, FileStat.lstat(candidate)?.isRegularFile == true else { return nil }
        return candidate
    }

    mutating func noteMalformedLine() {
        state.malformedLines += 1
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

    mutating func consume(
        _ extraction: LineExtraction<PiLine>,
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

    private mutating func consume(_ line: PiLine, tools: [ToolEvent], deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        switch line {
        case .ignored:
            return
        case .header(let header):
            guard !headerSeen else { return }
            headerSeen = true
            headerMs = header.timestampMs
            state.project = header.cwd.flatMap(OmpParser.basename)
            state.cwdPath = header.cwd.flatMap { $0.isEmpty ? nil : $0 }
            state.thread?.id = header.id
            state.thread?.createdMs = header.timestampMs ?? 0
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
        _ record: PiRecord,
        id: String,
        at timestamp: Int64,
        tools: [ToolEvent],
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        switch record {
        case .input:
            state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)
            pendingToolCalls.removeAll()
        case .assistant(let assistant):
            consume(assistant, id: id, at: timestamp, tools: tools, deduper: &deduper, emit: emit)
        case .toolResult(let callId, let usage):
            side(usage, id: id, at: timestamp, model: nil, provider: nil, deduper: &deduper, emit: emit)
            if let callId { pendingToolCalls.remove(callId) }
            if state.phase == .tools, pendingToolCalls.isEmpty {
                state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)
            }
        case .usageEntry(let model, let provider, let usage):
            side(usage, id: id, at: timestamp, model: model, provider: provider, deduper: &deduper, emit: emit)
        case .summary(let usage):
            side(usage, id: id, at: timestamp, model: nil, provider: nil, deduper: &deduper, emit: emit)
        case .thinkingLevel(let effort):
            if let effort { state.modes = ExecutionModes([effort]) }
        case .other:
            break
        }
    }

    private mutating func consume(
        _ assistant: PiAssistant,
        id: String,
        at timestamp: Int64,
        tools: [ToolEvent],
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        let key = "pi:\(id):\(assistant.startedMs ?? timestamp)"
        guard !deduper.isOwnedByOtherFile(key, for: fileID) else { return }
        defer { state.actions.recordCalls(tools, at: timestamp, model: state.model) }
        var endMs = timestamp
        if let started = assistant.startedMs, let duration = assistant.durationMs {
            let (sum, overflow) = started.addingReportingOverflow(duration)
            endMs = overflow ? .max : sum
        }
        // The context holds one effort fact; the message's own level replaces it (EXECUTION-MODES, Pi).
        if let effort = assistant.effort { state.modes = ExecutionModes([effort]) }
        if assistant.usage.hasUsage {
            // Failed, interrupted and deferred requests count in totals only.
            let startMs = assistant.stop.isFailure ? nil : assistant.startedMs
            record(key: key, startMs: startMs, endMs: endMs, usage: assistant.usage, model: assistant.model,
                   provider: assistant.provider, modes: state.modes, deduper: &deduper, emit: emit)
            state.model = assistant.model
        }
        if assistant.stop.isFailure {
            state.setPhase(.idle, at: timestamp)
            pendingToolCalls.removeAll()
        } else if !assistant.toolCallIds.isEmpty, assistant.stop.runsTools {
            state.setPhase(.tools, at: timestamp)
            pendingToolCalls = assistant.toolCallIds
        } else {
            state.setPhase(.idle, at: timestamp)
        }
    }

    /// Side usage: no timing, no execution-mode facts, and it never sets the agent's model.
    private func side(
        _ usage: PiUsage?,
        id: String,
        at timestamp: Int64,
        model: String?,
        provider: String?,
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        guard let usage, usage.hasUsage else { return }
        let key = "pi:\(id):\(timestamp)"
        guard !deduper.isOwnedByOtherFile(key, for: fileID) else { return }
        record(key: key, startMs: nil, endMs: timestamp, usage: usage, model: model, provider: provider, modes: .empty,
               deduper: &deduper, emit: emit)
    }

    private func record(
        key: String,
        startMs: Int64?,
        endMs: Int64,
        usage: PiUsage,
        model: String?,
        provider: String?,
        modes: ExecutionModes,
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        let claim = deduper.claim(key, for: fileID)
        let record = ResponseRecord(key: key, source: .pi, agentId: agentId, model: model, startMs: startMs, endMs: endMs,
                                    usage: usage.tokens, isFinal: true, provider: provider, modes: modes)
        // A repeat in the same file (a rewrite read again) is an update (METRICS 4.4).
        emit(ResponseUpsert(record: record, isCompletion: claim == .new))
    }
}
