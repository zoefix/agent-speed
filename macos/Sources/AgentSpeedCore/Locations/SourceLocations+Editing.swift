import Foundation

/// Editing a location list (METRICS 2.1, DESIGN 5): add, remove, reset, with the limits and the same-location rules.
extension SourceLocations {
    public enum AddOutcome: Equatable, Sendable {
        /// Appended as a user location.
        case added
        /// The default location while the automatic entry was absent: the automatic entry is back, first.
        case restoredAutomatic
        /// The same location is an entry already; nothing changed.
        case alreadyListed
        /// The automatic entry reads this location already; nothing changed.
        case alreadyAutomatic
        /// The list has `maxEntries` entries; nothing changed.
        case full
        /// Not an absolute path; nothing changed.
        case invalid

        public var changed: Bool { self == .added || self == .restoredAutomatic }
    }

    /// What a folder's automatic entry reads now, as location keys; the first is the default location's.
    public struct AutomaticKeys: Equatable, Sendable {
        public var keys: [String]

        public init(keys: [String]) { self.keys = keys }

        public var defaultKey: String? { keys.first }
    }

    public func canAdd(to folder: TranscriptFolder) -> Bool { self[folder].count < Self.maxEntries }

    /// The only remaining entry cannot be removed: turning a source off is its toggle's job.
    public func canRemove(from folder: TranscriptFolder) -> Bool { self[folder].count > 1 }

    public func canReset(_ folder: TranscriptFolder) -> Bool { self[folder] != Self.automaticOnly }

    /// Adds a location the user picked. `automatic` defaults to what this process's environment derives.
    @discardableResult
    public mutating func add(_ path: String, to folder: TranscriptFolder, automatic: AutomaticKeys? = nil) -> AddOutcome {
        guard let path = Self.standardized(path) else { return .invalid }
        let key = Self.locationKey(path, folder: folder)
        let list = self[folder]
        if list.contains(where: { $0.path.map { Self.locationKey($0, folder: folder) } == key }) { return .alreadyListed }
        let automatic = automatic ?? Self.automaticKeys(of: folder)
        if list.contains(.automatic) {
            if automatic.keys.contains(key) { return .alreadyAutomatic }
        } else if !folder.automaticIsWiderThanDefault, key == automatic.defaultKey {
            guard list.count < Self.maxEntries else { return .full }
            self[folder] = [.automatic] + list
            return .restoredAutomatic
        }
        guard list.count < Self.maxEntries else { return .full }
        self[folder] = list + [.path(path)]
        return .added
    }

    /// Removes an entry, unless it is the only one.
    public mutating func remove(_ location: DataLocation, from folder: TranscriptFolder) {
        guard canRemove(from: folder) else { return }
        self[folder] = self[folder].filter { $0 != location }
    }

    /// Back to `[automatic]`.
    public mutating func reset(_ folder: TranscriptFolder) {
        self[folder] = Self.automaticOnly
    }

    // MARK: - Same location

    /// The root or database a location names, resolved: two locations are the same when their keys are equal — a
    /// WorkBuddy data folder and its `projects`, a `.factory` folder and its `sessions`, a Kimi Code CLI or Grok Build
    /// data directory and its `sessions`, an Antigravity store and its `conversations`, an OpenCode folder and its
    /// `opencode.db`, a ZCode data directory, its database folder and `db.sqlite`, an OpenClaw agent folder and its
    /// `openclaw-agent.sqlite`, a Devin data folder and its `cli` folder, a Qoder IDE data folder, its
    /// `SharedClientCache` folder and its `cache/db` folder, a Qoder CLI config folder and its `projects` folder.
    public static func locationKey(_ path: String, folder: TranscriptFolder) -> String {
        if folder == .devin { return DevinRoot.location(path).locationKey }
        if folder == .qoder { return QoderRoot.location(path).locationKey }
        return LocationPaths.resolved(namedRoot(path, folder: folder))
    }

    static func namedRoot(_ path: String, folder: TranscriptFolder) -> String {
        if let family = TranscriptFamily(folder: folder) { return MonitorConfiguration.userRoot(path, family: family).path }
        switch folder {
        case .opencode: return OpencodeDatabase.databasePath(forRoot: path)
        case .zcode: return ZcodeDatabase.databasePath(forRoot: path)
        case .openclaw:
            let database = (path as NSString).appendingPathComponent(OpenClawHome.databaseName)
            return FileStat.lstat(database)?.isRegularFile == true ? database : path
        default: return path
        }
    }

    /// The keys of what a folder's automatic entry reads, in the configuration's environment (tests set one).
    public static func automaticKeys(of folder: TranscriptFolder,
                                     configuration: MonitorConfiguration = MonitorConfiguration()) -> AutomaticKeys {
        let roots: [String]
        if let family = TranscriptFamily(folder: folder) {
            roots = configuration.automaticRoots(of: family).map(\.path)
        } else {
            switch folder {
            case .opencode: roots = [OpencodeDatabase.databasePath(forRoot: TranscriptLocations.defaultOpencodeRoot.path)]
            case .zcode: roots = [ZcodeDatabase.databasePath(forRoot: TranscriptLocations.defaultZcodeRoot.path)]
            case .openclaw: roots = (configuration.openclawHome ?? .current).stateFolders.map { namedRoot($0.path, folder: .openclaw) }
            case .hermes: roots = [(configuration.hermesHome ?? .current).folder]
            case .devin: roots = (configuration.devinAutomaticRoots ?? TranscriptLocations.defaultDevinRoots).map(\.locationKey)
            case .qoder: roots = (configuration.qoderAutomaticRoots ?? TranscriptLocations.defaultQoderRoots).map(\.locationKey)
            default: roots = []
            }
        }
        return AutomaticKeys(keys: LocationPaths.unique(roots.map(LocationPaths.resolved)))
    }
}

extension TranscriptFamily {
    /// The file-based family a folder's list feeds; nil for OpenCode, ZCode, OpenClaw and Hermes Agent.
    init?(folder: TranscriptFolder) {
        guard let family = TranscriptFamily.allCases.first(where: { $0.folder == folder }) else { return nil }
        self = family
    }
}
