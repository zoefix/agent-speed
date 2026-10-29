import Foundation

/// Follows OpenClaw's agent databases for the live monitor (METRICS 4.14): one agent per transcript window, read in
/// short read-only transactions, incrementally by `seq`, with rewritten windows read again from their first row.
///
/// Not thread-safe: owned by `TranscriptSession` on its serial queue.
final class OpenClawSource {
    typealias Status = OpencodeSource.Status

    /// What changed in one read.
    struct Update {
        var upserts: [(upsert: ResponseUpsert, project: String)] = []
        /// Agent id → state, for windows whose state may have changed.
        var agents: [String: AgentState] = [:]
        /// Agent id → current task (METRICS 6.9), for the windows in `agents`.
        var tasks: [String: AgentTaskStatus] = [:]
        var removedAgents: [String] = []
        /// Watched tasks that completed (METRICS 11.10), by agent id.
        var completions: [(agentId: String, completion: TaskCompletion)] = []

        var isEmpty: Bool { upserts.isEmpty && agents.isEmpty && removedAgents.isEmpty }

        mutating func merge(_ other: Update) {
            upserts += other.upserts
            completions += other.completions
            for id in other.removedAgents {
                agents[id] = nil
                tasks[id] = nil
            }
            agents.merge(other.agents) { _, new in new }
            tasks.merge(other.tasks) { _, new in new }
            removedAgents += other.removedAgents.filter { other.agents[$0] == nil }
        }
    }

    /// Window ids for deduplication count down from here; transcript files count up from 0.
    static let firstFileID: FileID = .min

    private struct TrackedWindow {
        var info: OpenClawWindow
        var parser: OpenClawParser
        /// The last row read; `.min` before the first.
        var lastSeq: Int64 = .min
        var generation: String?
        /// Started past its first row: the header is read before the next rows (METRICS 4.7, 4.14).
        var headerPending = false
        /// The window's task stream (METRICS 6.9) and its current task's evaluation.
        var taskReader: TaskLineReader?
        var tasks = TaskComposer(source: .openclaw, keepsEveryTask: false)
        var taskStatus: (version: Int, models: Int, status: AgentTaskStatus)?

        init(info: OpenClawWindow, parser: OpenClawParser) {
            self.info = info
            self.parser = parser
            taskReader = TaskLineReader(source: .openclaw)
            taskReader?.mirrorWindow = info.mirrorsTurns
        }

        mutating func taskStatus(models: TaskModels, version: Int) -> AgentTaskStatus {
            if let cached = taskStatus, cached.version == tasks.version, cached.models == version { return cached.status }
            let status = AgentTaskStatus(events: tasks.events, now: nil, models: models)
            taskStatus = (tasks.version, version, status)
            return status
        }
    }

    private final class TrackedDatabase {
        let path: String
        let canonicalPath: String
        var status: Status = .missing
        var connection: OpenClawDatabase?
        /// The `.db` when the connection opened, to drop an immutable connection after a change.
        var openedStat: FileStat?
        var stat: FileStat?
        var walStat: FileStat?
        var windows: [String: TrackedWindow] = [:]
        var currentSessions: [String: String] = [:]
        /// A read failed (busy or vanished); the next poll or rediscovery reads again.
        var needsRead = false

        init(path: String) {
            self.path = path
            canonicalPath = OpenClawSource.canonical(path)
        }
    }

    let roots: [String]
    private let maxLineBytes: Int
    private var databases: [String: TrackedDatabase] = [:]
    private var fileIDs: [String: FileID] = [:]
    private var nextFileID = OpenClawSource.firstFileID
    private var removedMalformedLines = 0
    /// The prediction coefficients of OpenClaw's calibration (METRICS 6.11).
    var taskModels: [SourceKind: TaskModels] = [:] {
        didSet { taskModelsVersion += 1 }
    }
    private var taskModelsVersion = 0
    /// Live reporting's watch for the rows read now (METRICS 11.10); nil while it is off or seeding runs.
    var taskWatch: () -> ReportTaskWatchContext? = { nil }

    init(roots: [String], maxLineBytes: Int = FileTailer.defaultMaxLineBytes) {
        self.roots = roots
        self.maxLineBytes = maxLineBytes
    }

    // MARK: - Availability

    /// A state folder (or the folder or database of the setting) exists (METRICS 2).
    var detected: Bool { roots.contains(where: Self.exists) }

    /// The first root that exists, else the first root.
    var displayRoot: String { roots.first(where: Self.exists) ?? roots.first ?? "" }

    var databaseCount: Int { databases.count }
    var readyDatabaseCount: Int { databases.values.filter { $0.status == .ready }.count }
    var databaseBytes: Int64 { databases.values.reduce(0) { $0 + ($1.stat?.size ?? 0) + ($1.walStat?.size ?? 0) } }
    var hasUnsupportedDatabase: Bool { databases.values.contains { $0.status == .unsupported } }
    var unreadableDatabaseCount: Int { databases.values.filter { $0.status == .unreadable }.count }

    var malformedRows: Int {
        databases.values.reduce(removedMalformedLines) { sum, database in
            database.windows.values.reduce(sum) { $0 + $1.parser.state.malformedLines }
        }
    }

    /// Folders the file watcher follows: the `agents` folder of every state folder, or the folder of a database.
    var watchedDirectories: [String] {
        var folders: [String] = []
        for root in roots {
            let folder: String
            if FileStat.stat(root)?.isRegularFile == true {
                folder = (root as NSString).deletingLastPathComponent
            } else if FileStat.lstat((root as NSString).appendingPathComponent(OpenClawHome.databaseName))?.isRegularFile == true {
                folder = root
            } else {
                folder = (root as NSString).appendingPathComponent("agents")
            }
            guard FileStat.stat(folder)?.isDirectory == true else { continue }
            let canonical = Self.canonical(folder)
            if !folders.contains(canonical) { folders.append(canonical) }
        }
        for database in databases.values {
            let folder = (database.canonicalPath as NSString).deletingLastPathComponent
            if !folders.contains(where: { folder == $0 || folder.hasPrefix($0 + "/") }) { folders.append(folder) }
        }
        return folders.sorted()
    }

    /// Every agent database the roots name, in root order, without duplicates.
    func discover() -> [String] {
        var seen: Set<String> = []
        return roots.flatMap(OpenClawHome.databases(in:)).filter { seen.insert($0).inserted }
    }

    // MARK: - Reading

    /// Startup read (METRICS 4.14, 5): databases in startup order; windows active since `todayStartMs − 15 min` are
    /// read completely, older ones start after their last row.
    func seed(todayStartMs: Int64, deduper: inout Deduper) -> Update {
        var update = Update()
        update.removedAgents = databases.values.flatMap { database in database.windows.keys.map { Self.agentId(database.path, $0) } }
        for database in databases.values { forget(database) }
        databases = [:]
        let threshold = todayStartMs - TranscriptSession.startupLookbackMs
        for path in Self.startupOrder(discover()) {
            let database = TrackedDatabase(path: path)
            databases[path] = database
            update.merge(read(database, threshold: threshold, readsTools: false, deduper: &deduper))
        }
        update.removedAgents = update.removedAgents.filter { update.agents[$0] == nil }
        return update
    }

    /// Reads the databases a set of changed (canonical) paths names; a new database file triggers discovery.
    func handleChanges(atCanonicalPaths paths: Set<String>, todayStartMs: Int64, deduper: inout Deduper) -> Update {
        var update = Update()
        var unknown = false
        for path in paths {
            let name = (path as NSString).lastPathComponent
            guard name == OpenClawHome.databaseName || name == OpenClawHome.databaseName + "-wal" else { continue }
            let database = String(path.hasSuffix("-wal") ? path.dropLast(4) : path[...])
            // Only `agents/<agentId>/agent/openclaw-agent.sqlite` of a visible agent folder can be a new database.
            let agentFolder = (database as NSString).deletingLastPathComponent
            let agentName = ((agentFolder as NSString).deletingLastPathComponent as NSString).lastPathComponent
            guard (agentFolder as NSString).lastPathComponent == "agent", !agentName.hasPrefix(".") else { continue }
            if !databases.values.contains(where: { $0.canonicalPath == database }) { unknown = true }
        }
        for database in databases.values.sorted(by: { $0.path < $1.path })
        where paths.contains(database.canonicalPath) || paths.contains(database.canonicalPath + "-wal") {
            update.merge(read(database, threshold: nil, readsTools: true, deduper: &deduper))
        }
        if unknown { update.merge(rediscover(todayStartMs: todayStartMs, deduper: &deduper)) }
        return update
    }

    /// The 1-second fallback: reads every database whose file or `-wal` changed, or whose last read failed while it
    /// was busy. `retriesUnreadable`: also databases that could not be opened (rediscovery).
    func refreshIfChanged(retriesUnreadable: Bool = false, deduper: inout Deduper) -> Update {
        var update = Update()
        for database in databases.values.sorted(by: { $0.path < $1.path })
        where hasChanged(database) || (retriesUnreadable && database.status == .unreadable) {
            update.merge(read(database, threshold: nil, readsTools: true, deduper: &deduper))
        }
        return update
    }

    /// Periodic check: new databases are read like at startup, vanished ones are dropped, changed ones are read.
    func rediscover(todayStartMs: Int64, deduper: inout Deduper) -> Update {
        var update = Update()
        let found = discover()
        let present = Set(found)
        for (path, database) in databases where !present.contains(path) {
            update.removedAgents += database.windows.keys.map { Self.agentId(path, $0) }
            forget(database)
            databases[path] = nil
        }
        let threshold = todayStartMs - TranscriptSession.startupLookbackMs
        for path in Self.startupOrder(found.filter { databases[$0] == nil }) {
            let database = TrackedDatabase(path: path)
            databases[path] = database
            update.merge(read(database, threshold: threshold, readsTools: false, deduper: &deduper))
        }
        update.merge(refreshIfChanged(retriesUnreadable: true, deduper: &deduper))
        return update
    }

    private func hasChanged(_ database: TrackedDatabase) -> Bool {
        if database.needsRead { return true }
        let stat = FileStat.lstat(database.path)
        return stat != database.stat || FileStat.lstat(database.path + "-wal") != database.walStat
    }

    /// One short read transaction over one database. `threshold` set: windows quiet since before it start after their
    /// last row (startup); nil: new windows are read completely.
    private func read(_ database: TrackedDatabase, threshold: Int64?, readsTools: Bool, deduper: inout Deduper) -> Update {
        var update = Update()
        guard let stat = FileStat.lstat(database.path), stat.isRegularFile else {
            update.removedAgents = database.windows.keys.map { Self.agentId(database.path, $0) }
            forget(database)
            database.windows = [:]
            database.status = .missing
            database.stat = nil
            database.walStat = nil
            return update
        }
        if let previous = database.stat, previous.identity != stat.identity {
            // Replaced: every window is read again; the dedupe set (and each window's owner id) is kept.
            update.removedAgents = database.windows.keys.map { Self.agentId(database.path, $0) }
            forget(database)
            database.windows = [:]
            database.connection = nil
        }
        let wal = FileStat.lstat(database.path + "-wal")
        if let connection = database.connection, connection.isImmutable,
           wal != nil || stat.size != database.openedStat?.size || stat.modifiedNs != database.openedStat?.modifiedNs {
            database.connection = nil
        }
        if database.connection == nil {
            do {
                database.connection = try OpenClawDatabase(path: database.path)
                database.openedStat = stat
                database.status = .ready
            } catch .unsupported {
                database.status = .unsupported
            } catch .missing {
                database.status = .missing
            } catch {
                database.status = .unreadable
            }
        }
        database.stat = stat
        database.walStat = wal
        guard let connection = database.connection else { return update }
        do {
            try connection.read {
                let windows = try connection.windows()
                let generations = try connection.generations()
                database.currentSessions = try connection.currentSessions()
                let present = Set(windows.map(\.sessionId))
                for id in database.windows.keys where !present.contains(id) {
                    update.removedAgents.append(Self.agentId(database.path, id))
                    removedMalformedLines += database.windows[id]?.parser.state.malformedLines ?? 0
                    database.windows[id] = nil
                }
                for info in windows {
                    try read(info, generation: generations[info.sessionId], in: database, connection: connection,
                             threshold: threshold, readsTools: readsTools, deduper: &deduper, into: &update)
                }
            }
            database.needsRead = false
        } catch {
            // A busy or vanished database is read again on the next change, poll or rediscovery.
            database.needsRead = true
            database.connection = nil
        }
        return update
    }

    private func read(
        _ info: OpenClawWindow,
        generation: String?,
        in database: TrackedDatabase,
        connection: OpenClawDatabase,
        threshold: Int64?,
        readsTools: Bool,
        deduper: inout Deduper,
        into update: inout Update
    ) throws {
        let agentId = Self.agentId(database.path, info.sessionId)
        var window: TrackedWindow
        if var tracked = database.windows[info.sessionId] {
            let rewritten = tracked.generation != generation || tracked.info.isDelegated != info.isDelegated
                || tracked.info.mirrorsTurns != info.mirrorsTurns || tracked.info.isSubagent != info.isSubagent
            let advanced = rewritten || info.transcriptUpdatedMs == nil || info.transcriptUpdatedMs != tracked.info.transcriptUpdatedMs
            tracked.info = info
            guard advanced else {
                database.windows[info.sessionId] = tracked
                return
            }
            let highest = try connection.maxSeq(session: info.sessionId) ?? .min
            if rewritten || highest < tracked.lastSeq {
                // Rewritten: parser state restarts and the window is read again from its first row (METRICS 4.14).
                removedMalformedLines += tracked.parser.state.malformedLines
                tracked = TrackedWindow(info: info, parser: OpenClawParser(agentId: agentId, fileID: fileID(agentId), window: info))
            } else if highest == tracked.lastSeq {
                database.windows[info.sessionId] = tracked
                return
            }
            window = tracked
        } else {
            window = TrackedWindow(info: info, parser: OpenClawParser(agentId: agentId, fileID: fileID(agentId), window: info))
            if let threshold, info.activityMs < threshold {
                window.lastSeq = try connection.maxSeq(session: info.sessionId) ?? .min
                window.headerPending = window.lastSeq != .min
                window.generation = generation
                database.windows[info.sessionId] = window
                return
            }
        }
        window.generation = generation
        var upserts: [ResponseUpsert] = []
        let fileID = window.parser.fileID
        if window.headerPending {
            window.headerPending = false
            if let header = try connection.header(session: info.sessionId, maxLineBytes: maxLineBytes), let entry = header.entry {
                window.parser.consume(entry, deduper: &deduper) { upserts.append($0) }
                if case .line(let line)? = header.entry, case .header(let found) = line {
                    // The header's cwd counts for a window read from an offset (METRICS 6.9 *Reading from an offset*).
                    window.taskReader?.headerSeen = true
                    window.taskReader?.createdMs = found.timestampMs
                    window.taskReader?.setCwd(found.cwd)
                }
            }
        }
        var taskReader = window.taskReader
        window.tasks.watch = taskWatch()
        try connection.rows(session: info.sessionId, after: window.lastSeq, maxLineBytes: maxLineBytes, readsTools: readsTools,
                            tasks: &taskReader) { row in
            window.lastSeq = max(window.lastSeq, row.seq)
            guard let entry = row.entry else { return }
            window.parser.consume(entry, tools: row.tools, deduper: &deduper) { upserts.append($0) }
            guard let t = window.tasks.feed(row.task, isDuplicate: { deduper.isOwnedByOtherFile($0, for: fileID) }),
                  window.tasks.watch != nil else { return }
            // Live reporting takes a completed task's model from these notes (METRICS 11.10).
            let effort = window.parser.state.modes.attribution(for: .effort)
            window.tasks.note(t: t, model: window.parser.state.model, effort: effort.isUnknown ? nil : effort.mode)
        }
        window.taskReader = taskReader
        update.completions += window.tasks.takeCompletions().map { (agentId, $0) }
        let status = window.taskStatus(models: taskModels[.openclaw] ?? .prior, version: taskModelsVersion)
        database.windows[info.sessionId] = window
        let project = Self.project(of: window.parser.state, agentId: agentId)
        update.upserts += upserts.map { ($0, project) }
        update.agents[agentId] = window.parser.state
        update.tasks[agentId] = status
    }

    /// Every window's state with its current task evaluated with the current coefficients.
    func taskStatusUpdate() -> Update {
        var update = Update()
        for database in databases.values {
            for (id, var window) in database.windows {
                let agentId = Self.agentId(database.path, id)
                update.agents[agentId] = window.parser.state
                update.tasks[agentId] = window.taskStatus(models: taskModels[.openclaw] ?? .prior, version: taskModelsVersion)
                database.windows[id] = window
            }
        }
        return update
    }

    // MARK: - Live reporting (METRICS 11.10)

    /// A break: every window's tasks are unwatched.
    func clearTaskWatch() {
        for database in databases.values {
            for id in Array(database.windows.keys) where database.windows[id]?.tasks.hasWatchedTasks == true {
                database.windows[id]?.tasks.clearWatch()
            }
        }
    }

    /// The 30-minute rule: a window quiet that long completes its watched tasks.
    func completeIdleTasks(nowMs: Int64) -> Update {
        var update = Update()
        for database in databases.values {
            for (id, window) in database.windows where window.tasks.hasWatchedTasks {
                guard let last = window.parser.state.lastLineMs, TaskRecord.idle(nowMs, since: last) else { continue }
                var tasks = window.tasks
                tasks.completeWatchedTasks()
                update.completions += tasks.takeCompletions().map { (Self.agentId(database.path, id), $0) }
                database.windows[id]?.tasks = tasks
            }
        }
        return update
    }

    private func forget(_ database: TrackedDatabase) {
        for window in database.windows.values { removedMalformedLines += window.parser.state.malformedLines }
    }

    private func fileID(_ agentId: String) -> FileID {
        if let id = fileIDs[agentId] { return id }
        let id = nextFileID
        nextFileID = nextFileID == -4 ? Self.firstFileID : nextFileID &+ 1
        fileIDs[agentId] = id
        return id
    }

    // MARK: - Agents

    /// Agent id → state of every window that has at least one line read.
    var agentStates: [String: AgentState] {
        var states: [String: AgentState] = [:]
        for database in databases.values {
            for (id, window) in database.windows where window.parser.state.lastLineMs != nil {
                states[Self.agentId(database.path, id)] = window.parser.state
            }
        }
        return states
    }

    /// A sub-agent window → the current window of the session key that spawned it, in the same database (METRICS 4.14).
    var parentLinks: [String: String] {
        var links: [String: String] = [:]
        for database in databases.values {
            for (id, window) in database.windows {
                guard let key = window.info.spawnedBy, let parent = database.currentSessions[key], parent != id,
                      database.windows[parent] != nil else { continue }
                links[Self.agentId(database.path, id)] = Self.agentId(database.path, parent)
            }
        }
        return links
    }

    /// Windows that are not sub-agents with a line today.
    func activeSessionCount(todayStartMs: Int64) -> Int {
        databases.values.reduce(0) { count, database in
            count + database.windows.values.filter { !$0.info.isSubagent && ($0.parser.state.lastLineMs ?? .min) >= todayStartMs }.count
        }
    }

    // MARK: - Helpers

    static func agentId(_ databasePath: String, _ sessionId: String) -> String {
        databasePath + "#" + sessionId
    }

    /// The database of an OpenClaw agent id (`<database>#<session id>`); nil for any other id.
    static func databasePath(ofAgent id: String) -> String? {
        guard let range = id.range(of: "/" + OpenClawHome.databaseName + "#", options: .backwards) else { return nil }
        return String(id[..<range.upperBound].dropLast())
    }

    /// The header's working directory, else the OpenClaw agent the database belongs to.
    static func project(of state: AgentState, agentId: String) -> String {
        state.project ?? EngineAgent.fallbackProject(for: agentId)
    }

    /// Startup order (METRICS 5): the newer of the database and its `-wal`, then the path.
    static func startupOrder(_ paths: [String]) -> [String] {
        paths.compactMap { path in FileStat.databaseStat(path).map { DiscoveredFile(path: path, stat: $0) } }
            .sorted(by: TranscriptDiscovery.startupOrder)
            .map(\.path)
    }

    private static func exists(_ path: String) -> Bool {
        guard let stat = FileStat.stat(path) else { return false }
        return stat.isDirectory || stat.isRegularFile
    }

    fileprivate static func canonical(_ path: String) -> String {
        guard let resolved = realpath(path, nil) else { return path }
        defer { free(resolved) }
        return String(cString: resolved)
    }
}
