import AgentSpeedCore
import AppKit

/// A data folder the user can relocate. Claude Code and Claude Desktop share the Claude root; OpenCode's folder is the
/// one that contains `opencode.db`; Gemini CLI reads one `tmp` folder, or by default two (an override replaces both);
/// omp reads its default `sessions` root and one per named profile, Antigravity three stores, Pi its sessions root and
/// its own custom session folder, OpenClaw every state folder (an override replaces all; OpenClaw's may also name an
/// agent folder or one database), Hermes Agent the log folders of its home (an override names a home or one `logs`
/// folder); each WorkBuddy edition reads the `projects` folder of its data folder, which the row names, and Factory
/// Droid the `sessions` folder of a chosen `.factory` folder, or the chosen folder itself.
enum TranscriptFolder: CaseIterable {
    case codex
    case claude
    case dsh
    case opencode
    case gemini
    case omp
    case antigravity
    case workbuddy
    case workbuddyAi
    case zcode
    case kimi
    case grokBuild
    case pi
    case openclaw
    case hermes
    case droid
    case trae

    /// Every default location, in reading order.
    var defaultURLs: [URL] {
        switch self {
        case .codex: [LiveMonitor.defaultCodexRoot]
        case .claude: [LiveMonitor.defaultClaudeRoot]
        case .dsh: [LiveMonitor.defaultDshRoot]
        case .opencode: [LiveMonitor.defaultOpencodeRoot]
        case .gemini: LiveMonitor.defaultGeminiRoots
        case .omp: LiveMonitor.defaultOmpRoots
        case .antigravity: LiveMonitor.defaultAntigravityStores
        case .workbuddy: [LiveMonitor.defaultWorkBuddyDataFolder]
        case .zcode: [LiveMonitor.defaultZcodeRoot]
        case .kimi: [LiveMonitor.defaultKimiRoot]
        case .grokBuild: [LiveMonitor.defaultGrokBuildRoot]
        case .trae: LiveMonitor.defaultTraeRoots
        case .workbuddyAi: [LiveMonitor.defaultWorkBuddyAiDataFolder]
        case .pi: LiveMonitor.defaultPiRoots
        case .openclaw: LiveMonitor.defaultOpenclawRoots
        case .hermes: [LiveMonitor.defaultHermesHome]
        case .droid: [LiveMonitor.defaultDroidRoot]
        }
    }

    var defaultURL: URL { defaultURLs[0] }

    var chooseMessageKey: String {
        switch self {
        case .codex: "mac.settings.sources.chooseCodex"
        case .claude: "mac.settings.sources.chooseClaude"
        case .dsh: "mac.settings.sources.chooseDsh"
        case .opencode: "mac.settings.sources.chooseOpencode"
        case .gemini: "mac.settings.sources.chooseGemini"
        case .omp: "mac.settings.sources.chooseOmp"
        case .antigravity: "mac.settings.sources.chooseAntigravity"
        case .workbuddy: "mac.settings.sources.chooseWorkbuddy"
        case .zcode: "mac.settings.sources.chooseZcode"
        case .kimi: "mac.settings.sources.chooseKimi"
        case .grokBuild: "mac.settings.sources.chooseGrokBuild"
        case .trae: "mac.settings.sources.chooseTrae"
        case .workbuddyAi: "mac.settings.sources.chooseWorkbuddyAi"
        case .pi: "mac.settings.sources.choosePi"
        case .openclaw: "mac.settings.sources.chooseOpenClaw"
        case .hermes: "mac.settings.sources.chooseHermes"
        case .droid: "mac.settings.sources.chooseDroid"
        }
    }

    /// What the section footer explains about the folder, if anything.
    var footnoteKey: String? {
        switch self {
        case .codex: nil
        case .claude: "settings.sources.sharedRoot"
        case .dsh: "settings.sources.dshHint"
        case .opencode: "settings.sources.opencodeHint"
        case .gemini: "settings.sources.geminiHint"
        case .omp: "settings.sources.ompHint"
        case .antigravity: "settings.sources.antigravityHint"
        case .workbuddy: "settings.sources.workbuddyHint"
        case .zcode: "settings.sources.zcodeHint"
        case .kimi: "settings.sources.kimiHint"
        case .grokBuild: "settings.sources.grokBuildHint"
        case .trae: "settings.sources.traeHint"
        case .workbuddyAi: "settings.sources.workbuddyAiHint"
        case .pi: "settings.sources.piHint"
        case .openclaw: "settings.sources.openclawHint"
        case .hermes: "settings.sources.hermesHint"
        case .droid: "settings.sources.droidHint"
        }
    }

    @MainActor
    func customURL(in settings: AppSettings) -> URL? {
        switch self {
        case .codex: settings.codexRoot
        case .claude: settings.claudeRoot
        case .dsh: settings.dshRoot
        case .opencode: settings.opencodeRoot
        case .gemini: settings.geminiRoot
        case .omp: settings.ompRoot
        case .antigravity: settings.antigravityRoot
        case .workbuddy: settings.workbuddyRoot
        case .zcode: settings.zcodeRoot
        case .kimi: settings.kimiRoot
        case .grokBuild: settings.grokBuildRoot
        case .trae: settings.traeRoot
        case .workbuddyAi: settings.workbuddyAiRoot
        case .pi: settings.piRoot
        case .openclaw: settings.openclawRoot
        case .hermes: settings.hermesRoot
        case .droid: settings.droidRoot
        }
    }

    @MainActor
    func url(in settings: AppSettings) -> URL {
        customURL(in: settings) ?? defaultURL
    }

    /// The folders read with the current settings: the custom one, else every default.
    @MainActor
    func urls(in settings: AppSettings) -> [URL] {
        customURL(in: settings).map { [$0] } ?? defaultURLs
    }

    /// The folders the settings row names (DESIGN section 5): the custom one, else the defaults. omp and Pi name only
    /// their default root although profiles and Pi's custom session folder are read too; Antigravity names the stores
    /// found, or all three when none is; OpenClaw the state folders found, or `~/.openclaw` when none is.
    @MainActor
    func shownURLs(in settings: AppSettings) -> [URL] {
        if let custom = customURL(in: settings) { return [custom] }
        switch self {
        case .omp:
            return [LiveMonitor.defaultOmpRoot]
        case .pi:
            return [defaultURL]
        case .antigravity, .trae:
            let found = defaultURLs.filter(isReadable)
            return found.isEmpty ? defaultURLs : found
        case .openclaw:
            let found = defaultURLs.filter(isReadable)
            return found.isEmpty ? [defaultURL] : found
        case .codex, .claude, .dsh, .opencode, .gemini, .workbuddy, .workbuddyAi, .zcode, .kimi, .grokBuild, .hermes, .droid:
            return defaultURLs
        }
    }

    /// An existing folder the app reads; an Antigravity `*-backup` store never is; OpenClaw's may be a database file.
    func isReadable(_ url: URL) -> Bool {
        locationExists(url) && (self != .antigravity || LiveMonitor.isReadableAntigravityStore(url))
    }

    /// The folder exists; for OpenClaw an agent database file counts too.
    func locationExists(_ url: URL) -> Bool {
        Self.exists(url) || (acceptsDatabaseFile && FolderChooser.isOpenClawDatabase(url))
    }

    /// The picker also accepts `openclaw-agent.sqlite` (DESIGN 5).
    var acceptsDatabaseFile: Bool { self == .openclaw }

    /// Stores a custom folder; nil clears the override. Choosing the (first) default location clears it too, except
    /// for omp, Antigravity and Pi, where one chosen folder is narrower than the automatic set. A WorkBuddy data folder and
    /// its `projects` folder are the same location, as are a `.factory` folder and its `sessions` folder.
    @MainActor
    func setURL(_ url: URL?, in settings: AppSettings) {
        let value = url.flatMap { replacesAutomaticSet || !isDefaultLocation($0) ? $0 : nil }
        switch self {
        case .codex: settings.codexRoot = value
        case .claude: settings.claudeRoot = value
        case .dsh: settings.dshRoot = value
        case .opencode: settings.opencodeRoot = value
        case .gemini: settings.geminiRoot = value
        case .omp: settings.ompRoot = value
        case .antigravity: settings.antigravityRoot = value
        case .workbuddy: settings.workbuddyRoot = value
        case .zcode: settings.zcodeRoot = value
        case .kimi: settings.kimiRoot = value
        case .grokBuild: settings.grokBuildRoot = value
        case .trae: settings.traeRoot = value
        case .workbuddyAi: settings.workbuddyAiRoot = value
        case .pi: settings.piRoot = value
        case .openclaw: settings.openclawRoot = value
        case .hermes: settings.hermesRoot = value
        case .droid: settings.droidRoot = value
        }
    }

    private var replacesAutomaticSet: Bool { self == .omp || self == .antigravity || self == .pi || self == .openclaw || self == .trae }

    private func isDefaultLocation(_ url: URL) -> Bool {
        switch self {
        case .workbuddy, .workbuddyAi:
            Self.isSameFolder(LiveMonitor.workbuddyProjectsFolder(url), LiveMonitor.workbuddyProjectsFolder(defaultURL))
        case .droid:
            Self.isSameFolder(LiveMonitor.droidSessionsFolder(url), defaultURL)
        case .codex, .claude, .dsh, .opencode, .gemini, .omp, .antigravity, .zcode, .kimi, .grokBuild, .pi, .openclaw, .hermes, .trae:
            Self.isSameFolder(url, defaultURL)
        }
    }

    static func isSameFolder(_ lhs: URL, _ rhs: URL) -> Bool {
        lhs.standardizedFileURL.path == rhs.standardizedFileURL.path
    }

    static func exists(_ url: URL) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: url.path, isDirectory: &isDirectory) && isDirectory.boolValue
    }

    /// "~/.codex/sessions" for display; the full path stays available as help text.
    static func displayPath(_ url: URL) -> String {
        (url.standardizedFileURL.path as NSString).abbreviatingWithTildeInPath
    }
}

/// Lets the user pick a transcript folder (or, where a source accepts one, its database file) in a sheet and validates
/// the choice. Never writes anything.
@MainActor
enum FolderChooser {
    /// OpenClaw's agent database (METRICS 4.14), the one file a picker may accept.
    nonisolated static let openclawDatabaseName = "openclaw-agent.sqlite"

    static func choose(
        message: String,
        prompt: String,
        startingAt current: URL,
        window: NSWindow?,
        acceptsDatabaseFile: Bool = false,
        completion: @escaping @MainActor (URL) -> Void
    ) {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = acceptsDatabaseFile
        panel.allowsMultipleSelection = false
        panel.canCreateDirectories = false
        panel.treatsFilePackagesAsDirectories = false
        panel.resolvesAliases = true
        panel.showsHiddenFiles = true
        panel.message = message
        panel.prompt = prompt
        panel.directoryURL = nearestExistingFolder(current)
        // The panel holds its delegate weakly; the handlers below keep the filter alive while it is open.
        let filter = acceptsDatabaseFile ? DatabaseFileFilter() : nil
        panel.delegate = filter

        if let window {
            panel.beginSheetModal(for: window) { response in
                MainActor.assumeIsolated {
                    withExtendedLifetime(filter) { finish(panel, response, acceptsDatabaseFile, completion) }
                }
            }
        } else {
            panel.begin { response in
                MainActor.assumeIsolated {
                    withExtendedLifetime(filter) { finish(panel, response, acceptsDatabaseFile, completion) }
                }
            }
        }
    }

    private static func finish(
        _ panel: NSOpenPanel,
        _ response: NSApplication.ModalResponse,
        _ acceptsDatabaseFile: Bool,
        _ completion: @MainActor (URL) -> Void
    ) {
        guard response == .OK, let url = panel.url else { return }
        let database = acceptsDatabaseFile && isOpenClawDatabase(url) ? URL(fileURLWithPath: url.standardizedFileURL.path) : nil
        guard let chosen = database ?? validatedFolder(url) else {
            NSSound.beep()
            return
        }
        completion(chosen)
    }

    /// A local regular file named `openclaw-agent.sqlite` (METRICS 4.14).
    nonisolated static func isOpenClawDatabase(_ url: URL) -> Bool {
        guard url.isFileURL, url.lastPathComponent == openclawDatabaseName else { return false }
        let values = try? url.standardizedFileURL.resourceValues(forKeys: [.isRegularFileKey])
        return values?.isRegularFile == true
    }

    /// A local, existing directory (not a package), as a standardized file URL.
    static func validatedFolder(_ url: URL) -> URL? {
        guard url.isFileURL else { return nil }
        let standardized = url.standardizedFileURL
        let values = try? standardized.resourceValues(forKeys: [.isDirectoryKey, .isPackageKey])
        guard values?.isDirectory == true, values?.isPackage != true else { return nil }
        return URL(fileURLWithPath: standardized.path, isDirectory: true)
    }

    /// Enables folders and `openclaw-agent.sqlite` only, so no other file can be picked.
    private final class DatabaseFileFilter: NSObject, NSOpenSavePanelDelegate {
        func panel(_ sender: Any, shouldEnable url: URL) -> Bool {
            let values = try? url.resourceValues(forKeys: [.isDirectoryKey])
            return values?.isDirectory == true || url.lastPathComponent == FolderChooser.openclawDatabaseName
        }
    }

    private static func nearestExistingFolder(_ url: URL) -> URL {
        var candidate = url.standardizedFileURL
        while !TranscriptFolder.exists(candidate), candidate.pathComponents.count > 1 {
            candidate.deleteLastPathComponent()
        }
        return candidate
    }
}
