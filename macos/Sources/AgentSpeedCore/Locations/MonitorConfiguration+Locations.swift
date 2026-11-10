import Foundation

extension TranscriptFamily {
    /// The folder whose location list this family's roots come from.
    var folder: TranscriptFolder {
        switch self {
        case .codex: .codex
        case .claude: .claude
        case .dsh: .dsh
        case .gemini: .gemini
        case .omp: .omp
        case .antigravity: .antigravity
        case .workbuddy: .workbuddy
        case .workbuddyAi: .workbuddyAi
        case .kimi: .kimi
        case .grokBuild: .grokBuild
        case .pi: .pi
        case .droid: .droid
        case .qoderCli: .qoderCli
        }
    }
}

/// A Hermes Agent root (METRICS 2, 4.15): the automatic home, or a user location (a home or one `logs` folder).
struct HermesRoot: Equatable, Sendable {
    var path: String
    var isSetting: Bool

    /// The log folders it names now.
    var logFolders: [String] { isSetting ? HermesHome.settingFolders(path) : HermesHome.logFolders(ofHome: path) }
}

/// How the location lists become roots (METRICS 2.1): every entry names roots, a source reads the union in list order,
/// and a root or database named twice is read once.
extension MonitorConfiguration {
    // MARK: - File-based families

    /// The root a user location names for a file-based family: an Antigravity store's `conversations` folder, a
    /// WorkBuddy data folder's `projects`, a Kimi Code CLI or Grok Build data directory's `sessions`, a `.factory`
    /// folder's `sessions`; any other location is the root itself.
    static func userRoot(_ path: String, family: TranscriptFamily) -> URL {
        let url = URL(fileURLWithPath: path, isDirectory: true)
        switch family {
        case .antigravity: return TranscriptLocations.antigravityConversations(url)
        case .workbuddy, .workbuddyAi: return TranscriptLocations.workbuddyProjects(url)
        case .kimi, .grokBuild: return TranscriptLocations.sessionsFolder(url)
        case .droid: return DroidPaths.sessionsFolder(url)
        case .qoderCli: return QoderPaths.cliProjectsFolder(url)
        case .codex, .claude, .dsh, .gemini, .omp, .pi: return url
        }
    }

    /// The automatic roots of a file-based family (METRICS 2). Codex's include the agent-scoped Codex home next to every
    /// OpenClaw agent database, so a list without the automatic entry reads none of them.
    func automaticRoots(of family: TranscriptFamily) -> [URL] {
        let replaced = automaticRoots[family].map { urls in
            switch family {
            case .antigravity: urls.map(TranscriptLocations.antigravityConversations)
            case .workbuddy, .workbuddyAi: urls.map(TranscriptLocations.workbuddyProjects)
            case .kimi, .grokBuild: urls.map(TranscriptLocations.sessionsFolder)
            case .qoderCli: urls.map(QoderPaths.cliProjectsFolder)
            default: urls
            }
        }
        let fallback = family == .pi ? piHome?.roots : nil
        var urls = replaced ?? fallback ?? TranscriptLocations.defaultRoots(for: family)
        if family == .codex { urls += openclawCodexRoots.map { URL(fileURLWithPath: $0, isDirectory: true) } }
        return urls
    }

    /// The roots one entry of a family's list names.
    func roots(of location: DataLocation, family: TranscriptFamily) -> [URL] {
        switch location {
        case .automatic: automaticRoots(of: family)
        case .path(let path): [Self.userRoot(path, family: family)]
        }
    }

    /// Every root of a family: the union of its entries' roots in list order, normalized, each once.
    func resolvedRoots(for family: TranscriptFamily) -> [String] {
        LocationPaths.unique(locations[family.folder].flatMap { roots(of: $0, family: family) }.map(\.path))
    }

    /// Whether a resolved root may be read at all: an Antigravity `*-backup` store never is (METRICS 4.8).
    static func isReadable(root: String, family: TranscriptFamily) -> Bool {
        family != .antigravity || TranscriptLocations.isReadableAntigravityRoot(root)
    }

    /// Every resolved root of every family, and whether it is read: never an Antigravity `*-backup` store, and never an
    /// omp root that is a Pi root or lies inside one, which holds Pi's files (METRICS 4.7), also while Pi is off.
    func allRoots() -> [(family: TranscriptFamily, path: String, readable: Bool)] {
        let pi = resolvedRoots(for: .pi)
        return TranscriptFamily.allCases.flatMap { family in
            (family == .pi ? pi : resolvedRoots(for: family)).map { root in
                (family, root, Self.isReadable(root: root, family: family, piRoots: pi))
            }
        }
    }

    static func isReadable(root: String, family: TranscriptFamily, piRoots: [String]) -> Bool {
        isReadable(root: root, family: family) && (family != .omp || !PiHome.contains(piRoots, root))
    }

    /// The `archived_sessions` folder next to a Codex sessions root (METRICS 8).
    static func codexArchiveRoot(of root: String) -> String {
        ((root as NSString).deletingLastPathComponent as NSString).appendingPathComponent("archived_sessions")
    }

    /// Gemini CLI's user settings for each of its roots (EXECUTION-MODES, Configured values): the automatic roots
    /// share `<home>/.gemini/settings.json`; a user location's file is the `settings.json` next to its `tmp` folder.
    var resolvedGeminiSettings: [String: String] {
        var settings: [String: String] = [:]
        for location in locations[.gemini] {
            let file = switch location {
            case .automatic:
                automaticRoots[.gemini]?.first.map { Self.geminiSettings(besideRoot: $0.path) } ?? TranscriptLocations.defaultGeminiSettings
            case .path(let path):
                Self.geminiSettings(besideRoot: path)
            }
            for root in roots(of: location, family: .gemini) {
                let key = TranscriptDiscovery.normalizedRoot(root.path)
                if settings[key] == nil { settings[key] = file }
            }
        }
        return settings
    }

    /// `settings.json` in the folder that holds a `tmp` root, or in the root itself when it has another name.
    static func geminiSettings(besideRoot root: String) -> String {
        let folder = TranscriptDiscovery.normalizedRoot(root)
        let home = (folder as NSString).lastPathComponent == "tmp" ? (folder as NSString).deletingLastPathComponent : folder
        return (home as NSString).appendingPathComponent("settings.json")
    }

    // MARK: - Database and log sources

    /// OpenCode's roots, one per database: the automatic data folder and every user location (a folder that contains
    /// `opencode.db`, or the file). Two roots naming the same database file are read once.
    var resolvedOpencodeRoots: [String] {
        LocationPaths.uniqueDatabases(locations[.opencode].map { $0.path ?? TranscriptLocations.defaultOpencodeRoot.path },
                                      database: OpencodeDatabase.databasePath(forRoot:))
    }

    /// ZCode's roots, one per database: the automatic data folder and every user location (a data directory, its
    /// database folder, or `db.sqlite`).
    var resolvedZcodeRoots: [String] {
        LocationPaths.uniqueDatabases(locations[.zcode].map { $0.path ?? TranscriptLocations.defaultZcodeRoot.path },
                                      database: ZcodeDatabase.databasePath(forRoot:))
    }

    /// OpenClaw's roots: every automatic state folder and every user location (a state folder, an agent folder or a
    /// database), each once (METRICS 2, 4.14).
    var resolvedOpenclawRoots: [String] {
        LocationPaths.unique(locations[.openclaw].flatMap { location -> [String] in
            switch location {
            case .automatic: (openclawHome ?? .current).stateFolders.map(\.path)
            case .path(let path): [path]
            }
        })
    }

    /// The agent-scoped Codex homes next to every OpenClaw agent database: automatic Codex roots (METRICS 2, 4.14),
    /// also while OpenClaw itself is off.
    var openclawCodexRoots: [String] {
        var seen: Set<String> = []
        return resolvedOpenclawRoots.flatMap(OpenClawHome.databases(in:))
            .map(OpenClawHome.codexRoot(ofDatabase:))
            .filter { seen.insert($0).inserted }
    }

    /// Hermes Agent's roots: the automatic home and every user location, each once (METRICS 2, 4.15).
    var resolvedHermesRoots: [HermesRoot] {
        var seen: Set<String> = []
        return locations[.hermes].compactMap { location in
            let root = switch location {
            case .automatic: HermesRoot(path: (hermesHome ?? .current).folder, isSetting: false)
            case .path(let path): HermesRoot(path: TranscriptDiscovery.normalizedRoot(path), isSetting: true)
            }
            return seen.insert(root.path).inserted ? root : nil
        }
    }

    /// The log folders Hermes Agent's roots name now, each once.
    var resolvedHermesFolders: [String] {
        var seen: Set<String> = []
        return resolvedHermesRoots.flatMap(\.logFolders).filter { seen.insert($0).inserted }
    }

    /// Devin's roots (METRICS 2, 4.17): the automatic CLI database and every user location, each once.
    var resolvedDevinRoots: [DevinRoot] {
        var seen: Set<DevinRoot> = []
        return locations[.devin].flatMap { location -> [DevinRoot] in
            switch location {
            case .automatic: devinAutomaticRoots ?? TranscriptLocations.defaultDevinRoots
            case .path(let path): [.location(TranscriptDiscovery.normalizedRoot(path))]
            }
        }.filter { seen.insert($0).inserted }
    }

    /// Qoder IDE's roots (METRICS 2, 4.18): the automatic database of each edition and every user location, each once.
    var resolvedQoderRoots: [QoderRoot] {
        var seen: Set<QoderRoot> = []
        return locations[.qoder].flatMap { location -> [QoderRoot] in
            switch location {
            case .automatic: qoderAutomaticRoots ?? TranscriptLocations.defaultQoderRoots
            case .path(let path): [.location(TranscriptDiscovery.normalizedRoot(path))]
            }
        }.filter { seen.insert($0).inserted }
    }

    /// The zone Hermes's wall-clock times are read in: this computer's, unless a test sets one.
    var resolvedHermesTimeZone: TimeZone { hermesTimeZone ?? .autoupdatingCurrent }

    // MARK: - Sources

    var readsOpenclaw: Bool { enabledSources.contains(.openclaw) }
    var readsHermes: Bool { enabledSources.contains(.hermes) }
    var readsOpencode: Bool { enabledSources.contains(.opencode) }
    var readsZcode: Bool { enabledSources.contains(.zcode) }
    var readsDevin: Bool { enabledSources.contains(.devin) }
    var readsQoder: Bool { enabledSources.contains(.qoder) }

    func reads(_ family: TranscriptFamily) -> Bool {
        family.sources.contains { enabledSources.contains($0) }
    }

    /// True when switching between the two configurations requires reading the transcripts again: when what is read
    /// differs, not merely how the lists say it (a user location equal to the only automatic root reads the same).
    func requiresRescan(comparedTo other: MonitorConfiguration) -> Bool {
        enabledSources != other.enabledSources
            || TranscriptFamily.allCases.contains { resolvedRoots(for: $0) != other.resolvedRoots(for: $0) }
            || resolvedGeminiSettings != other.resolvedGeminiSettings
            || resolvedOpencodeRoots != other.resolvedOpencodeRoots
            || resolvedZcodeRoots != other.resolvedZcodeRoots
            || resolvedOpenclawRoots != other.resolvedOpenclawRoots
            || resolvedHermesRoots != other.resolvedHermesRoots
            || resolvedDevinRoots != other.resolvedDevinRoots
            || resolvedQoderRoots != other.resolvedQoderRoots
    }
}

enum LocationPaths {
    /// Normalized paths in order, each once.
    static func unique(_ paths: [String]) -> [String] {
        var seen: Set<String> = []
        return paths.map(TranscriptDiscovery.normalizedRoot).filter { seen.insert($0).inserted }
    }

    /// Normalized roots in order, without a later root whose database resolves to the same file as an earlier one.
    static func uniqueDatabases(_ roots: [String], database: (String) -> String) -> [String] {
        var seen: Set<String> = []
        return roots.map(TranscriptDiscovery.normalizedRoot).filter { seen.insert(resolved(database($0))).inserted }
    }

    /// The real path of an existing file or folder (symbolic links resolved), else the path itself.
    static func resolved(_ path: String) -> String {
        guard let resolved = realpath(path, nil) else { return TranscriptDiscovery.normalizedRoot(path) }
        defer { free(resolved) }
        return String(cString: resolved)
    }
}
