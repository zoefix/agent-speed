import Foundation

/// Per-file Claude Code / Claude Desktop state machine (METRICS 4.1), which also reads Qoder CLI's transcripts with
/// the differences of METRICS 4.19.
struct ClaudeParser: Sendable {
    enum Variant: Sendable {
        case claude
        /// Qoder CLI: source `qoderCli`, its own keys, no response without usage, no `api-request`, execution modes or
        /// actions.
        case qoderCli
    }

    static let syntheticModel = "<synthetic>"
    static let qoderKeyPrefix = "qodercli:"
    /// Repeated lines of one message are adjacent, so a short memory of recent keys is enough to merge them.
    static let mergeMemory = 1024

    let agentId: String
    let fileID: FileID
    let variant: Variant
    private(set) var state: AgentState

    private var sourceDecided = false
    private var pendingStartMs: Int64?
    private var preciseStart = false
    private var responses: [String: ResponseRecord] = [:]
    private var responseOrder: [String] = []
    /// Qoder CLI keys this file claimed with a line that had no usage (no response yet).
    private var silentKeys: Set<String> = []
    /// Ultracode (EXECUTION-MODES, Claude Code): on for the session between enter and exit markers, and for one turn
    /// after a workflow keyword until the next prompt.
    private var ultraSession = false
    private var ultraTurn = false
    /// Facts of the latest accepted response and the latest recorded effort of an accepted line.
    private var latestModes: ExecutionModes = .empty
    private var lineEffort: ExecutionModeFact?

    init(agentId: String, fileID: FileID, variant: Variant = .claude) {
        self.agentId = agentId
        self.fileID = fileID
        self.variant = variant
        let components = agentId.split(separator: "/", omittingEmptySubsequences: true)
        state = AgentState(source: variant == .qoderCli ? .qoderCli : .claudeCode,
                           isSubagent: components.dropLast().contains("subagents"))
        // Every Qoder CLI file is `qoderCli`; `entrypoint` is not used (METRICS 4.19).
        sourceDecided = variant == .qoderCli
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
        if variant == .qoderCli {
            consumeQoder(line, at: timestamp, deduper: &deduper, emit: emit)
            return
        }
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
        for old in evicted {
            responses[old] = nil
            silentKeys.remove(old)
        }
        responseOrder.removeFirst(evicted.count)
    }

    // MARK: - Qoder CLI (METRICS 4.19)

    /// 4.1's line table without `api-request` (any other line only updates `lastLineMs`), and without actions.
    private mutating func consumeQoder(_ line: ClaudeLine, at timestamp: Int64, deduper: inout Deduper,
                                       emit: (ResponseUpsert) -> Void) {
        switch line.type {
        case .user:
            if line.isInterrupt || line.toolEndsTurn {
                state.setPhase(.idle, at: timestamp)
                pendingStartMs = nil
                preciseStart = false
            } else {
                if !preciseStart { pendingStartMs = timestamp }
                state.setPhase(.generating, at: timestamp, inFlightStartMs: pendingStartMs)
            }
        case .assistant:
            consumeQoderAssistant(line, at: timestamp, deduper: &deduper, emit: emit)
        case .apiRequest, .attachment, .other:
            break
        }
    }

    /// Claude's tokens except `input`: Qoder's `input_tokens` already includes the cache when it covers it. Nil when
    /// the line has no usage, or its input and output are both 0.
    static func qoderUsage(_ line: ClaudeLine) -> TokenUsage? {
        var usage = line.usage
        if line.inputTokens >= usage.cachedInput + usage.cacheWriteInput { usage.input = line.inputTokens }
        return usage.input == 0 && usage.output == 0 ? nil : usage
    }

    /// An assistant line: keyed `qodercli:` + `message.id`, else `uuid`. Without usage it creates no response but claims
    /// its key, sets the model and consumes the pending start; `firstMs` null, `fast` false, no execution modes.
    private mutating func consumeQoderAssistant(_ line: ClaudeLine, at timestamp: Int64, deduper: inout Deduper,
                                                emit: (ResponseUpsert) -> Void) {
        guard line.model != Self.syntheticModel, let raw = line.messageId ?? line.uuid else { return }
        let key = Self.qoderKeyPrefix + raw
        let claim = deduper.claim(key, for: fileID)
        // Another file's message is copied history (METRICS 3.5).
        guard claim != .otherFile else { return }
        let hasStopReason = line.stopReason != .none
        let start = pendingStartMs
        var record = claim == .sameFile ? responses[key] : nil
        if let usage = Self.qoderUsage(line) {
            let next: ResponseRecord
            let isCompletion: Bool
            if var existing = record {
                existing.endMs = max(existing.endMs, timestamp)
                existing.usage = existing.usage.merged(with: usage)
                if let model = line.model { existing.model = model }
                existing.isFinal = existing.isFinal || hasStopReason
                next = existing
                isCompletion = false
            } else {
                next = ResponseRecord(key: key, source: .qoderCli, agentId: agentId, model: line.model,
                                      startMs: pendingStartMs, endMs: timestamp, usage: usage, isFinal: hasStopReason)
                let wasSilent = silentKeys.remove(key) != nil
                isCompletion = claim == .new || wasSilent
                pendingStartMs = nil
                preciseStart = false
                if !wasSilent { remember(key) }
            }
            responses[key] = next
            record = next
            if let model = next.model { state.model = model }
            emit(ResponseUpsert(record: next, isCompletion: isCompletion))
        } else {
            if var existing = record, hasStopReason, !existing.isFinal {
                existing.isFinal = true
                responses[key] = existing
                emit(ResponseUpsert(record: existing, isCompletion: false))
            }
            pendingStartMs = nil
            preciseStart = false
            if let model = line.model { state.model = model }
            if claim == .new {
                silentKeys.insert(key)
                remember(key)
            }
        }
        switch line.stopReason {
        case .toolUse:
            state.setPhase(.tools, at: timestamp)
        case .none where line.hasToolUse:
            state.setPhase(.tools, at: timestamp)
        case .none, .pauseTurn:
            let begin = record.map(\.startMs) ?? start
            state.setPhase(.generating, at: timestamp, inFlightStartMs: begin ?? timestamp)
        case .other:
            state.setPhase(.idle, at: timestamp)
        }
    }
}
