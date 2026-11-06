import Foundation

/// Follows the Devin CLI for the live monitor (METRICS 4.17): the databases of its location list, one reader each in
/// list order, a session read once.
///
/// Not thread-safe: owned by `TranscriptSession` on its serial queue.
final class DevinSource {
    struct Upsert {
        var upsert: ResponseUpsert
        var project: String
        /// Read at startup or from a database found while running: counts, but raises no live event.
        var seeded: Bool
    }

    /// What changed in one read.
    struct Update {
        var upserts: [Upsert] = []
        /// Agent id → state, for agents whose state may have changed.
        var agents: [String: AgentState] = [:]
        var removedAgents: [String] = []

        var isEmpty: Bool { upserts.isEmpty && agents.isEmpty && removedAgents.isEmpty }

        mutating func merge(_ other: Update) {
            upserts += other.upserts
            for id in other.removedAgents { agents[id] = nil }
            agents.merge(other.agents) { _, new in new }
            removedAgents += other.removedAgents.filter { other.agents[$0] == nil }
        }
    }

    /// Databases own their response keys with ids counting up from here.
    static let firstFileID: FileID = .min / 4

    let roots: [DevinRoot]
    private let claims = SessionClaims()
    private var readers: [DevinCliReader] = []
    private var nextFileID = DevinSource.firstFileID
    /// A database keeps its dedupe owner across rebuilds of the reader list.
    private var databaseIDs: [String: FileID] = [:]
    private var todayStartMs: Int64 = 0
    private var removedMalformedLines = 0
    private(set) var displayRoot = ""
    private(set) var filesDiscovered = 0
    private(set) var filesRead = 0
    private(set) var bytesRead: Int64 = 0

    init(roots: [DevinRoot]) {
        self.roots = roots
        updateDisplayRoot()
    }

    // MARK: - Availability

    var detected: Bool { readers.contains(where: \.exists) || !DevinPaths.databases(roots).isEmpty }

    var watchedDirectories: [String] {
        var paths: [String] = []
        for directory in readers.compactMap(\.watchedDirectory) {
            let canonical = LocationPaths.resolved(directory)
            if !paths.contains(canonical) { paths.append(canonical) }
        }
        return paths
    }

    var hasUnsupported: Bool { readers.contains { $0.status == .unsupported } }
    var unreadableCount: Int { readers.filter { $0.status == .unreadable }.count }
    var malformedLines: Int { readers.reduce(removedMalformedLines) { $0 + $1.malformedRows } }

    /// Today's sessions: sessions with a row today (DESIGN 5).
    func activeSessionCount(todayStartMs: Int64) -> Int {
        readers.reduce(0) { $0 + $1.activeSessionCount(todayStartMs: todayStartMs) }
    }

    /// Every agent's state (tests).
    var agentStates: [String: AgentState] {
        var states: [String: AgentState] = [:]
        for reader in readers {
            for (session, state) in reader.states { states[reader.agentId(session)] = state.agent }
        }
        return states
    }

    // MARK: - Reading

    /// Startup read (METRICS 4.17, 5): every database in list order, the rows of the startup window.
    func seed(todayStartMs: Int64, deduper: inout Deduper) -> Update {
        self.todayStartMs = todayStartMs
        var update = Update()
        for reader in readers {
            update.removedAgents += reader.agentIds
            reader.release()
        }
        readers = []
        filesDiscovered = 0
        filesRead = 0
        bytesRead = 0
        updateDisplayRoot()
        syncReaders(DevinPaths.databases(roots))
        for reader in readers {
            if let next = reader.refresh(todayStartMs: todayStartMs, deduper: &deduper) { update.merge(next) }
            guard reader.exists else { continue }
            filesDiscovered += 1
            filesRead += reader.status == .ready ? 1 : 0
            bytesRead += reader.databaseBytes
        }
        update.removedAgents = update.removedAgents.filter { update.agents[$0] == nil }
        return update
    }

    /// Reads the databases a set of changed (canonical) paths names.
    func handleChanges(atCanonicalPaths paths: Set<String>, deduper: inout Deduper) -> Update {
        var update = Update()
        for reader in readers where paths.contains(where: reader.matches(canonicalPath:)) {
            if let next = reader.refresh(todayStartMs: todayStartMs, deduper: &deduper) { update.merge(next) }
        }
        return update
    }

    /// The 1-second fallback: databases whose file or `-wal` changed.
    func poll(deduper: inout Deduper) -> Update {
        var update = Update()
        for reader in readers {
            if let next = reader.refresh(todayStartMs: todayStartMs, deduper: &deduper) { update.merge(next) }
        }
        return update
    }

    /// Periodic check (every 10 s): the roots resolved again; databases that appeared, vanished or moved in the list.
    func rediscover(todayStartMs: Int64, deduper: inout Deduper) -> Update {
        self.todayStartMs = todayStartMs
        updateDisplayRoot()
        var update = Update()
        update.removedAgents += syncReaders(DevinPaths.databases(roots))
        for reader in readers {
            if let next = reader.refresh(todayStartMs: todayStartMs, deduper: &deduper) { update.merge(next) }
        }
        update.removedAgents = update.removedAgents.filter { update.agents[$0] == nil }
        return update
    }

    // MARK: - Helpers

    /// The row's path: the first root that names an existing database, else the first root (METRICS 2.1).
    private func updateDisplayRoot() {
        displayRoot = (roots.first { $0.database != nil } ?? roots.first)?.displayFolder ?? ""
    }

    /// One reader per database in list order. A list that changed (a database appeared, vanished or moved) starts every
    /// reader over, like at startup, so the earliest database keeps its sessions; returns the agents that left.
    @discardableResult
    private func syncReaders(_ databases: [String]) -> [String] {
        guard databases != readers.map(\.path) else { return [] }
        var removed: [String] = []
        for reader in readers {
            removed += reader.agentIds
            removedMalformedLines += reader.malformedRows
            reader.release()
        }
        readers = databases.enumerated().map { position, path in
            let fileID = databaseIDs[path] ?? {
                defer { nextFileID &+= 1 }
                return nextFileID
            }()
            databaseIDs[path] = fileID
            return DevinCliReader(path: path, fileID: fileID, claims: claims, owner: position)
        }
        return removed
    }
}
