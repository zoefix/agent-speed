import Foundation

/// What file-based agents show beyond their own facts: configured values and WorkBuddy's session-current effort. Files
/// are checked at startup and at every rediscovery and read again only when their size or modification time changed.
///
/// Not thread-safe: owned by `TranscriptSession` on its serial queue.
final class AgentModeContext {
    private struct Watched<Value: Equatable> {
        let path: String
        let read: (String) -> Value
        var value: Value
        private var signature: [Int64]?

        init(path: String, empty: Value, read: @escaping (String) -> Value) {
            self.path = path
            self.read = read
            value = empty
        }

        /// True when the value changed.
        mutating func refresh() -> Bool {
            let stat = FileStat.lstat(path)
            let current = stat.map { [$0.size, $0.modifiedNs] }
            guard current != signature else { return false }
            signature = current
            let next = read(path)
            defer { value = next }
            return next != value
        }
    }

    /// WorkBuddy's `workbuddy.db` beside one `projects` folder.
    private struct WorkBuddyStore {
        let path: String
        var stat: FileStat?
        var walStat: FileStat?
        var values: [String: ExecutionModes] = [:]
        var queried: Set<String> = []

        init(path: String) { self.path = path }

        /// Queries when the file changed, else only the sessions not queried yet. True when a value changed.
        mutating func refresh(sessions: Set<String>) -> Bool {
            let current = FileStat.lstat(path), wal = FileStat.lstat(path + "-wal")
            let changed = current != stat || wal != walStat
            let pending = changed ? sessions : sessions.subtracting(queried)
            guard !pending.isEmpty else {
                (stat, walStat) = (current, wal)
                return false
            }
            let read = SessionModes.workBuddy(databasePath: path, sessions: pending.sorted())
            (stat, walStat) = (current, wal)
            queried.formUnion(pending)
            var updated = false
            for session in pending {
                let value = read[session]
                if value != values[session] { updated = true }
                values[session] = value
            }
            return updated
        }
    }

    private var rootFiles: [String: Watched<ExecutionModes>] = [:]
    private var gemini: Watched<ConfiguredModes.GeminiEfforts>?
    private var workBuddy: [String: WorkBuddyStore] = [:]

    /// `roots`: the enabled roots; a disabled source's files are not read.
    init(roots: [(family: TranscriptFamily, path: String)], geminiSettings: String) {
        for root in roots {
            switch root.family {
            case .codex:
                rootFiles[root.path] = Watched(path: ConfiguredModes.codexConfigPath(sessionsRoot: root.path), empty: .empty,
                                               read: ConfiguredModes.codex(path:))
            case .antigravity:
                rootFiles[root.path] = Watched(path: TranscriptLocations.antigravityConfig(ofRoot: root.path), empty: .empty,
                                               read: ConfiguredModes.antigravity(path:))
            case .workbuddy, .workbuddyAi:
                let folder = TranscriptLocations.workbuddyDataFolder(ofRoot: root.path)
                rootFiles[root.path] = Watched(path: (folder as NSString).appendingPathComponent("settings.json"), empty: .empty,
                                               read: ConfiguredModes.workBuddy(path:))
                workBuddy[root.path] = WorkBuddyStore(path: (folder as NSString).appendingPathComponent("workbuddy.db"))
            case .gemini:
                gemini = Watched(path: geminiSettings, empty: ConfiguredModes.GeminiEfforts(), read: ConfiguredModes.gemini(path:))
            default:
                break
            }
        }
    }

    /// `workBuddySessions`: the main sessions of each WorkBuddy root. True when any value changed.
    func refresh(workBuddySessions: [String: Set<String>] = [:]) -> Bool {
        var changed = false
        for root in rootFiles.keys where rootFiles[root]?.refresh() == true { changed = true }
        if gemini?.refresh() == true { changed = true }
        for root in workBuddy.keys where workBuddy[root]?.refresh(sessions: workBuddySessions[root] ?? []) == true { changed = true }
        return changed
    }

    /// The configured facts of an agent found under `root`; Gemini's depend on its model.
    func configured(family: TranscriptFamily, root: String, model: String?) -> ExecutionModes {
        family == .gemini ? gemini?.value.modes(model: model) ?? .empty : rootFiles[root]?.value ?? .empty
    }

    /// The session-current fact of a WorkBuddy transcript.
    func session(root: String, sessionId: String) -> ExecutionModes {
        workBuddy[root]?.values[sessionId] ?? .empty
    }

    /// A source's configured value, where it has one per source.
    func sourceConfigured(root: String?) -> ExecutionModes {
        root.flatMap { rootFiles[$0]?.value } ?? .empty
    }
}
