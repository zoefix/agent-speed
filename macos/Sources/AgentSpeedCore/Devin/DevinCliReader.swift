import Foundation

/// The live state of one Devin CLI database (METRICS 4.17): its sessions as agents, read incrementally by `row_id`.
///
/// Not thread-safe: owned by `DevinSource` on the session queue.
final class DevinCliReader {
    /// One session's agent, with the time of the row that last set its phase and the model of its latest call.
    struct SessionState: Equatable {
        var agent = AgentState(source: .devin)
        var phaseRowMs: Int64?
        var callModel: String??
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
    private(set) var sessions: [String: DevinDatabase.Session] = [:]
    private(set) var states: [String: SessionState] = [:]
    private var emitted: [String: ResponseRecord] = [:]

    /// Copies of a call are written together, so older emitted calls are forgotten once there are many.
    private static let emittedLimit = 4096
    private static let emittedWindowMs: Int64 = 2 * 3_600_000

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

    func activeSessionCount(todayStartMs: Int64) -> Int {
        states.values.filter { ($0.agent.lastLineMs ?? .min) >= todayStartMs }.count
    }

    /// Every agent id it holds, for removal.
    var agentIds: [String] { states.keys.map(agentId) }

    func release() {
        claims.releaseAll(by: owner)
    }

    /// Reads what changed: the startup window when the database is new or was replaced (`seeded`), else the rows
    /// above the highest `row_id` seen. Nil when nothing changed.
    func refresh(todayStartMs: Int64, deduper: inout Deduper, force: Bool = false) -> DevinSource.Update? {
        let current = FileStat.lstat(path)
        let wal = FileStat.lstat(path + "-wal")
        guard current?.isRegularFile == true else {
            guard exists else { return nil }
            status = .missing
            var update = DevinSource.Update()
            update.removedAgents = agentIds
            reset()
            return update
        }
        let replaced = stat.map { $0.identity != current?.identity || (current?.size ?? 0) < $0.size } ?? false
        guard force || needsSeed || replaced || status != .ready || current != stat || wal != walStat else { return nil }
        var update = DevinSource.Update()
        if replaced {
            update.removedAgents = agentIds
            reset()
        }
        do {
            let database = try DevinDatabase(path: path)
            let seeding = needsSeed
            let (sessionRows, rows, top) = try database.snapshot { () throws -> ([DevinDatabase.Session], [DevinDatabase.Row], Int64) in
                let sessionRows = try database.sessions()
                let rows = seeding
                    ? try database.recentRows(since: todayStartMs - TranscriptSession.startupLookbackMs)
                    : try database.rows(after: cursor ?? 0)
                return (sessionRows, rows, try database.maxRowId())
            }
            status = .ready
            needsSeed = false
            readSessions(sessionRows, into: &update)
            for row in rows { consume(row, seeded: seeding, deduper: &deduper, into: &update) }
            cursor = max(cursor ?? 0, top, rows.last?.rowId ?? 0)
            if emitted.count > Self.emittedLimit, let newest = rows.last?.timeMs {
                emitted = emitted.filter { $0.value.endMs >= newest - Self.emittedWindowMs }
            }
            stat = current
            walStat = wal
        } catch let error as SQLiteError {
            status = error.code & 0xFF == 1 ? .unsupported : .unreadable
        } catch {
            status = .unreadable
        }
        return update
    }

    private func reset() {
        release()
        sessions = [:]
        states = [:]
        emitted = [:]
        cursor = nil
        stat = nil
        walStat = nil
        needsSeed = true
    }

    private func readSessions(_ rows: [DevinDatabase.Session], into update: inout DevinSource.Update) {
        var next: [String: DevinDatabase.Session] = [:]
        for session in rows where claims.claim(session.id, by: owner) {
            next[session.id] = session
        }
        for id in sessions.keys where next[id] == nil { claims.release(id, by: owner) }
        for (id, session) in next where sessions[id] != session {
            guard var state = states[id] else { continue }
            apply(session, to: &state)
            states[id] = state
            update.agents[agentId(id)] = state.agent
        }
        sessions = next
    }

    /// Project and working directory from `working_directory`; a session without one is named by its id, never by the
    /// database's folder (METRICS 4.17).
    private func apply(_ session: DevinDatabase.Session, to state: inout SessionState) {
        if let directory = session.workingDirectory, !directory.isEmpty {
            state.agent.project = DevinPaths.project(ofWorkingDirectory: directory) ?? session.id
            state.agent.cwdPath = directory
        } else {
            state.agent.project = session.id
            state.agent.cwdPath = nil
        }
        state.agent.model = state.callModel ?? session.model
    }

    private func consume(_ row: DevinDatabase.Row, seeded: Bool, deduper: inout Deduper,
                         into update: inout DevinSource.Update) {
        guard let session = sessions[row.sessionId] else { return }
        let id = agentId(row.sessionId)
        var state = states[row.sessionId] ?? {
            var fresh = SessionState()
            apply(session, to: &fresh)
            return fresh
        }()
        defer {
            states[row.sessionId] = state
            update.agents[id] = state.agent
        }
        guard let time = row.timeMs, let message = row.message else {
            state.agent.malformedLines += 1
            if let time = row.timeMs { state.agent.noteLine(at: time) }
            return
        }
        state.agent.noteLine(at: time)
        if let record = DevinCalls.record(of: message, session: row.sessionId, rowId: row.rowId, endMs: time,
                                          sessionModel: session.model, agentId: id),
           deduper.claim(record.key, for: fileID) != .otherFile {
            state.callModel = .some(record.model)
            state.agent.model = record.model ?? session.model
            if emitted[record.key] != record {
                let isCompletion = emitted[record.key] == nil
                emitted[record.key] = record
                update.upserts.append(.init(upsert: ResponseUpsert(record: record, isCompletion: isCompletion),
                                            project: state.agent.project ?? row.sessionId, seeded: seeded))
            }
        }
        if let phase = message.phase, time >= (state.phaseRowMs ?? .min) {
            state.phaseRowMs = time
            state.agent.setPhase(phase, at: time, inFlightStartMs: phase == .generating ? time : nil)
        }
    }
}
