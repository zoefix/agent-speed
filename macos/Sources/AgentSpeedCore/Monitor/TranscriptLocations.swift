import Foundation

/// Default data locations (METRICS 2). Safe to call from any thread.
enum TranscriptLocations {
    static var defaultCodexRoot: URL {
        if let home = environment("CODEX_HOME") {
            return URL(fileURLWithPath: home, isDirectory: true).appendingPathComponent("sessions", isDirectory: true)
        }
        return home.appendingPathComponent(".codex/sessions", isDirectory: true)
    }

    static var defaultClaudeRoot: URL {
        if let directory = environment("CLAUDE_CONFIG_DIR") {
            return URL(fileURLWithPath: directory, isDirectory: true).appendingPathComponent("projects", isDirectory: true)
        }
        return home.appendingPathComponent(".claude/projects", isDirectory: true)
    }

    static var defaultDshRoot: URL {
        if let home = environment("DSH_HOME") {
            return URL(fileURLWithPath: home, isDirectory: true).appendingPathComponent("sessions", isDirectory: true)
        }
        return home.appendingPathComponent(".dsh/sessions", isDirectory: true)
    }

    /// The folder that contains `opencode.db`.
    static var defaultOpencodeRoot: URL {
        if let data = environment("XDG_DATA_HOME") {
            return URL(fileURLWithPath: data, isDirectory: true).appendingPathComponent("opencode", isDirectory: true)
        }
        return home.appendingPathComponent(".local/share/opencode", isDirectory: true)
    }

    /// Gemini CLI's `tmp` folders: `<home>/.gemini/tmp` and, for its macOS seatbelt sandbox (v0.63+),
    /// `<home>/.cache/.gemini/tmp`, where `<home>` is `$GEMINI_CLI_HOME`, else the user's home (METRICS 2).
    static var defaultGeminiRoots: [URL] {
        geminiRoots(cliHome: environment("GEMINI_CLI_HOME"))
    }

    static func geminiRoots(cliHome: String?) -> [URL] {
        let base = cliHome.map { URL(fileURLWithPath: $0, isDirectory: true) } ?? home
        return [
            base.appendingPathComponent(".gemini/tmp", isDirectory: true),
            base.appendingPathComponent(".cache/.gemini/tmp", isDirectory: true),
        ]
    }

    /// omp's automatic roots (METRICS 2, 4.7): the default root, then one per named profile, without duplicates.
    static var defaultOmpRoots: [URL] {
        ompRoots(environment: ProcessInfo.processInfo.environment, home: home)
    }

    /// `<cfg>` = `$PI_CONFIG_DIR`, else `.omp`. The default root is `$PI_CODING_AGENT_DIR/sessions`, else
    /// `$XDG_DATA_HOME/omp/sessions` when `$XDG_DATA_HOME/omp` exists, else `~/<cfg>/agent/sessions`. Profiles are the
    /// folders in `~/<cfg>/profiles` and `$XDG_DATA_HOME/omp/profiles`; each reads `$XDG_DATA_HOME/omp/profiles/<name>/
    /// sessions` when that folder exists, else `~/<cfg>/profiles/<name>/agent/sessions`.
    static func ompRoots(environment: [String: String], home: URL) -> [URL] {
        func value(_ name: String) -> String? {
            guard let value = environment[name], !value.isEmpty else { return nil }
            return value
        }
        let configName = String((value("PI_CONFIG_DIR") ?? ".omp").drop(while: { $0 == "/" || $0 == "\\" }))
        let config = home.appendingPathComponent(configName, isDirectory: true)
        let xdg = value("XDG_DATA_HOME").map {
            URL(fileURLWithPath: $0, isDirectory: true).appendingPathComponent("omp", isDirectory: true)
        }
        var roots: [URL]
        if let agent = value("PI_CODING_AGENT_DIR") {
            roots = [URL(fileURLWithPath: agent, isDirectory: true).appendingPathComponent("sessions", isDirectory: true)]
        } else if let xdg, isDirectory(xdg.path) {
            roots = [xdg.appendingPathComponent("sessions", isDirectory: true)]
        } else {
            roots = [config.appendingPathComponent("agent/sessions", isDirectory: true)]
        }
        var names = Set(subdirectories(of: config.appendingPathComponent("profiles", isDirectory: true)))
        if let xdg { names.formUnion(subdirectories(of: xdg.appendingPathComponent("profiles", isDirectory: true))) }
        for name in names.sorted(by: { $0.unicodeScalars.lexicographicallyPrecedes($1.unicodeScalars) }) {
            if let profile = xdg?.appendingPathComponent("profiles", isDirectory: true).appendingPathComponent(name, isDirectory: true),
               isDirectory(profile.path) {
                roots.append(profile.appendingPathComponent("sessions", isDirectory: true))
            } else {
                roots.append(config.appendingPathComponent("profiles", isDirectory: true)
                    .appendingPathComponent(name, isDirectory: true).appendingPathComponent("agent/sessions", isDirectory: true))
            }
        }
        var seen: Set<String> = []
        return roots.filter { seen.insert(TranscriptDiscovery.normalizedRoot($0.standardizedFileURL.path)).inserted }
    }

    /// Antigravity's store folders (METRICS 2, 4.8): CLI, desktop app and IDE extensions, IDE 2.x. No environment
    /// variable moves them.
    static var defaultAntigravityStores: [URL] {
        let gemini = home.appendingPathComponent(".gemini", isDirectory: true)
        return ["antigravity-cli", "antigravity", "antigravity-ide"].map { gemini.appendingPathComponent($0, isDirectory: true) }
    }

    /// The `conversations` folder of a store; a folder that is itself called `conversations` is used directly.
    static func antigravityConversations(_ folder: URL) -> URL {
        let normalized = URL(fileURLWithPath: TranscriptDiscovery.normalizedRoot(folder.path), isDirectory: true)
        if normalized.lastPathComponent == "conversations" { return normalized }
        return normalized.appendingPathComponent("conversations", isDirectory: true)
    }

    /// False for the `conversations` folder of a `*-backup` store, which is never read (METRICS 4.8).
    static func isReadableAntigravityRoot(_ conversations: String) -> Bool {
        let store = (TranscriptDiscovery.normalizedRoot(conversations) as NSString).deletingLastPathComponent
        return !(store as NSString).lastPathComponent.hasSuffix("-backup")
    }

    /// WorkBuddy's data folders (METRICS 2, 4.9). `WORKBUDDY_CONFIG_DIR` and `CODEBUDDY_CONFIG_DIR` are not honoured.
    static var defaultWorkBuddyDataFolder: URL { home.appendingPathComponent(".workbuddy", isDirectory: true) }
    static var defaultWorkBuddyAiDataFolder: URL { home.appendingPathComponent(".workbuddy-ai", isDirectory: true) }

    /// The `projects` folder of a WorkBuddy data folder; a folder that is itself called `projects` is used directly.
    static func workbuddyProjects(_ folder: URL) -> URL {
        var path = folder.path
        while path.count > 1, path.hasSuffix("/") || path.hasSuffix("\\") { path.removeLast() }
        let normalized = URL(fileURLWithPath: path, isDirectory: true)
        if normalized.lastPathComponent == "projects" { return normalized }
        return normalized.appendingPathComponent("projects", isDirectory: true)
    }

    /// The data folder whose `projects` folder is `root`: a WorkBuddy source is detected when it exists.
    static func workbuddyDataFolder(ofRoot root: String) -> String {
        (TranscriptDiscovery.normalizedRoot(root) as NSString).deletingLastPathComponent
    }

    /// Trae IDE and SOLO editions share a store format and one source id.
    static var defaultTraeRoots: [URL] {
        let support = home.appendingPathComponent("Library/Application Support", isDirectory: true)
        return ["Trae", "Trae CN", "TRAE SOLO", "TRAE SOLO CN"].map { support.appendingPathComponent($0, isDirectory: true) }
    }

    static var defaultZcodeRoot: URL {
        let base = environment("ZCODE_DATA_BASE_DIR").map { URL(fileURLWithPath: $0, isDirectory: true) } ?? home
        return base.appendingPathComponent(".zcode/cli/db", isDirectory: true)
    }

    static var defaultKimiRoot: URL {
        let base = environment("KIMI_CODE_HOME").map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? home.appendingPathComponent(".kimi-code", isDirectory: true)
        return base.appendingPathComponent("sessions", isDirectory: true)
    }

    static var defaultGrokBuildRoot: URL {
        let base = environment("GROK_HOME").map { URL(fileURLWithPath: $0, isDirectory: true) }
            ?? home.appendingPathComponent(".grok", isDirectory: true)
        return base.appendingPathComponent("sessions", isDirectory: true)
    }

    /// Pi's automatic roots (METRICS 2, 4.13) for this process's environment and home folder.
    static var defaultPiRoots: [URL] { PiHome.current.roots }

    /// OpenClaw's state folders (METRICS 2, 4.14) for this process's environment and home folder.
    static var defaultOpenclawRoots: [URL] { OpenClawHome.current.stateFolders }

    /// The Hermes Agent home (METRICS 2, 4.15) for this process's environment and home folder.
    static var defaultHermesHome: URL { URL(fileURLWithPath: HermesHome.current.folder, isDirectory: true) }

    /// Factory Droid's sessions folder (METRICS 2, 4.16); no environment variable moves it.
    static var defaultDroidRoot: URL { DroidPaths.defaultRoot(home: home) }

    static func sessionsFolder(_ folder: URL) -> URL {
        folder.lastPathComponent == "sessions" ? folder : folder.appendingPathComponent("sessions", isDirectory: true)
    }

    /// Default roots of a family; Gemini CLI has two, omp one per profile, Antigravity one per store.
    static func defaultRoots(for family: TranscriptFamily) -> [URL] {
        switch family {
        case .codex: [defaultCodexRoot]
        case .claude: [defaultClaudeRoot]
        case .dsh: [defaultDshRoot]
        case .gemini: defaultGeminiRoots
        case .omp: defaultOmpRoots
        case .antigravity: defaultAntigravityStores.map(antigravityConversations)
        case .workbuddy: [workbuddyProjects(defaultWorkBuddyDataFolder)]
        case .workbuddyAi: [workbuddyProjects(defaultWorkBuddyAiDataFolder)]
        case .kimi: [defaultKimiRoot]
        case .grokBuild: [defaultGrokBuildRoot]
        case .pi: defaultPiRoots
        case .droid: [defaultDroidRoot]
        }
    }

    private static var home: URL { FileManager.default.homeDirectoryForCurrentUser }

    private static func environment(_ name: String) -> String? {
        guard let value = ProcessInfo.processInfo.environment[name], !value.isEmpty else { return nil }
        return value
    }

    private static func isDirectory(_ path: String) -> Bool {
        FileStat.stat(path)?.isDirectory == true
    }

    /// Names of the real (not symlinked) folders inside `folder`.
    private static func subdirectories(of folder: URL) -> [String] {
        guard let names = try? FileManager.default.contentsOfDirectory(atPath: folder.path) else { return [] }
        return names.filter { FileStat.lstat(folder.appendingPathComponent($0).path)?.isDirectory == true }
    }
}

extension MonitorConfiguration {
    /// The configured root of a transcript family, nil for the default.
    func configuredRoot(for family: TranscriptFamily) -> URL? {
        switch family {
        case .codex: codexRoot
        case .claude: claudeRoot
        case .dsh: dshRoot
        case .gemini: geminiRoot
        case .omp: ompRoot
        case .antigravity: antigravityRoot.map(TranscriptLocations.antigravityConversations)
        case .workbuddy: workbuddyRoot.map(TranscriptLocations.workbuddyProjects)
        case .workbuddyAi: workbuddyAiRoot.map(TranscriptLocations.workbuddyProjects)
        case .kimi: kimiRoot.map(TranscriptLocations.sessionsFolder)
        case .grokBuild: grokBuildRoot.map(TranscriptLocations.sessionsFolder)
        case .pi: piRoot
        case .droid: droidRoot.map(DroidPaths.sessionsFolder)
        }
    }

    /// Every root of a family: the configured one, else the defaults.
    func resolvedRoots(for family: TranscriptFamily) -> [String] {
        let automatic = automaticRoots[family].map { urls in
            switch family {
            case .antigravity: urls.map(TranscriptLocations.antigravityConversations)
            case .workbuddy, .workbuddyAi: urls.map(TranscriptLocations.workbuddyProjects)
            case .kimi, .grokBuild: urls.map(TranscriptLocations.sessionsFolder)
            default: urls
            }
        }
        let fallback = family == .pi ? piHome?.roots : nil
        var urls = configuredRoot(for: family).map { [$0] } ?? automatic ?? fallback ?? TranscriptLocations.defaultRoots(for: family)
        if family == .codex, configuredRoot(for: .codex) == nil {
            // OpenClaw's agent-scoped Codex homes are automatic Codex roots; a Codex setting replaces them too.
            urls += openclawCodexRoots.map { URL(fileURLWithPath: $0, isDirectory: true) }
        }
        var roots: [String] = []
        for url in urls {
            let root = TranscriptDiscovery.normalizedRoot(url.path)
            if !roots.contains(root) { roots.append(root) }
        }
        return roots
    }

    /// The first root of a family.
    func resolvedRoot(for family: TranscriptFamily) -> String {
        resolvedRoots(for: family)[0]
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

    /// OpenClaw's roots: the configured folder or database, else every state folder (METRICS 2, 4.14).
    var resolvedOpenclawRoots: [String] {
        let urls = openclawRoot.map { [$0] } ?? (openclawHome ?? .current).stateFolders
        var roots: [String] = []
        for url in urls {
            let root = TranscriptDiscovery.normalizedRoot(url.path)
            if !roots.contains(root) { roots.append(root) }
        }
        return roots
    }

    /// The agent-scoped Codex homes next to every OpenClaw agent database: automatic Codex roots (METRICS 2, 4.14),
    /// also while OpenClaw itself is off.
    var openclawCodexRoots: [String] {
        var seen: Set<String> = []
        return resolvedOpenclawRoots.flatMap(OpenClawHome.databases(in:))
            .map(OpenClawHome.codexRoot(ofDatabase:))
            .filter { seen.insert($0).inserted }
    }

    var readsOpenclaw: Bool { enabledSources.contains(.openclaw) }

    /// The Hermes Agent home, or the folder of the setting (a home or one log folder), and whether it is the setting
    /// (METRICS 2, 4.15).
    var resolvedHermesRoot: (path: String, isSetting: Bool) {
        if let hermesRoot { return (TranscriptDiscovery.normalizedRoot(hermesRoot.path), true) }
        return ((hermesHome ?? .current).folder, false)
    }

    /// The log folders Hermes Agent's root names now: the home's own and every profile's, or the setting's.
    var resolvedHermesFolders: [String] {
        let root = resolvedHermesRoot
        return root.isSetting ? HermesHome.settingFolders(root.path) : HermesHome.logFolders(ofHome: root.path)
    }

    /// The zone Hermes's wall-clock times are read in: this computer's, unless a test sets one.
    var resolvedHermesTimeZone: TimeZone { hermesTimeZone ?? .autoupdatingCurrent }

    var readsHermes: Bool { enabledSources.contains(.hermes) }

    func reads(_ family: TranscriptFamily) -> Bool {
        family.sources.contains { enabledSources.contains($0) }
    }

    var resolvedCodexRoot: String { resolvedRoot(for: .codex) }
    var resolvedClaudeRoot: String { resolvedRoot(for: .claude) }
    var resolvedDshRoot: String { resolvedRoot(for: .dsh) }

    var resolvedOpencodeRoot: String {
        TranscriptDiscovery.normalizedRoot((opencodeRoot ?? TranscriptLocations.defaultOpencodeRoot).path)
    }

    /// Codex archives finished sessions next to the sessions folder (METRICS 8).
    var resolvedCodexArchiveRoot: String { Self.codexArchiveRoot(of: resolvedCodexRoot) }

    /// The `archived_sessions` folder next to a Codex sessions root.
    static func codexArchiveRoot(of root: String) -> String {
        ((root as NSString).deletingLastPathComponent as NSString).appendingPathComponent("archived_sessions")
    }

    var readsCodex: Bool { reads(.codex) }
    var readsClaude: Bool { reads(.claude) }
    var readsDsh: Bool { reads(.dsh) }
    var readsOpencode: Bool { enabledSources.contains(.opencode) }
    var readsTrae: Bool { enabledSources.contains(.trae) }
    var resolvedTraeRoots: [String] {
        (traeRoot.map { [$0] } ?? TranscriptLocations.defaultTraeRoots).map { TranscriptDiscovery.normalizedRoot($0.path) }
    }
    var readsZcode: Bool { enabledSources.contains(.zcode) }
    var resolvedZcodeRoot: String { TranscriptDiscovery.normalizedRoot((zcodeRoot ?? TranscriptLocations.defaultZcodeRoot).path) }

    /// True when switching between the two configurations requires reading the transcripts again.
    func requiresRescan(comparedTo other: MonitorConfiguration) -> Bool {
        enabledSources != other.enabledSources
            || TranscriptFamily.allCases.contains { resolvedRoots(for: $0) != other.resolvedRoots(for: $0) }
            || resolvedOpencodeRoot != other.resolvedOpencodeRoot
            || resolvedZcodeRoot != other.resolvedZcodeRoot
            || resolvedOpenclawRoots != other.resolvedOpenclawRoots
            || resolvedHermesRoot != other.resolvedHermesRoot
            || resolvedTraeRoots != other.resolvedTraeRoots
    }
}
