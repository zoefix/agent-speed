import Foundation

/// Per-file Codex state machine (METRICS 4.2).
struct CodexParser: Sendable {
    static let copiedHistoryToleranceMs: Int64 = 2000
    static let defaultAgentPath = "/root"

    let agentId: String
    let fileID: FileID
    private(set) var state = AgentState(source: .codex)

    private var myAgentPath = CodexParser.defaultAgentPath
    private var pendingStartMs: Int64?
    private var streaming = false
    /// The output line that started the streaming response (METRICS 4.2).
    private var firstOutputMs: Int64?
    private var sawModernUsage = false
    private var previousLegacyTotals: CodexRawUsage?
    /// `model_provider` of the first `session_meta` (METRICS 3.2).
    private var provider: String?
    /// Lines of a paginated sub-agent below this ordinal copy its parent's records (METRICS 3.5).
    private var subagentStartOrdinal: Int64?

    init(agentId: String, fileID: FileID) {
        self.agentId = agentId
        self.fileID = fileID
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
        _ extraction: LineExtraction<CodexLine>,
        tools: [ToolEvent] = [],
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        switch extraction {
        case .blank:
            return
        case .malformed:
            state.malformedLines += 1
            return
        case .line(let line):
            consume(line, tools: tools, deduper: &deduper, emit: emit)
        }
    }

    private mutating func consume(_ line: CodexLine, tools: [ToolEvent], deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        guard let timestamp = line.timestampMs else { return }
        if case .sessionMeta(let meta) = line.kind {
            // Only the first session_meta applies; sub-agent files copy their parent's right after their own.
            guard state.thread == nil else { return }
            state.noteLine(at: timestamp)
            state.thread = SessionThread(id: meta.id, parentId: meta.parentThreadId, createdMs: timestamp)
            if let cwd = meta.cwd { state.setProject(fromWorkingDirectory: cwd) }
            myAgentPath = meta.agentPath ?? Self.defaultAgentPath
            state.isSubagent = meta.isSubagent
            provider = meta.modelProvider
            subagentStartOrdinal = meta.subagentStartOrdinal
            return
        }
        if let created = state.thread?.createdMs, timestamp < created - Self.copiedHistoryToleranceMs {
            return
        }
        if let start = subagentStartOrdinal, let ordinal = line.ordinal, ordinal < start {
            return
        }
        state.noteLine(at: timestamp)
        // After the line, with the model of the current turn (METRICS 6.10).
        defer { state.actions.record(tools, at: timestamp, model: state.model) }
        var requestModes = ExecutionModes.empty
        switch line.modeUpdate {
        case .none: break
        case .turn(let facts):
            state.modes = ExecutionModes(state.modes.facts.filter { $0.scope != .turn } + facts.facts)
        case .session(let settings):
            var facts = state.modes.facts
            for setting in settings {
                facts.removeAll {
                    $0.kind == setting.kind && $0.evidence == .selected && $0.scope == .session && $0.provider == setting.provider
                }
                if let fact = setting.fact { facts.append(fact) }
            }
            state.modes = ExecutionModes(facts)
        case .request(let facts):
            requestModes = facts
        }

        switch line.kind {
        case .sessionMeta:
            return

        case .turnContext(let model, let cwd):
            if let model { state.model = model }
            if let cwd { state.setProject(fromWorkingDirectory: cwd) }

        case .input:
            markInput(at: timestamp)

        case .agentMessage(let recipient):
            // A mailbox input marks a possible request start; an orchestrator may receive it while blocked.
            if recipient == myAgentPath, !streaming { pendingStartMs = timestamp }

        case .output:
            if !streaming { firstOutputMs = timestamp }
            streaming = true
            state.setPhase(.generating, at: timestamp, inFlightStartMs: pendingStartMs ?? timestamp)

        case .modernUsage(let responseId, let threadId, let usage):
            sawModernUsage = true
            let key = responseId ?? "codex:\(threadId ?? state.thread?.id ?? agentId):\(timestamp)"
            guard deduper.claim(key, for: fileID) == .new else { return }
            complete(key: key, usage: usage.normalized, modes: state.modes.merging(requestModes), at: timestamp, emit: emit)

        case .legacyUsage(let threadId, let info):
            if sawModernUsage {
                // A response that ended without a usage record (seen when an orchestrator starts waiting).
                if streaming { finishWithoutUsage(at: timestamp) }
                return
            }
            guard let info, let total = info.total else { return }
            let tokens: CodexRawUsage
            if let previous = previousLegacyTotals {
                let (delta, hasNegative) = total.delta(since: previous)
                if delta.total == 0 { return }
                tokens = hasNegative ? (info.last ?? .zero) : delta
            } else {
                tokens = info.last ?? total
            }
            previousLegacyTotals = total
            let key = "codex-legacy:\(threadId ?? state.thread?.id ?? agentId):\(total.total)"
            guard deduper.claim(key, for: fileID) == .new else { return }
            complete(key: key, usage: tokens.normalized, modes: state.modes, at: timestamp, emit: emit)

        case .turnEnded:
            state.setPhase(.idle, at: timestamp)
            pendingStartMs = nil
            streaming = false

        case .other:
            break
        }
    }

    private mutating func markInput(at timestamp: Int64) {
        guard !streaming else { return }
        pendingStartMs = timestamp
        state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)
    }

    private mutating func finishWithoutUsage(at timestamp: Int64) {
        pendingStartMs = timestamp
        streaming = false
        state.setPhase(.tools, at: timestamp)
    }

    /// `modes`: a snapshot of the context, plus the response's own request facts.
    private mutating func complete(key: String, usage: TokenUsage, modes: ExecutionModes, at timestamp: Int64,
                                   emit: (ResponseUpsert) -> Void) {
        let record = ResponseRecord(
            key: key,
            source: .codex,
            agentId: agentId,
            model: state.model,
            startMs: pendingStartMs,
            endMs: timestamp,
            usage: usage,
            provider: provider,
            modes: modes,
            firstMs: streaming ? firstOutputMs : nil
        )
        finishWithoutUsage(at: timestamp)
        emit(ResponseUpsert(record: record, isCompletion: true))
    }
}
