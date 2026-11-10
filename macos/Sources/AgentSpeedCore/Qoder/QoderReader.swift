import Foundation

/// The live state of one Qoder IDE database (METRICS 4.18): its chat sessions as agents, read incrementally by `rowid`.
///
/// Not thread-safe: owned by `QoderSource` on the session queue.
final class QoderReader {
    /// An assistant row that set `tools` and is still its session's latest row: its turn record may finish it.
    struct Awaiting: Equatable {
        var request: String
        var rowMs: Int64
    }

    struct SessionState: Equatable {
        var agent = AgentState(source: .qoder)
        /// The time of the row that last set the phase.
        var phaseRowMs: Int64?
        /// The time of the latest row read (`.some(nil)`: that row has no time); nil before the first.
        var previousRowMs: Int64??
        var awaiting: Awaiting?
    }

    let path: String
    let fileID: FileID
    private let claims: SessionClaims
    private let owner: Int
    private(set) var status: OpencodeSource.Status = .missing
    private var stat: FileStat?
    private var walStat: FileStat?
    private var cursor: Int64?
    private var needsSeed = true
    private(set) var sessions: [String: QoderDatabase.Session] = [:]
    /// Sessions another database of the list reads.
    private var foreign: Set<String> = []
    private(set) var states: [String: SessionState] = [:]
    private var emitted: [String: ResponseRecord] = [:]

    private static let emittedLimit = 4096
    private static let emittedWindowMs: Int64 = 2 * 3_600_000
    /// A turn record is looked for while its assistant row is this recent (later, the agent shows idle anyway).
    private static let awaitingWindowMs: Int64 = 15 * 60_000

    init(path: String, fileID: FileID, claims: SessionClaims, owner: Int) {
        self.path = path
        self.fileID = fileID
        self.claims = claims
        self.owner = owner
    }

    var exists: Bool { status != .missing }
    var watchedDirectory: String? { exists ? (path as NSString).deletingLastPathComponent : nil }
    var databaseBytes: Int64 { (FileStat.lstat(path)?.size ?? 0) + (FileStat.lstat(path + "-wal")?.size ?? 0) }
    var malformedRows: Int { states.values.reduce(0) { $0 + $1.agent.malformedLines } }

    func agentId(_ session: String) -> String { path + "#" + session }

    func matches(canonicalPath changed: String) -> Bool {
        let canonical = LocationPaths.resolved(path)
        return changed == canonical || changed == canonical + "-wal" || changed == path || changed == path + "-wal"
    }

    /// Today's sessions: sessions with a row today, sub-agents included (METRICS 4.18).
    func activeSessionCount(todayStartMs: Int64) -> Int {
        states.values.filter { ($0.agent.lastLineMs ?? .min) >= todayStartMs }.count
    }

    var agentIds: [String] { states.keys.map(agentId) }

    /// Sub-agent → parent, for sessions whose parent session is an agent of this database (METRICS 6.7).
    var parentLinks: [String: String] {
        var links: [String: String] = [:]
        for id in states.keys {
            guard let parent = sessions[id]?.parentId, parent != id, states[parent] != nil else { continue }
            links[agentId(id)] = agentId(parent)
        }
        return links
    }

    func release() {
        claims.releaseAll(by: owner)
    }

    /// Reads what changed: the startup window when the database is new or was replaced (`seeded`), else the rows
    /// above the highest `rowid` seen and the turn records of agents waiting for one. Nil when nothing changed.
    func refresh(todayStartMs: Int64, deduper: inout Deduper, force: Bool = false) -> QoderSource.Update? {
        let current = FileStat.lstat(path)
        let wal = FileStat.lstat(path + "-wal")
        guard current?.isRegularFile == true else {
            guard exists else { return nil }
            status = .missing
            var update = QoderSource.Update()
            update.removedAgents = agentIds
            reset()
            return update
        }
        let replaced = stat.map { $0.identity != current?.identity || (current?.size ?? 0) < $0.size } ?? false
        guard force || needsSeed || replaced || status != .ready || current != stat || wal != walStat else { return nil }
        var update = QoderSource.Update()
        if replaced {
            update.removedAgents = agentIds
            reset()
        }
        do {
            let database = try QoderDatabase(path: path)
            let seeding = needsSeed
            let read = try database.snapshot { try self.read(database, seeding: seeding, todayStartMs: todayStartMs) }
            status = .ready
            needsSeed = false
            for row in read.rows {
                consume(row, previous: read.previous, finished: read.finished, seeded: seeding, deduper: &deduper,
                        into: &update)
            }
            finishAwaited(read.finished, into: &update)
            cursor = max(cursor ?? 0, read.top, read.rows.last?.rowId ?? 0)
            if emitted.count > Self.emittedLimit, let newest = read.rows.last?.timeMs {
                emitted = emitted.filter { $0.value.endMs >= newest - Self.emittedWindowMs }
            }
            stat = current
            walStat = wal
        } catch let error as SQLiteError {
            status = error.code & 0xFF == 1 ? .unsupported : .unreadable
        } catch QoderDatabase.OpenError.unsupported {
            status = .unsupported
        } catch {
            status = .unreadable
        }
        return update
    }

    private struct Read {
        var rows: [QoderDatabase.Row] = []
        var top: Int64 = 0
        /// The time of the row just below the first row read of each session read for the first time.
        var previous: [String: Int64??] = [:]
        var finished: [String: Int64] = [:]
    }

    /// One snapshot's reads: every session at startup (each claimed unless an earlier database has it), else the
    /// sessions of new rows not known yet; the rows; the previous row of sessions read for the first time; the turn
    /// records of their assistant rows and of the waiting agents.
    private func read(_ database: QoderDatabase, seeding: Bool, todayStartMs: Int64) throws -> Read {
        var read = Read()
        read.rows = seeding
            ? try database.recentRows(since: todayStartMs - TranscriptSession.startupLookbackMs)
            : try database.rows(after: cursor ?? 0)
        read.top = try database.maxRowId()
        if seeding {
            claimSessions(try database.sessions())
        } else {
            let unknown = Set(read.rows.compactMap(\.sessionId)).subtracting(sessions.keys).subtracting(foreign)
            if !unknown.isEmpty { claimSessions(try database.sessions(ids: unknown.sorted())) }
        }
        var firstRows: [String: Int64] = [:]
        var requests: Set<String> = []
        for row in read.rows {
            guard let session = row.sessionId, sessions[session] != nil else { continue }
            if states[session] == nil, firstRows[session] == nil { firstRows[session] = row.rowId }
            if row.role == .assistant, let request = row.requestId { requests.insert(request) }
        }
        for (session, rowId) in firstRows {
            read.previous[session] = try database.previousRowTime(session: session, before: rowId)
        }
        let newest = read.rows.compactMap(\.timeMs).max()
        let since = newest.map { $0.subtractingReportingOverflow(Self.awaitingWindowMs) }.map { $0.overflow ? .min : $0.partialValue }
        for state in states.values {
            guard let awaiting = state.awaiting, awaiting.rowMs >= since ?? .min else { continue }
            requests.insert(awaiting.request)
        }
        read.finished = try database.finishedAt(requests: requests.sorted())
        return read
    }

    private func reset() {
        release()
        sessions = [:]
        foreign = []
        states = [:]
        emitted = [:]
        cursor = nil
        stat = nil
        walStat = nil
        needsSeed = true
    }

    private func claimSessions(_ listed: [QoderDatabase.Session]) {
        for session in listed where sessions[session.id] == nil && !foreign.contains(session.id) {
            if claims.claim(session.id, by: owner) {
                sessions[session.id] = session
            } else {
                foreign.insert(session.id)
            }
        }
    }

    private func freshState(_ session: QoderDatabase.Session, previous: Int64??) -> SessionState {
        var state = SessionState()
        state.agent.isSubagent = session.parentId != nil
        state.agent.cwdPath = session.projectPath
        state.agent.project = session.projectPath.flatMap(DevinPaths.project(ofWorkingDirectory:)) ?? session.id
        state.previousRowMs = previous
        return state
    }

    private func consume(_ row: QoderDatabase.Row, previous: [String: Int64??], finished: [String: Int64], seeded: Bool,
                         deduper: inout Deduper, into update: inout QoderSource.Update) {
        guard let sessionId = row.sessionId, let session = sessions[sessionId] else { return }
        let id = agentId(sessionId)
        var state = states[sessionId] ?? freshState(session, previous: previous[sessionId] ?? nil)
        defer {
            states[sessionId] = state
            update.agents[id] = state.agent
        }
        let start = state.previousRowMs ?? nil
        state.previousRowMs = .some(row.timeMs)
        state.awaiting = nil
        if let time = row.timeMs { state.agent.noteLine(at: time) }
        guard let time = row.timeMs, let role = row.role else {
            state.agent.malformedLines += 1
            return
        }
        switch role {
        case .assistant:
            if let tokens = row.tokens,
               let record = QoderCalls.record(tokens: tokens, session: sessionId, messageId: row.messageId, rowId: row.rowId,
                                              startMs: start, endMs: time, model: row.model, agentId: id),
               deduper.claim(record.key, for: fileID) != .otherFile {
                if let model = record.model { state.agent.model = model }
                if emitted[record.key] != record {
                    let isCompletion = emitted[record.key] == nil
                    emitted[record.key] = record
                    update.upserts.append(.init(upsert: ResponseUpsert(record: record, isCompletion: isCompletion),
                                                project: state.agent.project ?? sessionId, seeded: seeded))
                }
            }
            guard time >= (state.phaseRowMs ?? .min) else { return }
            state.phaseRowMs = time
            if let request = row.requestId, let done = finished[request], done >= time {
                state.agent.setPhase(.idle, at: time)
            } else {
                state.agent.setPhase(.tools, at: time)
                state.awaiting = row.requestId.map { Awaiting(request: $0, rowMs: time) }
            }
        case .user, .tool:
            guard time >= (state.phaseRowMs ?? .min) else { return }
            state.phaseRowMs = time
            state.agent.setPhase(.generating, at: time, inFlightStartMs: time)
        case .other:
            break
        }
    }

    /// A waiting agent whose turn record now says its request finished goes idle at `finishedAt`.
    private func finishAwaited(_ finished: [String: Int64], into update: inout QoderSource.Update) {
        for (sessionId, state) in states {
            guard let awaiting = state.awaiting, let done = finished[awaiting.request], done >= awaiting.rowMs else { continue }
            var next = state
            next.awaiting = nil
            next.agent.setPhase(.idle, at: done)
            states[sessionId] = next
            update.agents[agentId(sessionId)] = next.agent
        }
    }
}
