import Foundation

/// Watches one or more Trae editions as a single source; copied native turn ids are counted once.
final class TraeSource {
    struct Update {
        var upserts: [(ResponseUpsert, String)] = []
        var agents: [String: AgentState] = [:]
        var removedAgents: [String] = []
        var isEmpty: Bool { upserts.isEmpty && agents.isEmpty && removedAgents.isEmpty }
    }
    struct ReadItem: Sendable {
        var path: String
        var signature: [FileStat?]
        var changed: Bool
    }
    struct ReadJob: Sendable {
        var id: UUID
        var items: [ReadItem]
        var sinceMs: Int64?
        var isSeed: Bool
        var clock: @Sendable () -> Int64

        func perform() -> ReadResult {
            ReadResult(id: id, isSeed: isSeed, readings: items.map { item in
                guard item.signature[0]?.isRegularFile == true else { return Reading(item: item, status: .missing) }
                guard item.changed else { return Reading(item: item, status: .ready) }
                do {
                    let database = try TraeDatabase(path: item.path)
                    let rows = try database.read(sinceMs: sinceMs)
                    let activities = try database.activities(nowMs: clock())
                    return Reading(item: item, status: .ready, rows: rows, activities: activities)
                } catch let error as SQLiteError {
                    return Reading(item: item, status: error.code & 0xFF == 1 ? .unsupported : .unreadable)
                } catch TraeSnapshot.Error.authentication { return Reading(item: item, status: .unsupported)
                } catch TraeSnapshot.Error.unsupported { return Reading(item: item, status: .unsupported)
                } catch { return Reading(item: item, status: .unreadable) }
            })
        }
    }
    struct Reading: Sendable {
        var item: ReadItem
        var status: OpencodeSource.Status
        var rows: [TraeDatabase.Row] = []
        var activities: [TraeTiming.Activity] = []
    }
    struct ReadResult: Sendable {
        var id: UUID
        var isSeed: Bool
        var readings: [Reading]
    }

    /// Installed after startup by MonitorCore. The source and deduper stay on the monitor queue; only
    /// immutable read jobs leave it. At most one job is in flight, with later changes coalesced by signatures.
    var backgroundReads: ((ReadJob) -> Void)?
    private var pendingRead: UUID?
    let roots: [String]
    let fileID: FileID
    private(set) var status: OpencodeSource.Status = .missing
    private(set) var partialHistory = false
    private var signatures: [String: [FileStat?]] = [:]
    private var emitted: [String: ResponseRecord] = [:]
    private var states: [String: AgentState] = [:]
    private var sessionOwners: [String: String] = [:]
    private var sinceMs: Int64?
    private var needsRead = true
    private var lastReadNs: UInt64?
    private let clock: @Sendable () -> Int64

    init(roots: [String], fileID: FileID, clock: @escaping @Sendable () -> Int64 = { Clock.milliseconds(Date()) }) {
        self.roots = roots
        self.fileID = fileID
        self.clock = clock
    }

    var databasePaths: [String] { Array(Set(roots.map(TraeDatabase.databasePath))).sorted() }
    var existingPaths: [String] { databasePaths.filter { FileStat.lstat($0)?.isRegularFile == true } }
    var exists: Bool { !existingPaths.isEmpty }
    var isReady: Bool { status == .ready }
    var rootPath: String { roots.first { FileStat.lstat(TraeDatabase.databasePath(forRoot: $0))?.isRegularFile == true } ?? roots[0] }
    var watchedDirectories: [String] { existingPaths.map { ($0 as NSString).deletingLastPathComponent } }
    var databaseSize: Int64 { existingPaths.reduce(0) { $0 + (FileStat.databaseStat($1)?.size ?? 0) } }

    // A completed log can arrive after the database turn. Its signature upgrades the existing response in place.
    static func signature(_ path: String) -> [FileStat?] {
        [path, path + "-wal", path + "-journal"].map(FileStat.lstat) + TraeTiming.files(database: path).map { Optional($0.stat) }
    }

    func matches(canonicalPath path: String) -> Bool {
        databasePaths.contains { database in
            let canonical = URL(fileURLWithPath: database).resolvingSymlinksInPath().path
            return path == canonical || path == canonical + "-wal" || path == canonical + "-journal"
        }
    }

    @discardableResult
    func refreshAvailability() -> Bool {
        let old = status
        if !exists { status = .missing }
        else if status == .missing { needsRead = true }
        return old != status || (exists && status == .missing)
    }

    func seed(todayStartMs: Int64, deduper: inout Deduper) -> Update {
        sinceMs = todayStartMs - TranscriptSession.startupLookbackMs
        needsRead = true
        return refresh(deduper: &deduper)
    }

    func refreshIfChanged(deduper: inout Deduper) -> Update? {
        guard pendingRead == nil else { return nil }
        guard needsRead || databasePaths.contains(where: { signatures[$0] != Self.signature($0) }) else { return nil }
        return refresh(deduper: &deduper)
    }

    func refresh(deduper: inout Deduper) -> Update {
        guard pendingRead == nil else { return Update() }
        let now = DispatchTime.now().uptimeNanoseconds
        if !needsRead, let lastReadNs, now - lastReadNs < 1_000_000_000 { return Update() }
        lastReadNs = now
        let job = ReadJob(id: UUID(), items: databasePaths.map { path in
            let signature = Self.signature(path)
            return ReadItem(path: path, signature: signature, changed: needsRead || signatures[path] != signature)
        }, sinceMs: sinceMs, isSeed: needsRead, clock: clock)
        pendingRead = job.id
        if let backgroundReads {
            backgroundReads(job)
            return Update()
        }
        return complete(job.perform(), deduper: &deduper) ?? Update()
    }

    /// A restarted session or already-consumed job cannot change this source. A snapshot which changed again
    /// during its read is accepted once; its original signature schedules the latest state on the next poll.
    func complete(_ result: ReadResult, deduper: inout Deduper) -> Update? {
        guard pendingRead == result.id else { return nil }
        pendingRead = nil
        var update = Update()
        var next = states
        let available = Set(existingPaths)
        sessionOwners = sessionOwners.filter { available.contains($0.value) }
        var anyReady = false
        var failure: OpencodeSource.Status = .missing
        for reading in result.readings {
            let path = reading.item.path, signature = reading.item.signature
            guard signature[0]?.isRegularFile == true else {
                for id in next.keys where id.hasPrefix(path + "#") { next[id] = nil }
                signatures[path] = signature
                continue
            }
            // A deleted or replaced store cannot resurrect agents from an obsolete read. Appends to the
            // same inode are fine: the saved signature will trigger one follow-up read of their newer state.
            guard let current = FileStat.lstat(path), current.isRegularFile,
                  current.identity == signature[0]?.identity else {
                for id in next.keys where id.hasPrefix(path + "#") { next[id] = nil }
                signatures[path] = nil
                continue
            }
            guard reading.status == .ready else { failure = reading.status; continue }
            if !reading.item.changed { anyReady = true; continue }
            do {
                let rows = reading.rows, activities = reading.activities
                partialHistory = true
                for id in next.keys where id.hasPrefix(path + "#") { next[id] = nil }
                for row in rows {
                    let record = row.record
                    guard deduper.claim(record.key, for: fileID) != .otherFile else { continue }
                    // A copied database has a different agent path, but the same native response identity.
                    if let previous = emitted[record.key], previous.agentId != record.agentId { continue }
                    guard sessionOwners[row.sessionId].map({ $0 == path }) ?? true else { continue }
                    sessionOwners[row.sessionId] = path
                    partialHistory = true
                    var state = next[record.agentId] ?? AgentState(source: .trae)
                    if let cwd = row.cwd { state.setProject(fromWorkingDirectory: cwd) }
                    if record.endMs >= (state.lastLineMs ?? 0) { state.model = record.model }
                    state.noteLine(at: record.endMs)
                    state.setPhase(.idle, at: record.endMs)
                    next[record.agentId] = state
                    if emitted[record.key] != record {
                        update.upserts.append((ResponseUpsert(record: record, isCompletion: emitted[record.key] == nil), row.project ?? row.sessionId))
                        emitted[record.key] = record
                    }
                }
                for activity in activities {
                    guard sessionOwners[activity.session].map({ $0 == path }) ?? true else { continue }
                    sessionOwners[activity.session] = path
                    let id = path + "#" + activity.session
                    var state = next[id] ?? AgentState(source: .trae)
                    if let cwd = activity.cwd { state.setProject(fromWorkingDirectory: cwd) }
                    if let model = activity.model { state.model = model }
                    state.referenceResponses = activity.referenceResponses
                    state.noteLine(at: activity.lastLine)
                    state.setPhase(activity.phase, at: activity.lastLine, inFlightStartMs: activity.inFlightStart)
                    next[id] = state
                }
                signatures[path] = signature
                anyReady = true
            }
        }
        status = anyReady ? .ready : failure
        needsRead = false
        update.removedAgents = states.keys.filter { next[$0] == nil }
        update.agents = next.filter { states[$0.key] != $0.value }
        states = next
        return update
    }

    func activeSessionCount(todayStartMs: Int64) -> Int {
        states.values.filter { ($0.lastLineMs ?? 0) >= todayStartMs }.count
    }
}
