import Foundation

/// Per-file Claude Code / Claude Desktop state machine (METRICS 4.1).
struct ClaudeParser: Sendable {
    static let syntheticModel = "<synthetic>"
    /// Repeated lines of one message are adjacent, so a short memory of recent keys is enough to merge them.
    static let mergeMemory = 1024

    let agentId: String
    let fileID: FileID
    private(set) var state: AgentState

    private var sourceDecided = false
    private var pendingStartMs: Int64?
    private var preciseStart = false
    private var responses: [String: ResponseRecord] = [:]
    private var responseOrder: [String] = []
    /// Ultracode (EXECUTION-MODES, Claude Code): on for the session between enter and exit markers, and for one turn
    /// after a workflow keyword until the next prompt.
    private var ultraSession = false
    private var ultraTurn = false
    /// Facts of the latest accepted response and the latest recorded effort of an accepted line.
    private var latestModes: ExecutionModes = .empty
    private var lineEffort: ExecutionModeFact?

    init(agentId: String, fileID: FileID) {
        self.agentId = agentId
        self.fileID = fileID
        let components = agentId.split(separator: "/", omittingEmptySubsequences: true)
        state = AgentState(source: .claudeCode, isSubagent: components.dropLast().contains("subagents"))
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
        _ extraction: LineExtraction<ClaudeLine>,
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

    private mutating func consume(_ line: ClaudeLine, tools: [ToolEvent], deduper: inout Deduper, emit: (ResponseUpsert) -> Void) {
        guard let timestamp = line.timestampMs else { return }
        state.noteLine(at: timestamp)
        if !sourceDecided, let source = line.entrypointSource {
            state.source = source
            sourceDecided = true
        }
        if let cwd = line.cwd { state.setProject(fromWorkingDirectory: cwd) }
        if line.isSidechain { state.isSubagent = true }
        let ultracode = ultraSession || ultraTurn
        switch line.attachment {
        case .ultraEffortEnter?: ultraSession = true
        case .ultraEffortExit?: ultraSession = false
        case .workflowKeywordRequest?: ultraTurn = true
        case nil: break
        }
        if line.isPrompt { ultraTurn = false }
        if (ultraSession || ultraTurn) != ultracode { state.modes = currentModes }

        switch line.type {
        case .apiRequest:
            pendingStartMs = timestamp
            preciseStart = true
            state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)

        case .user:
            state.actions.recordResults(tools, at: timestamp)
            if line.isInterrupt || line.toolEndsTurn {
                state.setPhase(.idle, at: timestamp)
                pendingStartMs = nil
                preciseStart = false
            } else {
                if !preciseStart { pendingStartMs = timestamp }
                state.setPhase(.generating, at: timestamp, inFlightStartMs: pendingStartMs)
            }

        case .assistant:
            consumeAssistant(line, tools: tools, at: timestamp, deduper: &deduper, emit: emit)

        case .attachment, .other:
            break
        }
    }

    private var ultracodeFact: ExecutionModeFact? {
        guard ultraSession || ultraTurn else { return nil }
        return ExecutionModeAdapters.effort("ultracode", evidence: .recorded, scope: .request)
    }

    /// The latest response's facts with the effort of now: ultracode while it is on, else the latest recorded one.
    private var currentModes: ExecutionModes {
        var modes = ExecutionModes(latestModes.facts.filter { $0.kind != .effort })
        if let effort = ultracodeFact ?? lineEffort { modes.insert(effort) }
        return modes
    }

    private mutating func consumeAssistant(
        _ line: ClaudeLine,
        tools: [ToolEvent],
        at timestamp: Int64,
        deduper: inout Deduper,
        emit: (ResponseUpsert) -> Void
    ) {
        guard line.model != Self.syntheticModel else { return }
        guard let key = line.messageId ?? line.requestId ?? line.uuid else { return }
        let claim = deduper.claim(key, for: fileID)
        // Another file's message is copied history (METRICS 3.5): no response and no actions.
        guard claim != .otherFile else { return }

        let hasStopReason = line.stopReason != .none
        var lineModes = line.modes
        if let effort = ultracodeFact ?? line.effort { lineModes.insert(effort) }
        let record: ResponseRecord
        if claim == .sameFile, var existing = responses[key] {
            existing.endMs = max(existing.endMs, timestamp)
            existing.firstMs = min(existing.firstMs ?? timestamp, timestamp)
            existing.usage = existing.usage.merged(with: line.usage)
            if let model = line.model { existing.model = model }
            existing.fast = existing.fast || line.fast
            existing.modes = existing.modes.merging(lineModes)
            existing.isFinal = existing.isFinal || hasStopReason
            record = existing
        } else {
            record = ResponseRecord(
                key: key,
                source: state.source,
                agentId: agentId,
                model: line.model,
                startMs: pendingStartMs,
                endMs: timestamp,
                usage: line.usage,
                isFinal: hasStopReason,
                fast: line.fast,
                modes: lineModes,
                firstMs: timestamp
            )
            pendingStartMs = nil
            preciseStart = false
            remember(key)
        }
        responses[key] = record
        latestModes = record.modes
        lineEffort = line.effort ?? lineEffort
        state.modes = currentModes
        if let model = line.model { state.model = model }
        // The calls of this message, with the model that issued them (METRICS 6.10).
        state.actions.recordCalls(tools, at: timestamp, model: state.model)

        switch line.stopReason {
        case .toolUse:
            state.setPhase(.tools, at: timestamp)
        case .none where line.hasToolUse:
            state.setPhase(.tools, at: timestamp)
        case .none, .pauseTurn:
            state.setPhase(.generating, at: timestamp, inFlightStartMs: record.startMs ?? timestamp)
        case .other:
            state.setPhase(.idle, at: timestamp)
        }
        emit(ResponseUpsert(record: record, isCompletion: claim == .new))
    }

    private mutating func remember(_ key: String) {
        responseOrder.append(key)
        guard responseOrder.count > Self.mergeMemory * 2 else { return }
        let evicted = responseOrder.prefix(responseOrder.count - Self.mergeMemory)
        for old in evicted { responses[old] = nil }
        responseOrder.removeFirst(evicted.count)
    }
}
