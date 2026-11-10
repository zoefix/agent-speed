import AgentSpeedCore
import AppKit

/// One line of a source's location list in Settings → 資料來源 (DESIGN 5).
struct LocationEntry: Identifiable, Equatable {
    let location: DataLocation
    /// What the line names: the automatic entry can name several folders (Gemini CLI's two, Antigravity's stores found).
    let shownURLs: [URL]
    /// What the entry reads, for its state and Show in Finder.
    let urls: [URL]
    /// The entry reads something that exists (a missing one is shown dimmed with `source.folderMissing`).
    let exists: Bool
    /// Show in Finder: the first readable location, else the first existing one; nil when none exists.
    let revealURL: URL?

    var id: DataLocation { location }
    var isAutomatic: Bool { location == .automatic }
}

/// The Settings side of a location list (DESIGN 5): default locations, picker texts and footnotes, and the entries as
/// shown. Claude Code and Claude Desktop share the Claude list; OpenCode's location is the folder that contains
/// `opencode.db`; OpenClaw's may also be an agent folder or one database.
extension TranscriptFolder {
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
        case .workbuddyAi: [LiveMonitor.defaultWorkBuddyAiDataFolder]
        case .pi: LiveMonitor.defaultPiRoots
        case .openclaw: LiveMonitor.defaultOpenclawRoots
        case .hermes: [LiveMonitor.defaultHermesHome]
        case .droid: [LiveMonitor.defaultDroidRoot]
        case .devin: [LiveMonitor.defaultDevinRoot]
        case .qoder: LiveMonitor.defaultQoderRoots
        case .qoderCli: LiveMonitor.defaultQoderCliRoots
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
        case .workbuddyAi: "mac.settings.sources.chooseWorkbuddyAi"
        case .pi: "mac.settings.sources.choosePi"
        case .openclaw: "mac.settings.sources.chooseOpenClaw"
        case .hermes: "mac.settings.sources.chooseHermes"
        case .droid: "mac.settings.sources.chooseDroid"
        case .devin: "mac.settings.sources.chooseDevin"
        case .qoder: "mac.settings.sources.chooseQoder"
        case .qoderCli: "mac.settings.sources.chooseQoderCli"
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
        case .workbuddyAi: "settings.sources.workbuddyAiHint"
        case .pi: "settings.sources.piHint"
        case .openclaw: "settings.sources.openclawHint"
        case .hermes: "settings.sources.hermesHint"
        case .droid: "settings.sources.droidHint"
        case .devin: "settings.sources.devinHint"
        case .qoder: "settings.sources.qoderHint"
        case .qoderCli: "settings.sources.qoderCliHint"
        }
    }

    // MARK: - Entries

    @MainActor
    func locations(in settings: AppSettings) -> [DataLocation] { settings.sourceLocations[self] }

    /// The list as Settings shows it, in list order.
    @MainActor
    func entries(in settings: AppSettings) -> [LocationEntry] {
        locations(in: settings).map(entry)
    }

    func entry(_ location: DataLocation) -> LocationEntry {
        let urls: [URL]
        let shown: [URL]
        switch location {
        case .automatic:
            urls = defaultURLs
            shown = automaticShownURLs
        case .path(let path):
            urls = [URL(fileURLWithPath: path)]
            shown = urls
        }
        let reveal = urls.first(where: isReadable) ?? urls.first(where: locationExists)
        return LocationEntry(location: location, shownURLs: shown, urls: urls, exists: urls.contains(where: isReadable),
                             revealURL: reveal)
    }

    /// The folders the automatic entry names (DESIGN 5): omp and Pi only their default root although profiles and Pi's
    /// custom session folder are read too; Antigravity the stores found, or all three when none is; OpenClaw the state
    /// folders found, or `~/.openclaw` when none is; every other source all of its defaults.
    var automaticShownURLs: [URL] {
        switch self {
        case .omp:
            return [LiveMonitor.defaultOmpRoot]
        case .pi:
            return [defaultURL]
        case .antigravity:
            let found = defaultURLs.filter(isReadable)
            return found.isEmpty ? defaultURLs : found
        case .openclaw:
            let found = defaultURLs.filter(isReadable)
            return found.isEmpty ? [defaultURL] : found
        case .codex, .claude, .dsh, .opencode, .gemini, .workbuddy, .workbuddyAi, .zcode, .kimi, .grokBuild, .hermes, .droid,
             .devin, .qoder, .qoderCli:
            return defaultURLs
        }
    }

    /// An existing folder the app reads; an Antigravity `*-backup` store never is; OpenClaw's may be a database file.
    func isReadable(_ url: URL) -> Bool {
        locationExists(url) && (self != .antigravity || LiveMonitor.isReadableAntigravityStore(url))
    }

    /// The folder exists; for OpenClaw an agent database file counts too; for Devin and Qoder IDE the folder must name
    /// the database (METRICS 4.17, 4.18).
    func locationExists(_ url: URL) -> Bool {
        if self == .devin { return LiveMonitor.devinLocationExists(url) }
        if self == .qoder { return LiveMonitor.qoderLocationExists(url) }
        return Self.exists(url) || (acceptsDatabaseFile && FolderChooser.isOpenClawDatabase(url))
    }

    // MARK: - Editing

    /// Adds a picked location (METRICS 2.1): refused when it is in the list already or the automatic entry reads it,
    /// and at the limit; the default location brings the automatic entry back where one chosen folder is not narrower.
    @MainActor
    @discardableResult
    func add(_ url: URL, in settings: AppSettings) -> SourceLocations.AddOutcome {
        var locations = settings.sourceLocations
        let outcome = locations.add(url.standardizedFileURL.path, to: self)
        if outcome.changed { settings.sourceLocations = locations }
        return outcome
    }

    @MainActor
    func remove(_ location: DataLocation, in settings: AppSettings) {
        settings.sourceLocations.remove(location, from: self)
    }

    @MainActor
    func reset(in settings: AppSettings) {
        settings.sourceLocations.reset(self)
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
