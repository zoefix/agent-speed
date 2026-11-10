import Foundation

/// Reads local agent transcripts on a background queue and publishes snapshots to a `SpeedStore`.
@MainActor
public final class LiveMonitor {
    public private(set) var configuration: MonitorConfiguration
    /// Subsets whose measured peak each snapshot reports in `subsetPeaks` (METRICS 6.8).
    public private(set) var subsets: Set<SubsetKey> = []
    /// Prices every response (METRICS 9); nil leaves costs unknown.
    public let pricing: PriceBook?
    private let store: SpeedStore
    private var core: MonitorCore?
    private var mailbox: SnapshotMailbox?
    /// The stored performance history (METRICS 6.11), kept across restarts: the score card's baselines.
    public private(set) var perfHistory: [PerfDayRecord] = []
    /// Live reporting's watch epoch (METRICS 11.10), kept across restarts.
    public private(set) var reportWatch: Int?
    /// The remote hosts' received state, kept across restarts (REMOTE 7).
    let remoteFeed = RemoteFeed()
    /// Follows the enabled sources and the report watch.
    weak var remote: RemoteCoordinator?

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
        let core = MonitorCore(configuration: configuration, mailbox: mailbox, subsets: subsets, pricing: pricing,
                               remoteFeed: remoteFeed)
        self.mailbox = mailbox
        self.core = core
        core.start()
        if !perfHistory.isEmpty { core.setPerfHistory(perfHistory) }
        if reportWatch != nil { core.setReportWatch(reportWatch) }
    }

    /// Live reporting's watch (METRICS 11.10 *Watch*): completions read from now on count in `epoch`; nil turns it
    /// off.
    public func setReportWatch(_ epoch: Int?) {
        guard epoch != reportWatch else { return }
        let wasOn = reportWatch != nil
        reportWatch = epoch
        core?.setReportWatch(epoch)
        if wasOn != (epoch != nil) { remote?.monitorSettingsChanged() }
    }

    /// The history store's performance records, as it last saved them (METRICS 6.11 *Baseline*).
    public func setPerfHistory(_ records: [PerfDayRecord]) {
        perfHistory = records
        core?.setPerfHistory(records)
    }

    public func stop() {
        mailbox?.close()
        core?.stop()
        mailbox = nil
        core = nil
    }

    /// Requests `SpeedSnapshot.subsetPeaks` for these subsets (one per floating orb or board that tracks a subset).
    /// Kept across restarts and configuration changes; a running monitor publishes a fresh snapshot promptly when they
    /// change.
    public func setSubsets(_ sets: Set<SubsetKey>) {
        subsets = sets
        core?.setSubsets(sets)
    }

    /// Applies new settings; transcripts are read again when the roots or the enabled sources change.
    public func update(configuration: MonitorConfiguration) {
        let needsRescan = configuration.requiresRescan(comparedTo: self.configuration)
        let sourcesChanged = configuration.enabledSources != self.configuration.enabledSources
        self.configuration = configuration
        if sourcesChanged { remote?.monitorSettingsChanged() }
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

    public nonisolated static var defaultZcodeRoot: URL { TranscriptLocations.defaultZcodeRoot }
    public nonisolated static var defaultKimiRoot: URL { TranscriptLocations.defaultKimiRoot }
    public nonisolated static var defaultGrokBuildRoot: URL { TranscriptLocations.defaultGrokBuildRoot }

    /// Pi's automatic roots: `<agent dir>/sessions`, then Pi's own custom session folder when one is set (the automatic
    /// entry of Pi's location list).
    public nonisolated static var defaultPiRoots: [URL] { TranscriptLocations.defaultPiRoots }

    /// OpenClaw's automatic roots: `$OPENCLAW_STATE_DIR`, else `~/.openclaw` and every `~/.openclaw-<profile>` folder
    /// (the automatic entry of OpenClaw's location list).
    public nonisolated static var defaultOpenclawRoots: [URL] { TranscriptLocations.defaultOpenclawRoots }

    /// The Hermes Agent home: `$HERMES_HOME` (the home of the profile it names), else `~/.hermes` (the automatic entry
    /// of its location list).
    public nonisolated static var defaultHermesHome: URL { TranscriptLocations.defaultHermesHome }

    /// Factory Droid's sessions folder, `~/.factory/sessions` (the automatic entry of its location list).
    public nonisolated static var defaultDroidRoot: URL { TranscriptLocations.defaultDroidRoot }

    /// The Devin CLI's folder that holds `sessions.db`: `$CHISEL_SESSION_DB`'s folder, else the XDG data folder's
    /// `devin/cli` (the automatic entry of its location list).
    public nonisolated static var defaultDevinRoot: URL {
        URL(fileURLWithPath: TranscriptLocations.defaultDevinRoots[0].displayFolder, isDirectory: true)
    }

    /// Whether a folder holds a Devin CLI database now: `sessions.db` in it or in its `cli` folder, or the automatic
    /// entry's database of any name in it (`$CHISEL_SESSION_DB`, METRICS 4.17).
    public nonisolated static func devinLocationExists(_ url: URL) -> Bool {
        let folder = TranscriptDiscovery.normalizedRoot(url.path)
        if DevinPaths.database(ofLocation: folder) != nil { return true }
        return TranscriptLocations.defaultDevinRoots.contains { $0.displayFolder == folder && $0.database != nil }
    }

    /// The folders that hold Qoder IDE's databases: `SharedClientCache/cache/db` of the international and the China
    /// edition (the automatic entry of its location list).
    public nonisolated static var defaultQoderRoots: [URL] {
        TranscriptLocations.defaultQoderRoots.map { URL(fileURLWithPath: $0.displayFolder, isDirectory: true) }
    }

    /// Whether a folder names a Qoder IDE database now: `local.db` in it, in its `cache/db` or in its
    /// `SharedClientCache/cache/db` folder (METRICS 4.18).
    public nonisolated static func qoderLocationExists(_ url: URL) -> Bool {
        QoderPaths.database(ofLocation: TranscriptDiscovery.normalizedRoot(url.path)) != nil
    }

    /// Qoder CLI's `projects` folders: `$QODER_CONFIG_DIR` or `~/.qoder`, then `$QODERCN_CONFIG_DIR` or `~/.qoder-cn`
    /// (the automatic entry of its location list).
    public nonisolated static var defaultQoderCliRoots: [URL] { TranscriptLocations.defaultQoderCliRoots }

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

    /// Gemini CLI's default `tmp` folders (both are the automatic entry of its location list).
    public nonisolated static var defaultGeminiRoots: [URL] {
        TranscriptLocations.defaultGeminiRoots
    }

    /// The folder that contains OpenCode's `opencode.db`.
    public nonisolated static var defaultOpencodeRoot: URL {
        TranscriptLocations.defaultOpencodeRoot
    }

    /// omp's automatic `sessions` roots: the default root first, then one per named profile (the automatic entry of
    /// its location list).
    public nonisolated static var defaultOmpRoots: [URL] {
        TranscriptLocations.defaultOmpRoots
    }

    /// omp's default `sessions` root (the first of `defaultOmpRoots`).
    public nonisolated static var defaultOmpRoot: URL {
        TranscriptLocations.defaultOmpRoots[0]
    }

    /// Antigravity's store folders, each containing `conversations` (the automatic entry of its location list).
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
