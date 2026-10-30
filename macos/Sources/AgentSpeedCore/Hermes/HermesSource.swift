import Foundation

/// Follows Hermes Agent's logs for the live monitor (METRICS 4.15): one log set per log folder of the Hermes home,
/// whose files (`agent.log` and its rotated copies) feed one parser state and are followed by identity across
/// renames, plus each set's Codex runtime switch read from its profile's `config.yaml`.
///
/// Not thread-safe: owned by `TranscriptSession` on its serial queue.
final class HermesSource {
    /// What changed in one read.
    struct Update {
        var upserts: [(upsert: ResponseUpsert, project: String)] = []
        /// Agent id → state, for agents whose state may have changed.
        var agents: [String: AgentState] = [:]
        var removedAgents: [String] = []
        /// Agent id → the name shown as its project: the log does not name a working directory (METRICS 4.15).
        var projects: [String: String] = [:]

        var isEmpty: Bool { upserts.isEmpty && agents.isEmpty && removedAgents.isEmpty }

        mutating func merge(_ other: Update) {
            upserts += other.upserts
            for id in other.removedAgents { agents[id] = nil }
            agents.merge(other.agents) { _, new in new }
            projects.merge(other.projects) { _, new in new }
            removedAgents += other.removedAgents.filter { other.agents[$0] == nil }
        }
    }

    /// Log sets own their response keys with ids counting up from here; transcript files count up from 0.
    static let firstFileID: FileID = .min / 2

    /// One log file, keyed by its identity: it keeps its offset when rotation renames it.
    private final class TrackedLog {
        var tailer: FileTailer
        var stat: FileStat
        var unreadable = false
        /// Not read yet (a file found at startup that starts at its end still counts as read).
        var pending = true

        init(path: String, stat: FileStat, start: FileTailer.Start, maxLineBytes: Int) {
            tailer = FileTailer(path: path, start: start, maxLineBytes: maxLineBytes)
            self.stat = stat
        }

        var path: String { tailer.path }
    }

    private final class LogSet {
        let canonicalFolder: String
        let configPath: String?
        /// `state.db` beside `config.yaml`; none for a log folder of another name.
        let statePath: String?
        var parser: HermesLogSet
        var files: [FileIdentity: TrackedLog] = [:]
        var configSignature: HermesCodexRuntime.FileSignature?
        var stateStat: FileStat?
        var stateWalStat: FileStat?
        var queriedSessions: Set<String> = []

        init(folder: String, timeZone: TimeZone, fileID: FileID) {
            canonicalFolder = HermesSource.canonical(folder)
            configPath = HermesHome.configPath(ofLogFolder: folder)
            statePath = configPath.map { HermesHome.join(HermesHome.directory(of: $0), "state.db") }
            parser = HermesLogSet(folder: folder, timeZone: timeZone, fileID: fileID)
        }

        var folder: String { parser.folder }
    }

    /// The Hermes home, or the folder of the user setting.
    let root: String
    /// True when `root` is the user setting (a home or one log folder).
    let isSetting: Bool
    private let timeZone: TimeZone
    private let limits: TranscriptSession.ReadingLimits
    private var sets: [String: LogSet] = [:]
    private var order: [String] = []
    private var nextFileID = HermesSource.firstFileID
    private var removedMalformedLines = 0
    private(set) var partialHistory = false
    private(set) var filesDiscovered = 0
    private(set) var filesRead = 0
    private(set) var bytesRead: Int64 = 0

    init(root: String, isSetting: Bool, timeZone: TimeZone, limits: TranscriptSession.ReadingLimits = .standard) {
        self.root = root
        self.isSetting = isSetting
        self.timeZone = timeZone
        self.limits = limits
    }

    // MARK: - Availability

    /// The home (or the folder of the setting) exists (METRICS 2); a home without logs counts.
    var detected: Bool { FileStat.stat(root)?.isDirectory == true }

    /// The log folders to read now: the setting's, else the home's (METRICS 2, 4.15).
    var logFolders: [String] {
        isSetting ? HermesHome.settingFolders(root) : HermesHome.logFolders(ofHome: root)
    }

    var watchedDirectories: [String] {
        var folders: [String] = []
        for folder in order {
            guard let set = sets[folder], FileStat.stat(folder)?.isDirectory == true else { continue }
            if !folders.contains(set.canonicalFolder) { folders.append(set.canonicalFolder) }
        }
        return folders
    }

    var malformedLines: Int { sets.values.reduce(removedMalformedLines) { $0 + $1.parser.malformedLines } }

    var unreadableFileCount: Int { sets.values.reduce(0) { $0 + $1.files.values.filter(\.unreadable).count } }

    /// Today's main sessions: distinct sessions with a turn start today whose platform is not `subagent` (DESIGN 5).
    func activeSessionCount(todayStartMs: Int64) -> Int {
        sets.values.reduce(0) { $0 + $1.parser.mainSessionCount(since: todayStartMs) }
    }

    /// The Codex runtime switch of a log folder (tests).
    func codexRuntime(ofFolder folder: String) -> Bool? { sets[folder]?.parser.codexRuntime }

    /// Every agent of every log set, by agent id (tests).
    var agentStates: [String: AgentState] {
        var states: [String: AgentState] = [:]
        for set in sets.values {
            for (sessionId, state) in set.parser.agents { states[set.parser.agentId(sessionId)] = state }
        }
        return states
    }

    // MARK: - Reading

    /// Startup read (METRICS 4.15, 5): per log folder, the Codex runtime switch, then its files in startup order;
    /// files modified since `todayStartMs − 15 min` are read from the start, older ones start at their end.
    func seed(todayStartMs: Int64, deduper: inout Deduper) -> Update {
        var update = Update()
        for folder in order { update.removedAgents += remove(folder) }
        filesDiscovered = 0
        filesRead = 0
        bytesRead = 0
        let threshold = todayStartMs - TranscriptSession.startupLookbackMs
        for folder in logFolders {
            update.merge(add(folder, threshold: threshold, deduper: &deduper))
            if let set = sets[folder] { update.merge(checkSessions(set)) }
        }
        update.removedAgents = update.removedAgents.filter { update.agents[$0] == nil }
        return update
    }

    /// Reads the log sets a set of changed (canonical) paths names.
    func handleChanges(atCanonicalPaths paths: Set<String>, deduper: inout Deduper) -> Update {
        var touched: Set<String> = []
        for path in paths {
            let folder = (path as NSString).deletingLastPathComponent
            guard HermesHome.isLogFileName((path as NSString).lastPathComponent),
                  let set = order.first(where: { sets[$0]?.canonicalFolder == folder }) else { continue }
            touched.insert(set)
        }
        var update = Update()
        for folder in order where touched.contains(folder) {
            if let set = sets[folder] { update.merge(sync(set, deduper: &deduper)) }
        }
        return update
    }

    /// The 1-second fallback: a log set with a file modified in the last two minutes is read again when one of its
    /// paths now names another file, changed size or modification time, or vanished.
    func poll(nowMs: Int64, deduper: inout Deduper) -> Update {
        var update = Update()
        for folder in order {
            guard let set = sets[folder] else { continue }
            let hot = set.files.values.filter { nowMs - $0.stat.modifiedMs <= TranscriptSession.hotWindowMs }
            let changed = hot.contains { file in
                guard let stat = FileStat.lstat(file.path) else { return true }
                return stat.identity != file.stat.identity || stat.size != file.stat.size || stat.modifiedNs != file.stat.modifiedNs
            }
            if changed { update.merge(sync(set, deduper: &deduper)) }
        }
        return update
    }

    /// Periodic check (every 10 s): log folders that appeared are read from the start, vanished ones leave; every
    /// set checks its Codex runtime switch, then reads what changed.
    func rediscover(deduper: inout Deduper) -> Update {
        var update = Update()
        let folders = logFolders
        for folder in order where !folders.contains(folder) { update.removedAgents += remove(folder) }
        for folder in folders {
            if let set = sets[folder] {
                update.merge(checkConfig(set))
                update.merge(sync(set, deduper: &deduper))
            } else {
                update.merge(add(folder, threshold: nil, deduper: &deduper))
            }
            if let set = sets[folder] { update.merge(checkSessions(set)) }
        }
        update.removedAgents = update.removedAgents.filter { update.agents[$0] == nil }
        return update
    }

    /// A new log set: its switch first, then its files in startup order. `threshold` set (startup): older files start
    /// at their end; nil (found while running): every file is read from the start.
    private func add(_ folder: String, threshold: Int64?, deduper: inout Deduper) -> Update {
        let set = LogSet(folder: folder, timeZone: timeZone, fileID: nextFileID)
        nextFileID &+= 1
        sets[folder] = set
        order.append(folder)
        var update = checkConfig(set)
        let files = Self.discover(folder).sorted(by: TranscriptDiscovery.startupOrder)
        filesDiscovered += files.count
        for file in files {
            var start = FileTailer.Start.beginning
            if let threshold, file.stat.modifiedMs < threshold {
                start = .end(file.stat.size)
            } else if file.stat.size > limits.largeFileBytes {
                partialHistory = true
                start = .tail(limits.tailWindowBytes)
            }
            let tracked = TrackedLog(path: file.path, stat: file.stat, start: start, maxLineBytes: limits.maxLineBytes)
            set.files[file.stat.identity] = tracked
            if case .end = start {
                // Nothing to read now; the first change reads what was appended after this size.
                tracked.pending = false
                continue
            }
            update.merge(read(tracked, in: set, deduper: &deduper))
            filesRead += 1
        }
        return update
    }

    private func remove(_ folder: String) -> [String] {
        guard let set = sets.removeValue(forKey: folder) else { return [] }
        order.removeAll { $0 == folder }
        removedMalformedLines += set.parser.malformedLines
        return set.parser.sessionOrder.map(set.parser.agentId)
    }

    /// Lists the set's files, follows renamed identities, drops vanished ones, tracks new ones from offset 0 and reads
    /// every file that changed, in startup order.
    private func sync(_ set: LogSet, deduper: inout Deduper) -> Update {
        let found = Self.discover(set.folder)
        var present: Set<FileIdentity> = []
        for file in found where present.insert(file.stat.identity).inserted {
            if let tracked = set.files[file.stat.identity] {
                if tracked.path != file.path { tracked.tailer.follow(to: file.path) }
            } else {
                set.files[file.stat.identity] = TrackedLog(path: file.path, stat: file.stat, start: .beginning,
                                                           maxLineBytes: limits.maxLineBytes)
            }
        }
        for identity in set.files.keys where !present.contains(identity) { set.files[identity] = nil }
        var update = Update()
        let current = Dictionary(found.map { ($0.stat.identity, $0.stat) }, uniquingKeysWith: { first, _ in first })
        let files = set.files.values.compactMap { file in current[file.stat.identity].map { (file, $0) } }
            .sorted { TranscriptDiscovery.startupOrder(DiscoveredFile(path: $0.0.path, stat: $0.1), DiscoveredFile(path: $1.0.path, stat: $1.1)) }
        for (file, stat) in files where file.pending || file.unreadable || stat.size != file.stat.size || stat.modifiedNs != file.stat.modifiedNs {
            update.merge(read(file, in: set, deduper: &deduper))
        }
        return update
    }

    /// Reads a file's complete new lines into its set. A shrunk file is read again from the start into the same state.
    private func read(_ file: TrackedLog, in set: LogSet, deduper: inout Deduper) -> Update {
        var upserts: [ResponseUpsert] = []
        var touched: [String] = []
        let result = file.tailer.read(expecting: file.stat.identity) { line in
            if let session = set.parser.consume(line, deduper: &deduper, emit: { upserts.append($0) }) {
                touched.append(session)
            }
        }
        // Renamed or removed in the meantime: the next listing finds it.
        if result.otherFile || (result.failed && FileStat.lstat(file.path) == nil) { return Update() }
        file.pending = false
        file.unreadable = result.failed
        if let stat = result.stat { file.stat = stat }
        bytesRead += Int64(result.bytesRead)
        var update = Update()
        let project = Self.project(ofFolder: set.folder)
        update.upserts = upserts.map { ($0, project) }
        for session in Set(touched) { note(session, of: set, in: &update) }
        return update
    }

    /// Applies the Codex runtime switch and the configured values of the set's `config.yaml` (METRICS 4.15).
    private func checkConfig(_ set: LogSet) -> Update {
        guard let path = set.configPath else {
            set.parser.setCodexRuntime(false)
            return Update()
        }
        var changed: [String] = []
        switch HermesCodexRuntime.check(path, last: set.configSignature) {
        case .missing:
            set.configSignature = nil
            changed = set.parser.setCodexRuntime(false) + set.parser.setConfiguredModes(.empty)
        case .read(let config, let signature):
            set.configSignature = signature
            changed = set.parser.setCodexRuntime(config.codexRuntime) + set.parser.setConfiguredModes(config.configuredModes)
        case .unchanged:
            break
        }
        var update = Update()
        for session in Set(changed) { note(session, of: set, in: &update) }
        return update
    }

    /// The session-current efforts of `state.db`: read when it or its `-wal` changed, else only for sessions it was not
    /// asked about yet, so at most once per rediscovery. No connection stays open (METRICS 4.15).
    private func checkSessions(_ set: LogSet) -> Update {
        guard let path = set.statePath else { return Update() }
        let stat = FileStat.lstat(path), wal = FileStat.lstat(path + "-wal")
        let changed = stat != set.stateStat || wal != set.stateWalStat
        let sessions = set.parser.sessionOrder.filter { !$0.isEmpty && (changed || !set.queriedSessions.contains($0)) }
        (set.stateStat, set.stateWalStat) = (stat, wal)
        guard !sessions.isEmpty else { return Update() }
        let values = SessionModes.hermes(databasePath: path, sessions: sessions)
        set.queriedSessions.formUnion(sessions)
        var update = Update()
        for session in sessions where set.parser.setSessionModes(values[session] ?? .empty, of: session) {
            note(session, of: set, in: &update)
        }
        return update
    }

    // MARK: - Helpers

    private func note(_ session: String, of set: LogSet, in update: inout Update) {
        let id = set.parser.agentId(session)
        update.agents[id] = set.parser.agents[session]
        update.projects[id] = Self.project(ofFolder: set.folder)
    }

    /// The log files of a folder with their stats.
    static func discover(_ folder: String) -> [DiscoveredFile] {
        HermesHome.logFiles(in: folder).compactMap { path in
            guard let stat = FileStat.lstat(path), stat.isRegularFile else { return nil }
            return DiscoveredFile(path: path, stat: stat)
        }
    }

    /// The folder that holds the log folder: a profile's name, or the Hermes home's.
    static func project(ofFolder folder: String) -> String {
        let normalized = TranscriptDiscovery.normalizedRoot(folder)
        let parent = (normalized as NSString).deletingLastPathComponent
        let name = (parent as NSString).lastPathComponent
        return name.isEmpty || name == "/" ? (normalized as NSString).lastPathComponent : name
    }

    fileprivate static func canonical(_ path: String) -> String {
        guard let resolved = realpath(path, nil) else { return path }
        defer { free(resolved) }
        return String(cString: resolved)
    }
}
