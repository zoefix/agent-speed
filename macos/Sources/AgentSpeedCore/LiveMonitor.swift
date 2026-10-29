import Foundation

/// Reads local agent transcripts on a background queue and publishes snapshots to a `SpeedStore`.
@MainActor
public final class LiveMonitor {
    public private(set) var configuration: MonitorConfiguration
    /// Source sets whose measured peak each snapshot reports in `subsetPeaks` (METRICS 6.8).
    public private(set) var subsets: Set<SourceSet> = []
    /// Prices every response (METRICS 9); nil leaves costs unknown.
    public let pricing: PriceBook?
    private let store: SpeedStore
    private var core: MonitorCore?
    private var mailbox: SnapshotMailbox?
    /// The prediction coefficients from the history's calibration (METRICS 6.11), kept across restarts.
    public private(set) var taskModels: [SourceKind: TaskModels] = [:]
    /// Live reporting's watch epoch (METRICS 11.10), kept across restarts.
    public private(set) var reportWatch: Int?

    public init(store: SpeedStore, configuration: MonitorConfiguration = MonitorConfiguration(), pricing: PriceBook? = nil) {
        self.store = store
        self.configuration = configuration
        self.pricing = pricing
    }

    /// True between `start()` and `stop()`.
    public var isRunning: Bool { core != nil }

    public func start() {
        guard core == nil else { return }
        let mailbox = SnapshotMailbox { [weak store] snapshot, events in
            store?.publishLive(snapshot, events: events)
        }
        let core = MonitorCore(configuration: configuration, mailbox: mailbox, subsets: subsets, pricing: pricing)
        self.mailbox = mailbox
        self.core = core
        core.start()
        if !taskModels.isEmpty { core.setTaskModels(taskModels) }
        if reportWatch != nil { core.setReportWatch(reportWatch) }
    }

    /// Live reporting's watch (METRICS 11.10 *Watch*): tasks whose first event is read from now on are marked with
    /// `epoch` and count when they complete; nil turns it off and unwatches every task.
    public func setReportWatch(_ epoch: Int?) {
        guard epoch != reportWatch else { return }
        reportWatch = epoch
        core?.setReportWatch(epoch)
    }

    /// New calibrations for the live predictions (METRICS 6.11).
    public func setTaskModels(_ models: [SourceKind: TaskModels]) {
        taskModels = models
        core?.setTaskModels(models)
    }

    public func stop() {
        mailbox?.close()
        core?.stop()
        mailbox = nil
        core = nil
    }

    /// Requests `SpeedSnapshot.subsetPeaks` for these sets (one per floating orb that tracks a subset). Kept across
    /// restarts and configuration changes; a running monitor publishes a fresh snapshot promptly when they change.
    public func setSubsets(_ sets: Set<SourceSet>) {
        subsets = sets
        core?.setSubsets(sets)
    }

    /// Applies new settings; transcripts are read again when the roots or the enabled sources change.
    public func update(configuration: MonitorConfiguration) {
        let needsRescan = configuration.requiresRescan(comparedTo: self.configuration)
        self.configuration = configuration
        guard needsRescan, isRunning else { return }
        stop()
        start()
    }

    public nonisolated static var defaultCodexRoot: URL {
        TranscriptLocations.defaultCodexRoot
    }

    public nonisolated static var defaultClaudeRoot: URL {
        TranscriptLocations.defaultClaudeRoot
    }

    public nonisolated static var defaultTraeRoots: [URL] { TranscriptLocations.defaultTraeRoots }

    public nonisolated static var defaultZcodeRoot: URL { TranscriptLocations.defaultZcodeRoot }
    public nonisolated static var defaultKimiRoot: URL { TranscriptLocations.defaultKimiRoot }
    public nonisolated static var defaultGrokBuildRoot: URL { TranscriptLocations.defaultGrokBuildRoot }

    /// Pi's automatic roots: `<agent dir>/sessions`, then Pi's own custom session folder when one is set (a configured
    /// root replaces all of them).
    public nonisolated static var defaultPiRoots: [URL] { TranscriptLocations.defaultPiRoots }

    /// OpenClaw's automatic roots: `$OPENCLAW_STATE_DIR`, else `~/.openclaw` and every `~/.openclaw-<profile>` folder
    /// (a configured folder or database replaces all of them).
    public nonisolated static var defaultOpenclawRoots: [URL] { TranscriptLocations.defaultOpenclawRoots }

    /// The Hermes Agent home: `$HERMES_HOME` (the home of the profile it names), else `~/.hermes` (a configured home or
    /// log folder replaces it).
    public nonisolated static var defaultHermesHome: URL { TranscriptLocations.defaultHermesHome }

    /// Factory Droid's sessions folder, `~/.factory/sessions` (a configured folder replaces it).
    public nonisolated static var defaultDroidRoot: URL { TranscriptLocations.defaultDroidRoot }

    /// The sessions folder a configured Factory Droid folder names: its `sessions` folder when it has one (a `.factory`
    /// folder), else the folder itself (METRICS 4.16).
    public nonisolated static func droidSessionsFolder(_ url: URL) -> URL { DroidPaths.sessionsFolder(url) }

    /// The agent databases a configured OpenClaw folder or file names (empty when it names none).
    public nonisolated static func openclawDatabases(in url: URL) -> [String] {
        OpenClawHome.databases(in: TranscriptDiscovery.normalizedRoot(url.path))
    }

    public nonisolated static var defaultDshRoot: URL {
        TranscriptLocations.defaultDshRoot
    }

    /// Gemini CLI's default `tmp` folders (a configured root replaces both).
    public nonisolated static var defaultGeminiRoots: [URL] {
        TranscriptLocations.defaultGeminiRoots
    }

    /// The folder that contains OpenCode's `opencode.db`.
    public nonisolated static var defaultOpencodeRoot: URL {
        TranscriptLocations.defaultOpencodeRoot
    }

    /// omp's automatic `sessions` roots: the default root first, then one per named profile (a configured root
    /// replaces all of them).
    public nonisolated static var defaultOmpRoots: [URL] {
        TranscriptLocations.defaultOmpRoots
    }

    /// omp's default `sessions` root (the first of `defaultOmpRoots`).
    public nonisolated static var defaultOmpRoot: URL {
        TranscriptLocations.defaultOmpRoots[0]
    }

    /// Antigravity's store folders, each containing `conversations` (a configured store replaces all three).
    public nonisolated static var defaultAntigravityStores: [URL] {
        TranscriptLocations.defaultAntigravityStores
    }

    /// WorkBuddy's data folder (`~/.workbuddy`, China edition); its `projects` folder holds the sessions.
    public nonisolated static var defaultWorkBuddyDataFolder: URL {
        TranscriptLocations.defaultWorkBuddyDataFolder
    }

    /// WorkBuddy AI's data folder (`~/.workbuddy-ai`, international edition).
    public nonisolated static var defaultWorkBuddyAiDataFolder: URL {
        TranscriptLocations.defaultWorkBuddyAiDataFolder
    }

    /// The `projects` folder a WorkBuddy folder setting reads: the folder itself when it is called `projects`.
    public nonisolated static func workbuddyProjectsFolder(_ folder: URL) -> URL {
        TranscriptLocations.workbuddyProjects(folder)
    }

    /// False for a `*-backup` store (or its `conversations` folder), which is never read.
    public nonisolated static func isReadableAntigravityStore(_ folder: URL) -> Bool {
        TranscriptLocations.isReadableAntigravityRoot(TranscriptLocations.antigravityConversations(folder).path)
    }
}
