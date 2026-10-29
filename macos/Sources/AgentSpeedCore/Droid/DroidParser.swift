import Foundation

/// Per-session Factory Droid state machine (METRICS 4.16): calls, their timing, model and effort come from the
/// transcript; their tokens from the growth of the sidecar's own counters, allocated to the calls written since.
///
/// A read cycle feeds the transcript's new lines (`consume`), then calls `finishReadCycle`, which reads the sidecar
/// when it changed and allocates the growth waiting in `pending` to the open calls, emitting them.
struct DroidParser: Sendable {
    let agentId: String
    let fileID: FileID
    private(set) var state: AgentState

    /// A call whose tokens are not allocated yet.
    private struct OpenCall: Sendable {
        var key: String
        var startMs: Int64?
        var endMs: Int64
        var modelId: String?
        var modes: ExecutionModes
        var offset: Int64
        /// Offset of the latest `compaction_state` before the call; nil: where reading started.
        var base: Int64?
        var outputWeight: Int64
        /// The key was new to the dedupe set when the line was read: its first emission is a completion.
        var isNew: Bool
    }

    private var headerSeen = false
    /// The fork's or successor's parent session id, whose counters this session's started from.
    private var parent: String?
    private var pendingStartMs: Int64?
    private var compactionOffset: Int64?
    /// Where reading began: 0 from the start, the file's end for a session older than the startup window.
    private var readStart: Int64 = 0
    private var seen: Set<String> = []
    private var open: [OpenCall] = []
    /// H: the own counters already accounted (a high-water mark).
    private(set) var mark = DroidCounters.zero
    /// P: growth not yet allocated.
    private(set) var pending = DroidCounters.zero
    private var sessionModel: String?
    /// The latest call's `modelId`.
    private var callModel: String?
    /// The sidecar version last read successfully; nil before that or after an unreadable read.
    private var readSignature: DroidPaths.Signature?
    /// The sidecar version last looked at, readable or not: a different one starts a read cycle.
    private var observedSignature: DroidPaths.Signature?
    /// The parent sidecar version the last attempt found unreadable; unreadable again with it, the parent gives 0.
    var unreadableParent: DroidPaths.Signature?
    /// The last sidecar read could not tell the own counters (the parent sidecar was unreadable); nothing changed.
    private(set) var ownCountersUnknown = false
    /// The transcript started at its end: the first read with known own counters only sets `H`.
    private var baselinePending = false

    init(agentId: String, fileID: FileID) {
        self.agentId = agentId
        self.fileID = fileID
        state = AgentState(source: .droid)
    }

    var sidecarPath: String { DroidPaths.sidecarPath(ofTranscript: agentId) }

    /// The sidecar is not the version last looked at (new, rewritten, replaced or removed), or the last read could not
    /// tell the own counters: either way the next read cycle reads it again.
    var sidecarHasChanged: Bool { ownCountersUnknown || DroidPaths.signature(sidecarPath) != observedSignature }

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

    /// Where the reader began; weights before the first compaction count from here.
    mutating func noteReadStart(_ offset: Int64?) {
        if let offset { readStart = offset }
    }

    // MARK: - Transcript lines

    /// `tools`: the line's tool calls and results (METRICS 6.10).
    mutating func consume(_ line: DroidLineRead, tools: [ToolEvent] = [], deduper: inout Deduper) {
        switch line.extraction {
        case .blank:
            return
        case .malformed:
            state.malformedLines += 1
        case .line(let decoded):
            consume(decoded, offset: line.offset, length: line.length, tools: tools, deduper: &deduper)
        }
    }

    private mutating func consume(_ line: DroidLine, offset: Int64, length: Int, tools: [ToolEvent],
                                  deduper: inout Deduper) {
        switch line {
        case .sessionStart(let cwd, let parent, let callingSessionId):
            guard !headerSeen else { return }
            headerSeen = true
            state.project = cwd.flatMap(OmpParser.basename)
            state.cwdPath = cwd.flatMap { $0.isEmpty ? nil : $0 }
            self.parent = parent
            if let callingSessionId {
                state.isSubagent = true
                state.thread = SessionThread(id: nil, parentId: DroidPaths.sibling(callingSessionId, of: agentId), createdMs: 0)
            }
        case .turnOutcome:
            state.setPhase(.idle, at: state.lastLineMs ?? 0)
            pendingStartMs = nil
        case .ignored:
            return
        case .timed(let timestamp, let record):
            state.noteLine(at: timestamp)
            switch record {
            case .input:
                state.actions.recordResults(tools, at: timestamp)
                pendingStartMs = timestamp
                state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)
            case .compaction:
                compactionOffset = offset
                if state.phase == .generating {
                    // The summary call ended; the next request follows.
                    pendingStartMs = timestamp
                    state.setPhase(.generating, at: timestamp, inFlightStartMs: timestamp)
                }
            case .assistant(let assistant):
                // Another file's message (a fork's copy) adds no actions (METRICS 6.10).
                let accepted = assistant.id.map { !deduper.isOwnedByOtherFile("droid:" + $0, for: fileID) } ?? false
                call(assistant, at: timestamp, offset: offset, length: length, deduper: &deduper)
                if accepted { state.actions.recordCalls(tools, at: timestamp, model: state.model) }
            case .notice:
                state.actions.recordResults(tools, at: timestamp)
            case .other:
                break
            }
        }
    }

    private mutating func call(_ assistant: DroidAssistant, at timestamp: Int64, offset: Int64, length: Int,
                               deduper: inout Deduper) {
        guard let id = assistant.id else { return }
        let key = "droid:" + id
        // Forks copy their parent's messages with their ids; a repeated line changes nothing.
        guard !seen.contains(key), !deduper.isOwnedByOtherFile(key, for: fileID) else { return }
        let claim = deduper.claim(key, for: fileID)
        seen.insert(key)
        // Each call replaces (or clears) the effort; its response keeps a snapshot of the context (EXECUTION-MODES).
        var modes = ExecutionModes(state.modes.facts.filter { $0.kind != .effort })
        if let effort = assistant.effort { modes.insert(effort) }
        state.modes = modes
        open.append(OpenCall(
            key: key, startMs: pendingStartMs, endMs: timestamp, modelId: assistant.modelId, modes: modes,
            offset: offset, base: compactionOffset, outputWeight: min(Int64(length), DroidAllocation.maxWeight),
            isNew: claim == .new))
        callModel = assistant.modelId
        state.model = assistant.modelId ?? sessionModel
        pendingStartMs = nil
        state.setPhase(assistant.asksForTools ? .tools : .idle, at: timestamp)
    }

    // MARK: - Read cycle

    /// Steps 2 and 3 of a read cycle: the sidecar when it changed, then the growth waiting in `pending` to the open
    /// calls. Returns true when the sidecar was read or calls were emitted.
    mutating func finishReadCycle(emit: (ResponseUpsert) -> Void) -> Bool {
        let path = sidecarPath
        let changed = readSidecar(signature: DroidPaths.signature(path)) { DroidSidecar.read(path) }
        return allocate(emit: emit) || changed
    }

    /// A session whose transcript starts at its end takes `H` from its sidecar, without adding to `pending`: only later
    /// growth counts. While the own counters cannot be told, `H` waits for the first read cycle that can.
    mutating func takeBaseline(_ snapshot: DroidSidecar.Snapshot) {
        baselinePending = true
        _ = readSidecar(signature: snapshot.signature) { snapshot.content }
    }

    /// History only, after the whole transcript and one sidecar read: own counters left without a call become one
    /// response of the session (METRICS 4.16 Startup and history).
    mutating func finishHistory(emit: (ResponseUpsert) -> Void) {
        guard !pending.isZero, let last = state.lastLineMs else { return }
        let key = "droid:" + DroidPaths.sessionId(ofTranscript: agentId) + ":session"
        emit(ResponseUpsert(record: record(key: key, startMs: nil, endMs: last, share: pending, model: sessionModel,
                                           modes: .empty), isCompletion: true))
        pending = .zero
    }

    /// Step 2 of a read cycle: reads the sidecar when it is not the version last read. An unreadable sidecar, or own
    /// counters that cannot be told, change nothing and are tried again at the next read cycle.
    private mutating func readSidecar(signature: DroidPaths.Signature?, content read: () -> DroidSidecar.Content?) -> Bool {
        observedSignature = signature
        ownCountersUnknown = false
        guard let signature else {
            // No sidecar: an old session's baseline stays 0.
            baselinePending = false
            return false
        }
        guard signature != readSignature else { return false }
        guard let content = read() else {
            // Unreadable (droid may be replacing it).
            readSignature = nil
            return false
        }
        var own: DroidCounters?
        if let counters = content.counters {
            guard let known = ownCounters(counters) else {
                readSignature = nil
                ownCountersUnknown = true
                return false
            }
            own = known
        }
        readSignature = signature
        sessionModel = content.model
        state.model = callModel ?? sessionModel
        let baseline = baselinePending
        baselinePending = false
        guard let own else { return true }
        for field in DroidCounters.fields {
            if !baseline { pending[keyPath: field] += max(0, own[keyPath: field] - mark[keyPath: field]) }
            mark[keyPath: field] = max(mark[keyPath: field], own[keyPath: field])
        }
        return true
    }

    /// A fork or successor started from its parent's counters, which are removed; thinking is capped at the output.
    /// Nil while the parent's counters cannot be told.
    private mutating func ownCounters(_ counters: DroidCounters) -> DroidCounters? {
        guard let inherited = parentCounters() else { return nil }
        var own = counters.subtracting(inherited)
        own.thinking = min(own.thinking, own.output)
        return own
    }

    /// The parent's counters: 0 without a parent, without its sidecar or without counters there; nil when its sidecar
    /// exists but is unreadable (droid may be replacing it), unless the last attempt found it unreadable with the same
    /// signature: then it will not mend, and gives 0.
    private mutating func parentCounters() -> DroidCounters? {
        guard let parent else { return .zero }
        let path = DroidPaths.sidecarPath(ofTranscript: DroidPaths.sibling(parent, of: agentId))
        guard let signature = DroidPaths.signature(path) else {
            unreadableParent = nil
            return .zero
        }
        if let content = DroidSidecar.read(path) {
            unreadableParent = nil
            return content.counters ?? .zero
        }
        if signature == unreadableParent { return .zero }
        unreadableParent = signature
        return nil
    }

    private mutating func allocate(emit: (ResponseUpsert) -> Void) -> Bool {
        guard !open.isEmpty, !pending.isZero else { return false }
        let inputWeights = open.map { call in
            min(DroidAllocation.contextBase + call.offset - (call.base ?? readStart), DroidAllocation.maxWeight)
        }
        let shares = DroidAllocation.allocate(pending, inputWeights: inputWeights, outputWeights: open.map(\.outputWeight))
        for (call, share) in zip(open, shares) {
            let record = record(key: call.key, startMs: call.startMs, endMs: call.endMs, share: share,
                                model: call.modelId ?? sessionModel, modes: call.modes)
            emit(ResponseUpsert(record: record, isCompletion: call.isNew))
        }
        open.removeAll()
        pending = .zero
        return true
    }

    private func record(key: String, startMs: Int64?, endMs: Int64, share: DroidCounters, model: String?,
                        modes: ExecutionModes) -> ResponseRecord {
        ResponseRecord(key: key, source: .droid, agentId: agentId, model: model, startMs: startMs, endMs: endMs,
                       usage: share.tokens, isFinal: true, provider: model.flatMap { Self.provider(ofModel: $0) }, modes: modes)
    }

    /// `custom` for the user's own models, `custom:<name>-<n>` (ASCII case-insensitive); droid names no provider.
    static func provider(ofModel model: String) -> String? {
        let prefix = "custom:".utf8
        let head = model.utf8.prefix(prefix.count)
        guard head.count == prefix.count,
              head.elementsEqual(prefix, by: { ($0 >= 0x41 && $0 <= 0x5A ? $0 + 32 : $0) == $1 }) else { return nil }
        return "custom"
    }
}
