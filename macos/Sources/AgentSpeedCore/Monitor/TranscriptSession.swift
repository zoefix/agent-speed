import Foundation
import os

/// Statistics of the startup backlog read.
public struct SeedSummary: Equatable, Sendable {
    public var filesDiscovered = 0
    public var filesRead = 0
    public var bytesRead: Int64 = 0
    public var durationMs: Double = 0
    public var partialHistory = false

    public init() {}
}

/// A live whole-file read (compressed Codex session or legacy Gemini document), prepared on the session queue and
/// performed elsewhere so live updates never pause (METRICS 5.3).
struct WholeFileRead: Sendable {
    let path: String
    let family: TranscriptFamily
    let fileID: FileID
    let reader: TranscriptReader
    /// The file's task reader (METRICS 6.9), carried through the read.
    var taskReader: TaskLineReader?

    func perform() -> WholeFileResult {
        var reader = self.reader
        var taskReader = self.taskReader
        let output = TranscriptDecoder.decode(family, reader: &reader, tasks: &taskReader)
        return WholeFileResult(path: path, fileID: fileID, reader: reader, lines: output.lines, read: output.read,
                               taskReader: taskReader, taskLines: output.tasks)
    }
}

struct WholeFileResult: Sendable {
    let path: String
    let fileID: FileID
    let reader: TranscriptReader
    let lines: DecodedLines
    let read: FileTailer.ReadResult
    var taskReader: TaskLineReader?
    var taskLines: [TaskLine] = []
}

/// Every transcript file of the enabled roots, the OpenCode database, their parsers, the shared dedupe set and the
/// engine.
///
/// Not thread-safe: one owner (a serial queue) drives it. Only seeding fans out, and it joins before returning.
final class TranscriptSession {
    /// Size rules of METRICS 4 and 5; tests shrink them.
    struct ReadingLimits: Sendable {
        var largeFileBytes = FileTailer.largeFileBytes
        var tailWindowBytes = FileTailer.tailWindowBytes
        var maxLineBytes = FileTailer.defaultMaxLineBytes

        static let standard = ReadingLimits()
    }

    struct Root: Sendable {
        var family: TranscriptFamily
        var path: String
        var canonicalPath: String
        var enabled: Bool
        var available: Bool
        /// False for roots that are never read (an Antigravity `*-backup` store).
        var readable = true
        /// A folder whose existence alone detects the source (WorkBuddy: the data folder that holds `projects`).
        var detectionPath: String?
        /// The root is available, or its detection folder exists (METRICS 2).
        var detected = false
    }

    static let startupLookbackMs: Int64 = 15 * 60_000
    static let hotWindowMs: Int64 = 120_000
    /// Rediscovery re-checks every file whose last observed change is this recent, and the older ones in turns, each
    /// once every `recheckTurns` rounds: FSEvents stays silent while a writer keeps its file open (METRICS 5).
    static let recheckWindowMs: Int64 = 24 * 3_600_000
    static let recheckTurns = 6
    static let maxDecodeWorkers = 8
    /// A changed Antigravity database is read at most this often; its WAL is written continuously (METRICS 5.4).
    static let databaseReadIntervalNs: UInt64 = 500_000_000
    /// Database connections unused this long are closed, and at most `maxOpenDatabases` stay open (METRICS 5.4).
    static let databaseIdleNs: UInt64 = 60_000_000_000
    static let maxOpenDatabases = 16
    /// Deduplication owner of every OpenCode row; transcript files count up from 0.
    static let opencodeFileID: FileID = -1
    static let zcodeFileID: FileID = -2
    static let traeFileID: FileID = -3

    let enabledSources: Set<SourceKind>
    let limits: ReadingLimits
    private let configuration: MonitorConfiguration
    private(set) var roots: [Root]
    private var files: [String: TrackedFile] = [:]
    /// Incremental directory walks, by root path.
    private var indexes: [String: DirectoryIndex] = [:]
    private var deduper = Deduper()
    private var engine = SpeedEngine()
    private var parentMembers: [String: AgentParents.Member] = [:]
    private var parentLinks: [String: String] = [:]
    private var parentLinksAreStale = false
    private var nextFileID: FileID = 0
    private var recheckTurn = 0
    private var pendingEvents: [SpeedEvent] = []
    private var unreadablePaths: Set<String> = []
    private var removedMalformedLines = 0
    private(set) var partialHistory = false
    /// The OpenCode database, when that source is enabled (METRICS 4.5, 5.2).
    let opencode: OpencodeSource?
    let zcode: ZcodeSource?
    /// OpenClaw's agent databases, when that source is enabled (METRICS 4.14).
    let openclaw: OpenClawSource?
    private var openclawLinks: [String: String] = [:]
    /// Hermes Agent's log folders, when that source is enabled (METRICS 4.15).
    let hermes: HermesSource?
    /// Today's start of the last seed or rediscovery, for databases found while running.
    private var todayStartMs: Int64 = 0
    let trae: TraeSource?
    private var traeNeedsSeed = false
    private let zcodeRoot: String
    private var zcodeNeedsSeed = false
    private var zcodeLinks: [String: String] = [:]
    private let opencodeRoot: String
    private var opencodeNeedsSeed = false
    private var opencodeLinks: [String: String] = [:]
    /// Each scored source's prediction coefficients (METRICS 6.11 *Calibration*), from the history analysis.
    private var taskModels: [SourceKind: TaskModels] = [:]
    private var taskModelsVersion = 0
    /// Live reporting's watch epoch (METRICS 11.10 *Watch*); nil while it is off.
    private var reportWatch: Int?
    /// The startup backlog is being read: its tasks are history.
    private var seeding = false
    /// Files whose task stream has watched tasks (the 30-minute rule checks only these).
    private var watchedFiles: Set<String> = []

    /// Wall-clock milliseconds, for the age rule of completion events (METRICS 6.4); tests pin it.
    private let clock: @Sendable () -> Int64
    /// When set, live whole-file reads are handed here instead of running on the session queue; the owner performs
    /// them and returns the result through `completeWholeFileRead`. Without it they run inline.
    var wholeFileReads: ((WholeFileRead) -> Void)?

    /// Diagnostics keep every agent's whole task stream instead of its current task (`TaskStreamMode.history`).
    let taskMode: TaskStreamMode

    init(
        configuration: MonitorConfiguration,
        limits: ReadingLimits = .standard,
        pricing: PriceBook? = nil,
        clock: @escaping @Sendable () -> Int64 = { Clock.milliseconds(Date()) },
        taskMode: TaskStreamMode = .live
    ) {
        self.taskMode = taskMode
        enabledSources = configuration.enabledSources
        self.configuration = configuration
        self.limits = limits
        self.clock = clock
        engine = SpeedEngine(pricing: pricing)
        roots = configuration.allRoots().map { family, path, readable in
            Root(family: family, path: path, canonicalPath: path, enabled: configuration.reads(family), available: false,
                 readable: readable,
                 detectionPath: family.isWorkBuddy ? TranscriptLocations.workbuddyDataFolder(ofRoot: path) : nil)
        }
        opencodeRoot = configuration.resolvedOpencodeRoot
        opencode = configuration.readsOpencode ? OpencodeSource(root: opencodeRoot, fileID: Self.opencodeFileID) : nil
        zcodeRoot = configuration.resolvedZcodeRoot
        zcode = configuration.readsZcode ? ZcodeSource(root: zcodeRoot, fileID: Self.zcodeFileID) : nil
        openclaw = configuration.readsOpenclaw
            ? OpenClawSource(roots: configuration.resolvedOpenclawRoots, maxLineBytes: limits.maxLineBytes) : nil
        let hermesRoot = configuration.resolvedHermesRoot
        hermes = configuration.readsHermes
            ? HermesSource(root: hermesRoot.path, isSetting: hermesRoot.isSetting,
                           timeZone: configuration.resolvedHermesTimeZone, limits: limits) : nil
        trae = configuration.readsTrae ? TraeSource(roots: configuration.resolvedTraeRoots, fileID: Self.traeFileID, clock: clock) : nil
        refreshAvailability()
        openclaw?.taskWatch = { [weak self] in self?.taskWatchContext() }
    }

    // MARK: - Roots

    var hasAvailableRoot: Bool {
        roots.contains { $0.enabled && $0.detected } || opencode?.isReady == true || zcode?.isReady == true
            || trae?.exists == true || openclaw?.detected == true || hermes?.detected == true
    }

    var watchedPaths: [String] {
        var paths = roots.filter { $0.enabled && $0.available }.map(\.canonicalPath)
        if let directory = opencode?.watchedDirectory, opencode?.isReady == true, !paths.contains(directory) {
            paths.append(directory)
        }
        if let directory = zcode?.watchedDirectory, !paths.contains(directory) { paths.append(directory) }
        for directory in openclaw?.watchedDirectories ?? [] where !paths.contains(directory) { paths.append(directory) }
        for directory in hermes?.watchedDirectories ?? [] where !paths.contains(directory) { paths.append(directory) }
        for directory in trae?.watchedDirectories ?? [] where !paths.contains(directory) { paths.append(directory) }
        return paths
    }

    /// The OpenClaw row's path (the first state folder found) and whether the source is detected (METRICS 2).
    var openclawLocation: (path: String, available: Bool) {
        (openclaw?.displayRoot ?? configuration.resolvedOpenclawRoots.first ?? "", openclaw?.detected ?? false)
    }
    var traeLocation: (path: String, available: Bool) { (trae?.rootPath ?? TranscriptLocations.defaultTraeRoots[0].path, trae?.exists ?? false) }

    var zcodeLocation: (path: String, available: Bool) { (zcodeRoot, zcode?.exists ?? false) }

    /// The Hermes Agent row's path (the home, or the folder of the setting) and whether it exists (METRICS 2).
    var hermesLocation: (path: String, available: Bool) {
        (hermes?.root ?? configuration.resolvedHermesRoot.path, hermes?.detected ?? false)
    }

    /// Root path and availability of the OpenCode source (the folder that contains `opencode.db`).
    var opencodeLocation: (path: String, available: Bool, status: OpencodeSource.Status?) {
        (opencodeRoot, opencode?.exists ?? false, opencode?.status)
    }

    /// Re-checks which roots exist. Returns true when availability changed.
    @discardableResult
    func refreshAvailability() -> Bool {
        var changed = false
        if let opencode, opencode.refreshAvailability() {
            changed = true
            opencodeNeedsSeed = true
        }
        if let zcode, zcode.refreshAvailability() {
            changed = true
            zcodeNeedsSeed = true
        }
        if let trae, trae.refreshAvailability() {
            changed = true
            traeNeedsSeed = true
        }
        for index in roots.indices {
            let available = roots[index].readable && TranscriptDiscovery.isAvailable(root: roots[index].path)
            let detected = available || roots[index].detectionPath.map(TranscriptDiscovery.isAvailable(root:)) == true
            if available != roots[index].available || detected != roots[index].detected { changed = true }
            roots[index].available = available
            roots[index].detected = detected
            if available, let resolved = realpath(roots[index].path, nil) {
                roots[index].canonicalPath = String(cString: resolved)
                free(resolved)
            }
        }
        return changed
    }

    /// Pi's automatic roots follow Pi's settings, whose custom session folder may change while the app runs (METRICS
    /// 4.13): true when they no longer match the roots this session reads. The settings file is read, never written.
    var piRootsChanged: Bool {
        configuration.resolvedRoots(for: .pi) != roots.filter { $0.family == .pi }.map(\.path)
    }

    /// OpenClaw's state folders and the Codex homes next to its agent databases change while the app runs (a new
    /// profile, a new agent, METRICS 2): true when they no longer match what this session reads.
    var openclawRootsChanged: Bool {
        if let openclaw, openclaw.roots != configuration.resolvedOpenclawRoots { return true }
        return configuration.resolvedRoots(for: .codex) != roots.filter { $0.family == .codex }.map(\.path)
    }

    /// The family's first detected root, else its first root (Gemini CLI, omp, Antigravity and Pi can have several).
    func root(for family: TranscriptFamily) -> Root {
        roots.first { $0.family == family && $0.detected } ?? roots.first { $0.family == family }!
    }

    // MARK: - Seeding

    /// Reads the startup backlog (METRICS 5). Responses are seeded: they never produce live events.
    func seed(
        todayStartMs: Int64,
        progress: @Sendable (Double) -> Void = { _ in },
        isCancelled: @Sendable () -> Bool = { false }
    ) -> SeedSummary {
        let started = DispatchTime.now().uptimeNanoseconds
        var summary = SeedSummary()
        self.todayStartMs = todayStartMs
        seeding = true
        defer { seeding = false }
        refreshAvailability()

        var backlog: [(file: DiscoveredFile, tracked: TrackedFile, start: FileTailer.Start)] = []
        var droidBaselines: [TrackedFile] = []
        let threshold = todayStartMs - Self.startupLookbackMs
        for root in roots where root.enabled && root.available {
            let discovered = TranscriptDiscovery.files(at: scan(root), family: root.family)
            for file in TranscriptDiscovery.mostRecent(discovered) where files[file.path] == nil {
                summary.filesDiscovered += 1
                var readsBacklog = file.stat.modifiedMs >= threshold
                let start: FileTailer.Start
                if !readsBacklog {
                    start = .end(file.stat.size)
                } else if root.family.isDatabase {
                    // A conversation database is read completely; the size rules of section 5 do not apply.
                    start = .beginning
                } else if TranscriptReader.isDocument(file.path), file.stat.size > DocumentReader.defaultMaxBytes {
                    // Whole-document sessions this large are not read at all (METRICS 4.6).
                    partialHistory = true
                    start = .end(file.stat.size)
                    readsBacklog = false
                } else if file.stat.size > limits.largeFileBytes, !TranscriptReader.isCompressedStream(file.path) {
                    // Compressed sessions are always read whole (METRICS 5.3).
                    partialHistory = true
                    if TranscriptReader.isFrameStream(file.path) {
                        // A frame stream cannot be entered in the middle, so it is not seeded at all (METRICS 5.1).
                        start = .end(file.stat.size)
                        readsBacklog = false
                    } else {
                        start = .tail(limits.tailWindowBytes)
                    }
                } else {
                    start = .beginning
                }
                let tracked = track(file, family: root.family, root: root.path, start: start)
                if readsBacklog {
                    backlog.append((file, tracked, start))
                } else if root.family == .droid {
                    droidBaselines.append(tracked)
                }
            }
        }
        takeDroidBaselines(droidBaselines)
        backlog.sort { TranscriptDiscovery.startupOrder($0.file, $1.file) }
        let ordered = backlog.map(\.tracked)

        // Decoded on worker threads a bounded window ahead and consumed in startup order, so only that window of
        // decoded lines (and task lines) is held at once, however large the backlog.
        let jobs = backlog.map { item in
            OrderedDecoder.Job(path: item.tracked.path, family: item.tracked.family,
                               size: Self.backlogBytes(item.start, size: item.file.stat.size),
                               reader: item.tracked.reader, taskReader: item.tracked.parser.taskReader)
        }
        let totalBytes = max(1, jobs.reduce(Int64(0)) { $0 + $1.size })
        let decoder = OrderedDecoder(jobs: jobs, maxBytesAhead: Self.backlogBytesAhead)
        if !jobs.isEmpty {
            // At the caller's priority, as a concurrent loop on its queue would run.
            decoder.start(workers: min(jobs.count, max(1, min(ProcessInfo.processInfo.activeProcessorCount,
                                                                Self.maxDecodeWorkers))),
                          qos: Thread.current.qualityOfService)
        }
        var consumedBytes: Int64 = 0
        for (file, job) in zip(ordered, jobs) {
            if isCancelled() {
                decoder.cancel()
                break
            }
            guard let result = decoder.next() else { break }
            consumedBytes += job.size
            progress(min(1, Double(consumedBytes) / Double(totalBytes)))
            file.reader = result.reader
            if file.family.isDatabase { file.lastReadNs = DispatchTime.now().uptimeNanoseconds }
            summary.filesRead += 1
            summary.bytesRead += Int64(result.read.bytesRead)
            if result.read.didReset { file.resetParser() }
            file.parser.taskReader = result.taskReader
            var upserts: [ResponseUpsert] = []
            file.parser.consume(result.lines, taskLines: result.tasks, deduper: &deduper) { upserts.append($0) }
            if !result.read.failed { _ = file.parser.finishReadCycle { upserts.append($0) } }
            let project = projectName(of: file)
            for upsert in upserts { deliver(upsert, project: project, live: false) }
            finishRead(of: file, result: result.read)
        }
        decoder.cancel()
        decoder.waitForWorkers()
        let seededAt = DispatchTime.now().uptimeNanoseconds
        closeIdleDatabases(nowNs: seededAt)
        if let opencode, opencode.exists {
            summary.filesDiscovered += 1
            if opencode.isReady {
                summary.filesRead += 1
                summary.bytesRead += opencode.databaseSize
            }
            seedOpencode(todayStartMs: todayStartMs)
        }
        if let zcode, zcode.exists {
            summary.filesDiscovered += 1
            if zcode.isReady {
                summary.filesRead += 1
                summary.bytesRead += zcode.databaseSize
            }
            zcodeNeedsSeed = false
            apply(zcode.seed(todayStartMs: todayStartMs, deduper: &deduper), live: false)
        }
        if let trae, trae.exists {
            summary.filesDiscovered += trae.existingPaths.count
            traeNeedsSeed = false
            apply(trae.seed(todayStartMs: todayStartMs, deduper: &deduper), live: false)
            if trae.isReady {
                summary.filesRead += trae.existingPaths.count
                summary.bytesRead += trae.databaseSize
            }
        }
        if let openclaw {
            apply(openclaw.seed(todayStartMs: todayStartMs, deduper: &deduper), live: false)
            summary.filesDiscovered += openclaw.databaseCount
            summary.filesRead += openclaw.readyDatabaseCount
            summary.bytesRead += openclaw.databaseBytes
        }
        if let hermes {
            apply(hermes.seed(todayStartMs: todayStartMs, deduper: &deduper), live: false)
            summary.filesDiscovered += hermes.filesDiscovered
            summary.filesRead += hermes.filesRead
            summary.bytesRead += hermes.bytesRead
        }
        summary.partialHistory = partialHistory
        summary.durationMs = Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000
        return summary
    }

    /// Factory Droid sessions older than the startup window start at their end and take their sidecar's counters as
    /// the baseline, so only later growth counts (METRICS 4.16). Sidecars are read concurrently.
    private func takeDroidBaselines(_ files: [TrackedFile]) {
        guard !files.isEmpty else { return }
        let paths = files.map { DroidPaths.sidecarPath(ofTranscript: $0.path) }
        let snapshots = OSAllocatedUnfairLock(initialState: [DroidSidecar.Snapshot?](repeating: nil, count: paths.count))
        let workers = min(paths.count, max(1, min(ProcessInfo.processInfo.activeProcessorCount, Self.maxDecodeWorkers)))
        DispatchQueue.concurrentPerform(iterations: workers) { worker in
            for index in stride(from: worker, to: paths.count, by: workers) {
                let snapshot = DroidSidecar.snapshot(paths[index])
                snapshots.withLock { $0[index] = snapshot }
            }
        }
        for (file, snapshot) in zip(files, snapshots.withLock { $0 }) {
            if let snapshot { file.parser.takeBaseline(snapshot) }
        }
    }

    /// The input the startup backlog may decode ahead of its consumer (a smaller window starves the consumer behind
    /// large files).
    static let backlogBytesAhead: Int64 = 256 << 20

    /// The bytes a backlog read covers: a tail window, or the whole file.
    private static func backlogBytes(_ start: FileTailer.Start, size: Int64) -> Int64 {
        switch start {
        case .tail(let window): min(window, size)
        case .end: 0
        case .beginning: size
        }
    }

    // MARK: - Live reading

    /// Reads files reported by the watcher. Unknown transcript paths under a root become new agents.
    @discardableResult
    func handleChanges(atCanonicalPaths paths: Set<String>) -> Bool {
        var changed = false
        if let opencode, paths.contains(where: opencode.matches(canonicalPath:)) {
            changed = apply(opencode.refresh(deduper: &deduper), live: true)
        }
        if let zcode, paths.contains(where: zcode.matches(canonicalPath:)) {
            changed = apply(zcode.refresh(deduper: &deduper), live: true) || changed
        }
        if let openclaw {
            changed = apply(openclaw.handleChanges(atCanonicalPaths: paths, todayStartMs: todayStartMs, deduper: &deduper), live: true)
                || changed
        }
        if let hermes { changed = apply(hermes.handleChanges(atCanonicalPaths: paths, deduper: &deduper), live: true) || changed }
        if let trae, paths.contains(where: trae.matches(canonicalPath:)) {
            changed = apply(trae.refresh(deduper: &deduper), live: true) || changed
        }
        var handled: Set<String> = []
        for path in paths.sorted() {
            guard let (localPath, root) = localPath(forCanonical: path), handled.insert(localPath).inserted else { continue }
            if let file = files[localPath] {
                changed = read(file, live: true) || changed
            } else if let stat = root.family.stat(localPath) {
                let file = track(DiscoveredFile(path: localPath, stat: stat), family: root.family, root: root.path, start: .beginning)
                changed = read(file, live: true) || changed
            }
        }
        return changed
    }

    /// Reads the databases whose changes arrived while their reads were throttled (METRICS 5.4).
    @discardableResult
    func readDirtyDatabases() -> Bool {
        let now = DispatchTime.now().uptimeNanoseconds
        var changed = false
        for file in files.values.filter(\.isDirty).sorted(by: { $0.path < $1.path })
        where now &- file.lastReadNs >= Self.databaseReadIntervalNs {
            changed = read(file, live: true) || changed
        }
        return changed
    }

    /// Closes database connections idle for a minute, then the least recently changed beyond the limit.
    private func closeIdleDatabases(nowNs: UInt64) {
        var open = files.values.filter { $0.family.isDatabase && !$0.wholeReadInFlight && $0.reader.hasOpenConnection }
        guard !open.isEmpty else { return }
        for file in open where nowNs &- file.lastReadNs >= Self.databaseIdleNs { file.reader.closeConnection() }
        open = open.filter { $0.reader.hasOpenConnection }
        guard open.count > Self.maxOpenDatabases else { return }
        open.sort { $0.lastStat.modifiedNs != $1.lastStat.modifiedNs ? $0.lastStat.modifiedNs < $1.lastStat.modifiedNs : $0.path < $1.path }
        for file in open.prefix(open.count - Self.maxOpenDatabases) { file.reader.closeConnection() }
    }

    /// The 1-second fallback: stat files modified in the last two minutes.
    @discardableResult
    func pollRecentFiles(nowMs: Int64) -> Bool {
        var changed = false
        if let update = opencode?.refreshIfChanged(deduper: &deduper) {
            changed = apply(update, live: true)
        }
        if let update = zcode?.refreshIfChanged(deduper: &deduper) { changed = apply(update, live: true) || changed }
        if let openclaw { changed = apply(openclaw.refreshIfChanged(deduper: &deduper), live: true) || changed }
        if let hermes { changed = apply(hermes.poll(nowMs: nowMs, deduper: &deduper), live: true) || changed }
        if let update = trae?.refreshIfChanged(deduper: &deduper) { changed = apply(update, live: true) || changed }
        let hot = files.values.filter { nowMs - $0.lastStat.modifiedMs <= Self.hotWindowMs }
        for file in hot.sorted(by: { $0.path < $1.path }) {
            guard let stat = file.family.stat(file.path) else {
                untrack(file.path)
                changed = true
                continue
            }
            if file.hasChanged(comparedTo: stat) { changed = read(file, live: true) || changed }
        }
        changed = readDirtyDatabases() || changed
        closeIdleDatabases(nowNs: DispatchTime.now().uptimeNanoseconds)
        return changed
    }

    /// Full rediscovery: new files are read from offset 0, vanished files are dropped, and tracked files are re-checked
    /// in case the watcher missed a change: recently changed ones every round, the others in turns (never by calendar
    /// day, so a session that resumes after midnight is noticed like any other).
    @discardableResult
    func rediscover(todayStartMs: Int64) -> Bool {
        self.todayStartMs = todayStartMs
        var changed = refreshAvailability()
        var seen: Set<String> = []
        var fresh: [(DiscoveredFile, Root)] = []
        let recentSince = clock() - Self.recheckWindowMs
        let turn = FileID(recheckTurn)
        recheckTurn = (recheckTurn + 1) % Self.recheckTurns
        for root in roots where root.enabled && root.available {
            for path in boundedCandidates(root) where seen.insert(path).inserted {
                if let file = files[path] {
                    guard file.lastStat.modifiedMs >= recentSince || file.fileID % FileID(Self.recheckTurns) == turn,
                          let stat = root.family.stat(path) else { continue }
                    if file.hasChanged(comparedTo: stat) { changed = read(file, live: true) || changed }
                } else if let stat = root.family.stat(path) {
                    fresh.append((DiscoveredFile(path: path, stat: stat), root))
                }
            }
        }
        for path in files.keys where !seen.contains(path) {
            untrack(path)
            changed = true
        }
        fresh.sort { TranscriptDiscovery.startupOrder($0.0, $1.0) }
        for (discovered, root) in fresh {
            let file = track(discovered, family: root.family, root: root.path, start: .beginning)
            changed = read(file, live: true) || changed
        }
        for file in files.values where file.family == .grokBuild {
            var upserts: [ResponseUpsert] = []
            if file.parser.refreshMetadata(deduper: &deduper, emit: { upserts.append($0) }) {
                for upsert in upserts { deliver(upsert, project: projectName(of: file), live: true) }
                syncAgent(file)
                changed = true
            }
        }
        if let opencode {
            if opencodeNeedsSeed {
                seedOpencode(todayStartMs: todayStartMs)
                changed = true
            } else {
                changed = apply(opencode.rediscover(nowMs: clock(), deduper: &deduper), live: true) || changed
            }
        }
        if let zcode {
            if zcodeNeedsSeed {
                zcodeNeedsSeed = false
                changed = apply(zcode.seed(todayStartMs: todayStartMs, deduper: &deduper), live: false) || changed
            } else if let update = zcode.refreshIfChanged(deduper: &deduper) {
                changed = apply(update, live: true) || changed
            }
        }
        if let openclaw {
            changed = apply(openclaw.rediscover(todayStartMs: todayStartMs, deduper: &deduper), live: true) || changed
        }
        if let hermes { changed = apply(hermes.rediscover(deduper: &deduper), live: true) || changed }
        if let trae {
            if traeNeedsSeed {
                traeNeedsSeed = false
                changed = apply(trae.seed(todayStartMs: todayStartMs, deduper: &deduper), live: false) || changed
            } else if let update = trae.refreshIfChanged(deduper: &deduper) {
                changed = apply(update, live: true) || changed
            }
        }
        return changed
    }

    /// Candidate paths of a root, limited to the most recently modified ones when there are too many.
    private func boundedCandidates(_ root: Root) -> [String] {
        let candidates = scan(root)
        guard candidates.count > TranscriptDiscovery.maxFiles else { return candidates }
        let ranked = candidates.compactMap { path -> DiscoveredFile? in
            if let file = files[path] { return DiscoveredFile(path: path, stat: file.lastStat) }
            guard let stat = root.family.stat(path) else { return nil }
            return DiscoveredFile(path: path, stat: stat)
        }
        return TranscriptDiscovery.mostRecent(ranked).map(\.path)
    }

    private func scan(_ root: Root) -> [String] {
        var index = indexes[root.path] ?? DirectoryIndex(root: root.path, names: root.family.names)
        let paths = index.scan()
        indexes[root.path] = index
        return paths
    }

    @discardableResult
    private func read(_ file: TrackedFile, live: Bool) -> Bool {
        if live, let schedule = wholeFileReads, file.reader.readsWholeFile {
            guard !file.wholeReadInFlight else {
                file.wholeReadAgain = true
                return false
            }
            file.wholeReadInFlight = true
            file.isDirty = false
            if let stat = file.family.stat(file.path) { file.lastStat = stat }
            schedule(WholeFileRead(path: file.path, family: file.family, fileID: file.fileID, reader: file.reader,
                                   taskReader: file.parser.taskReader))
            return false
        }
        if file.family.isDatabase { return readDatabase(file, live: live) }
        file.parser.setTaskWatch(live ? taskWatchContext() : nil)
        var upserts: [ResponseUpsert] = []
        let metadataChanged = file.parser.refreshMetadata(deduper: &deduper) { upserts.append($0) }
        for upsert in upserts { deliver(upsert, project: projectName(of: file), live: live) }
        upserts.removeAll(keepingCapacity: true)
        func consume(_ bytes: UnsafeRawBufferPointer, at offset: Int64) {
            file.parser.consume(rawLine: bytes, offset: offset, deduper: &deduper) { upserts.append($0) }
            guard !upserts.isEmpty else { return }
            let project = projectName(of: file)
            for upsert in upserts { deliver(upsert, project: project, live: live) }
            upserts.removeAll(keepingCapacity: true)
        }
        let result: FileTailer.ReadResult
        if file.family == .droid {
            // Byte offsets weigh each call's share of the sidecar's growth (METRICS 4.16).
            result = file.reader.read(onReset: { file.resetParser() }, onLineAt: consume)
        } else {
            result = file.reader.read(onReset: { file.resetParser() }, onMalformed: { file.parser.noteMalformedLine() }) { bytes in
                consume(bytes, at: 0)
            }
        }
        if result.failed, FileStat.lstat(file.path) == nil {
            untrack(file.path)
            return true
        }
        var cycleChanged = false
        if !result.failed {
            // Factory Droid: the transcript's new lines, then the sidecar, then the allocation (METRICS 4.16).
            file.parser.noteReadStart(result.startOffset)
            cycleChanged = file.parser.finishReadCycle { upserts.append($0) }
            let project = projectName(of: file)
            for upsert in upserts { deliver(upsert, project: project, live: live) }
        }
        finishRead(of: file, result: result)
        return metadataChanged || result.bytesRead > 0 || result.didReset || cycleChanged
    }

    /// One read of an Antigravity database, throttled while live (METRICS 5.4).
    private func readDatabase(_ file: TrackedFile, live: Bool) -> Bool {
        let now = DispatchTime.now().uptimeNanoseconds
        if live, file.lastReadNs != 0, now &- file.lastReadNs < Self.databaseReadIntervalNs {
            file.isDirty = true
            return false
        }
        file.isDirty = false
        file.lastReadNs = now
        let (batch, result) = file.reader.readDatabase()
        if result.failed, file.family.stat(file.path) == nil {
            untrack(file.path)
            return true
        }
        if result.didReset { file.resetParser() }
        var upserts: [ResponseUpsert] = []
        file.parser.consume(.antigravity(batch), deduper: &deduper) { upserts.append($0) }
        let project = projectName(of: file)
        for upsert in upserts { deliver(upsert, project: project, live: live) }
        finishRead(of: file, result: result)
        return result.bytesRead > 0 || result.didReset
    }

    /// Applies a whole-file read in one step. A key another file emitted first in the meantime stays that file's.
    /// Returns true when anything changed.
    @discardableResult
    func completeWholeFileRead(_ result: WholeFileResult) -> Bool {
        guard let file = files[result.path], file.fileID == result.fileID else { return false }
        file.wholeReadInFlight = false
        file.reader = result.reader
        if file.family.isDatabase { file.lastReadNs = DispatchTime.now().uptimeNanoseconds }
        if result.read.failed, file.family.stat(file.path) == nil {
            untrack(file.path)
            return true
        }
        if result.read.didReset { file.resetParser() }
        file.parser.taskReader = result.taskReader
        file.parser.setTaskWatch(taskWatchContext())
        var upserts: [ResponseUpsert] = []
        file.parser.consume(result.lines, taskLines: result.taskLines, deduper: &deduper) { upserts.append($0) }
        let project = projectName(of: file)
        for upsert in upserts { deliver(upsert, project: project, live: true) }
        finishRead(of: file, result: result.read)
        if file.wholeReadAgain {
            file.wholeReadAgain = false
            read(file, live: true)
        }
        return result.read.bytesRead > 0 || result.read.didReset
    }

    private func finishRead(of file: TrackedFile, result: FileTailer.ReadResult) {
        file.wasRead = true
        takeTaskCompletions(of: file)
        if result.oversized || file.parser.hasPartialHistory { partialHistory = true }
        if result.failed {
            unreadablePaths.insert(file.path)
        } else {
            unreadablePaths.remove(file.path)
        }
        if file.family == .grokBuild, let stat = file.family.stat(file.path) {
            file.lastStat = stat
        } else if file.family == .droid, let stat = result.stat {
            // The transcript as it was read, with the sidecar's time folded in like `DroidPaths.stat`, so lines written
            // after this read still count as a change.
            file.lastStat = DroidPaths.folding(stat, ofTranscript: file.path)
        } else if let stat = result.stat {
            file.lastStat = stat
        }
        syncAgent(file)
    }

    // MARK: - Engine

    /// The transcript agents of scored sources that were read, with their whole streams (with `TaskStreamMode.history`)
    /// and resolved parents: the probe's task rows (METRICS 6.9 *Dumps*).
    func taskAgents() -> [TaskValueReport.Agent] {
        var parentLinks = self.parentLinks
        if parentLinksAreStale {
            let direct = opencodeLinks.merging(zcodeLinks) { first, _ in first }.merging(openclawLinks) { first, _ in first }
            parentLinks = AgentParents.resolve(Array(parentMembers.values), direct: direct)
        }
        return files.values.filter { $0.wasRead && $0.parser.tasks != nil }.sorted { $0.fileID < $1.fileID }.map { file in
            let state = file.parser.state
            return TaskValueReport.Agent(id: file.path, source: state.source, phase: state.phase,
                                         lastLineMs: state.lastLineMs, parentId: parentLinks[file.path],
                                         events: file.parser.tasks?.events ?? [])
        }
    }

    /// New calibrations from the history analysis: every agent's predictions are evaluated again (METRICS 6.11).
    func setTaskModels(_ models: [SourceKind: TaskModels]) {
        guard models != taskModels else { return }
        taskModels = models
        taskModelsVersion += 1
        for file in files.values where file.parser.tasks != nil { syncAgent(file) }
        openclaw?.taskModels = models
        if let update = openclaw?.taskStatusUpdate() { apply(update, live: false) }
    }

    func advance(
        nowMs: Int64,
        todayStartMs: Int64,
        emitsEvents: Bool,
        subsets: Set<SourceSet> = []
    ) -> (output: EngineOutput, events: [SpeedEvent]) {
        if parentLinksAreStale {
            let direct = opencodeLinks.merging(zcodeLinks) { first, _ in first }.merging(openclawLinks) { first, _ in first }
            parentLinks = AgentParents.resolve(Array(parentMembers.values), direct: direct)
            engine.setParents(parentLinks)
            parentLinksAreStale = false
        }
        completeIdleTasks(nowMs: nowMs)
        let output = engine.advance(nowMs: nowMs, todayStartMs: todayStartMs, emitsEvents: emitsEvents, subsets: subsets)
        let events = pendingEvents + output.events
        pendingEvents.removeAll()
        return (output, events)
    }

    /// Responses currently held by the engine.
    var responses: [ResponseRecord] { engine.storedResponses }

    var malformedLines: Int {
        files.values.reduce(removedMalformedLines) { $0 + $1.parser.state.malformedLines } + (opencode?.malformedRows ?? 0)
            + (openclaw?.malformedRows ?? 0) + (hermes?.malformedLines ?? 0)
    }

    var unreadableFileCount: Int {
        unreadablePaths.count + (opencode?.status == .unreadable ? 1 : 0) + (zcode?.status == .unreadable ? 1 : 0)
            + (trae?.status == .unreadable ? 1 : 0) + (openclaw?.unreadableDatabaseCount ?? 0) + (hermes?.unreadableFileCount ?? 0)
    }

    /// Antigravity databases whose connection is open.
    var openDatabaseCount: Int {
        files.values.filter { $0.family.isDatabase && $0.reader.hasOpenConnection }.count
    }

    /// Sources with data this version cannot read: OpenCode, or an Antigravity database without its tables.
    var unsupportedSources: [SourceKind] {
        var sources: [SourceKind] = []
        if opencode?.status == .unsupported { sources.append(.opencode) }
        if zcode?.status == .unsupported { sources.append(.zcode) }
        if trae?.status == .unsupported { sources.append(.trae) }
        if enabledSources.contains(.antigravity), files.values.contains(where: { $0.family.isDatabase && $0.parser.isUnsupported }) {
            sources.append(.antigravity)
        }
        if openclaw?.hasUnsupportedDatabase == true { sources.append(.openclaw) }
        return sources
    }

    /// Agents of each source with at least one line today; WorkBuddy, Kimi Code CLI, Grok Build, Pi and Factory Droid
    /// count main sessions only (DESIGN 5).
    func sessionCounts(todayStartMs: Int64) -> [SourceKind: Int] {
        var counts: [SourceKind: Int] = [:]
        for file in files.values {
            let state = file.parser.state
            guard let last = state.lastLineMs, last >= todayStartMs, enabledSources.contains(state.source) else { continue }
            if file.family.isWorkBuddy || [.kimi, .grokBuild, .pi, .droid].contains(file.family), state.isSubagent {
                continue
            }
            counts[state.source, default: 0] += 1
        }
        if let opencode, enabledSources.contains(.opencode) {
            counts[.opencode] = opencode.activeSessionCount(todayStartMs: todayStartMs)
        }
        if let zcode { counts[.zcode] = zcode.activeSessionCount(todayStartMs: todayStartMs) }
        if let openclaw { counts[.openclaw] = openclaw.activeSessionCount(todayStartMs: todayStartMs) }
        if let hermes { counts[.hermes] = hermes.activeSessionCount(todayStartMs: todayStartMs) }
        if let trae { counts[.trae] = trae.activeSessionCount(todayStartMs: todayStartMs) }
        return counts
    }

    private func deliver(_ upsert: ResponseUpsert, project: String, live: Bool) {
        guard enabledSources.contains(upsert.record.source) else { return }
        let nowMs = live ? clock() : nil
        if let event = engine.upsert(upsert.record, project: project, isCompletion: upsert.isCompletion, emitsEvents: live, nowMs: nowMs) {
            pendingEvents.append(event)
        }
    }

    private func syncAgent(_ file: TrackedFile) {
        let state = file.parser.state
        if state.lastLineMs != nil, enabledSources.contains(state.source) {
            let member = AgentParents.Member(id: file.path, family: file.family, thread: state.thread)
            if parentMembers.updateValue(member, forKey: file.path) != member { parentLinksAreStale = true }
            var agent = EngineAgent(id: file.path, state: state, parentId: parentLinks[file.path])
            agent.task = file.currentTaskStatus(models: taskModels[state.source] ?? .prior, modelsVersion: taskModelsVersion)
            engine.setAgent(agent)
        } else {
            removeAgent(file.path)
        }
    }

    private func removeAgent(_ path: String) {
        if parentMembers.removeValue(forKey: path) != nil { parentLinksAreStale = true }
        engine.removeAgent(id: path)
    }

    private func projectName(of file: TrackedFile) -> String {
        file.parser.state.project ?? EngineAgent.fallbackProject(for: file.path)
    }

    // MARK: - OpenCode

    private func seedOpencode(todayStartMs: Int64) {
        guard let opencode else { return }
        opencodeNeedsSeed = false
        apply(opencode.seed(todayStartMs: todayStartMs, deduper: &deduper), live: false)
    }

    @discardableResult
    private func apply(_ update: OpencodeSource.Update, live: Bool) -> Bool {
        guard !update.isEmpty else { return false }
        for (upsert, project) in update.upserts { deliver(upsert, project: project, live: live) }
        for id in update.removedAgents { engine.removeAgent(id: id) }
        for (id, state) in update.agents {
            if state.lastLineMs != nil {
                engine.setAgent(EngineAgent(id: id, state: state, parentId: parentLinks[id]))
            } else {
                engine.removeAgent(id: id)
            }
        }
        let links = opencode?.parentLinks ?? [:]
        if links != opencodeLinks {
            opencodeLinks = links
            parentLinksAreStale = true
        }
        return true
    }

    @discardableResult
    private func apply(_ update: ZcodeSource.Update, live: Bool) -> Bool {
        for (upsert, project) in update.upserts { deliver(upsert, project: project, live: live) }
        for id in update.removedAgents { engine.removeAgent(id: id) }
        for (id, state) in update.agents {
            if state.lastLineMs != nil { engine.setAgent(EngineAgent(id: id, state: state, parentId: zcode?.parentLinks[id])) }
        }
        let links = zcode?.parentLinks ?? [:]
        if links != zcodeLinks {
            zcodeLinks = links
            parentLinksAreStale = true
        }
        return !update.isEmpty
    }

    @discardableResult
    func completeTraeRead(_ result: TraeSource.ReadResult) -> Bool {
        guard let update = trae?.complete(result, deduper: &deduper) else { return false }
        return apply(update, live: !result.isSeed)
    }

    @discardableResult
    private func apply(_ update: TraeSource.Update, live: Bool) -> Bool {
        for (upsert, project) in update.upserts { deliver(upsert, project: project, live: live) }
        for id in update.removedAgents { engine.removeAgent(id: id) }
        for (id, state) in update.agents {
            if state.lastLineMs != nil { engine.setAgent(EngineAgent(id: id, state: state, parentId: nil)) }
        }
        partialHistory = partialHistory || trae?.partialHistory == true
        return !update.isEmpty
    }

    @discardableResult
    private func apply(_ update: OpenClawSource.Update, live: Bool) -> Bool {
        for (agentId, completion) in update.completions { recordTaskCompletion(completion, agentId: agentId, source: .openclaw) }
        guard !update.isEmpty else { return false }
        for (upsert, project) in update.upserts { deliver(upsert, project: project, live: live) }
        for id in update.removedAgents { engine.removeAgent(id: id) }
        let links = openclaw?.parentLinks ?? [:]
        if links != openclawLinks {
            openclawLinks = links
            parentLinksAreStale = true
        }
        for (id, state) in update.agents {
            if state.lastLineMs != nil, enabledSources.contains(.openclaw) {
                var agent = EngineAgent(id: id, state: state, parentId: parentLinks[id])
                agent.task = update.tasks[id]
                engine.setAgent(agent)
            } else {
                engine.removeAgent(id: id)
            }
        }
        return true
    }

    @discardableResult
    private func apply(_ update: HermesSource.Update, live: Bool) -> Bool {
        if hermes?.partialHistory == true { partialHistory = true }
        guard !update.isEmpty else { return false }
        for (upsert, project) in update.upserts { deliver(upsert, project: project, live: live) }
        for id in update.removedAgents { engine.removeAgent(id: id) }
        for (id, state) in update.agents {
            if state.lastLineMs != nil, enabledSources.contains(.hermes) {
                var agent = EngineAgent(id: id, state: state)
                if let project = update.projects[id] { agent.project = project }
                engine.setAgent(agent)
            } else {
                engine.removeAgent(id: id)
            }
        }
        return true
    }

    // MARK: - Live reporting (METRICS 11.10)

    /// The report watch's epoch from the reporting side, nil while it is off. A change unwatches every task (a break, or
    /// a new watch whose tasks start from now).
    func setReportWatch(_ epoch: Int?) {
        guard epoch != reportWatch else { return }
        reportWatch = epoch
        for path in watchedFiles { files[path]?.parser.clearTaskWatch() }
        watchedFiles.removeAll()
        openclaw?.clearTaskWatch()
    }

    /// What a live read marks new tasks with; nil while the watch is off or seeding runs.
    private func taskWatchContext() -> ReportTaskWatchContext? {
        guard !seeding, let reportWatch else { return nil }
        return ReportTaskWatchContext(epoch: reportWatch, nowMs: clock())
    }

    private func takeTaskCompletions(of file: TrackedFile) {
        for completion in file.parser.takeTaskCompletions() {
            recordTaskCompletion(completion, agentId: file.path, source: file.parser.state.source)
        }
        if file.parser.hasWatchedTasks {
            watchedFiles.insert(file.path)
        } else {
            watchedFiles.remove(file.path)
        }
    }

    private func recordTaskCompletion(_ completion: TaskCompletion, agentId: String, source: SourceKind) {
        guard reportWatch == completion.epoch, enabledSources.contains(source) else { return }
        engine.recordTaskCompletion(completion, agentId: agentId, source: source)
    }

    /// The 30-minute rule of METRICS 11.10 *Counting*: an agent quiet that long completes its watched tasks, whether or
    /// not it is still listed.
    private func completeIdleTasks(nowMs: Int64) {
        for path in watchedFiles {
            guard let file = files[path], let last = file.parser.state.lastLineMs,
                  TaskRecord.idle(nowMs, since: last) else { continue }
            file.parser.completeWatchedTasks()
            takeTaskCompletions(of: file)
        }
        if let update = openclaw?.completeIdleTasks(nowMs: nowMs) { apply(update, live: true) }
    }

    // MARK: - Tracking

    private func track(_ file: DiscoveredFile, family: TranscriptFamily, root: String, start: FileTailer.Start) -> TrackedFile {
        let tracked = TrackedFile(
            path: file.path, family: family, root: root, fileID: nextFileID, stat: file.stat, start: start,
            maxLineBytes: limits.maxLineBytes, taskMode: taskMode
        )
        nextFileID &+= 1
        files[file.path] = tracked
        return tracked
    }

    private func untrack(_ path: String) {
        watchedFiles.remove(path)
        guard let file = files.removeValue(forKey: path) else { return }
        removedMalformedLines += file.parser.state.malformedLines
        unreadablePaths.remove(path)
        removeAgent(path)
    }

    /// Maps an FSEvents (canonical) path to the configured-root path, if it names a candidate transcript (an
    /// Antigravity `-wal` names its database).
    private func localPath(forCanonical changed: String) -> (String, Root)? {
        for root in roots where root.enabled && root.available {
            let path = root.family.transcriptPath(forChanged: changed)
            guard root.family.names.matches((path as NSString).lastPathComponent) else { continue }
            let prefix = root.canonicalPath == "/" ? "/" : root.canonicalPath + "/"
            guard path.hasPrefix(prefix) else { continue }
            let relative = path.dropFirst(prefix.count)
            let components = relative.split(separator: "/", omittingEmptySubsequences: true)
            guard !components.isEmpty, root.family.names.accepts(relativeComponents: components),
                  !relative.contains("/../") else { return nil }
            let localPrefix = root.path == "/" ? "/" : root.path + "/"
            return (localPrefix + relative, root)
        }
        return nil
    }
}
