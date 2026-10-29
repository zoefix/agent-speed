import Foundation

/// Computes the daily history (METRICS 8): every transcript of the enabled sources (plus Codex `archived_sessions`,
/// every omp and Pi root, every Antigravity conversation database, every Hermes Agent log of every profile and every
/// Factory Droid session with its sidecar) and every OpenCode session, parsed completely with the live rules, in startup
/// order, incrementally against a `HistoryDatabase`.
///
/// Not thread-safe: one owner (a serial queue) drives it.
final class HistoryAnalyzer {
    /// Bumped whenever a parser change alters results; stored files of other versions are analysed again.
    /// 2: per-model measured output and time (METRICS 8). 3: cost (METRICS 9.7). 5: reasoning effort and speed.
    /// 6: the copied prefix of paginated Codex sub-agents (METRICS 3.5). 7: the unpriced facts (METRICS 9.10).
    /// 8: the task records (METRICS 6.11).
    static let analysisVersion: Int64 = 8
    /// A file whose last task was still open is analysed again once it has been quiet this long (METRICS 6.11 *Writes*).
    static let openTaskQuietMs: Int64 = 30 * 60_000
    /// The current price table version of the last complete run; when it changes, files with unpriced responses are
    /// analysed again (METRICS 9.7).
    static let priceVersionKey = "priceTableVersion"
    static let opencodeFamily = "opencode"
    static let openclawFamily = "openclaw"
    /// Meta key prefix of an analysed OpenClaw database's identity.
    static let openclawMetaPrefix = "openclaw:"
    static let hermesFamily = "hermes"
    static let maxWorkers = 4

    struct Options: Sendable {
        var timeZone: TimeZone
        var analysisVersion: Int64 = HistoryAnalyzer.analysisVersion
        var workers: Int = HistoryAnalyzer.defaultWorkers
        /// Contributions are written in batches at least this often.
        var saveIntervalMs: Int64 = 1500
        /// The analysis time (`analysisMs` of the task records); nil: the run's start. Tests pin it.
        var nowMs: Int64?

        init(timeZone: TimeZone = .current) {
            self.timeZone = timeZone
        }
    }

    struct Progress: Equatable, Sendable {
        var filesDone = 0
        var filesTotal = 0
        var bytesDone: Int64 = 0
        var bytesTotal: Int64 = 0

        var fraction: Double {
            if bytesTotal > 0 { return min(1, Double(bytesDone) / Double(bytesTotal)) }
            return filesTotal > 0 ? Double(filesDone) / Double(filesTotal) : 1
        }
    }

    struct Report: Equatable, Sendable {
        var filesDiscovered = 0
        var filesAnalyzed = 0
        var bytesAnalyzed: Int64 = 0
        var sessionsAnalyzed = 0
        var filesUnreadable = 0
        var durationMs: Double = 0
        var cancelled = false
    }

    /// Half the cores, at most four: the first analysis stays short without crowding the machine.
    static var defaultWorkers: Int {
        max(2, min(maxWorkers, ProcessInfo.processInfo.activeProcessorCount / 2))
    }

    let database: HistoryDatabase
    /// Shared with the live engine in the app, so today is priced alike (METRICS 9.6).
    let pricing: PriceBook
    private(set) var entries: [String: HistoryDatabase.Entry]
    private var peaks: [String: Double]
    private var indexes: [String: DirectoryIndex] = [:]
    /// Factory Droid sessions' parent ids by transcript, read once per transcript identity (`DroidHistoryIdentity`).
    private var droidParents = DroidHistoryIdentity.ParentCache()
    /// The parent sidecar each Factory Droid session last found unreadable, carried from one analysis to the next
    /// (METRICS 4.16 Own counters); a relaunch starts without it.
    private var droidUnreadableParents: [String: DroidPaths.Signature] = [:]
    /// Files analysed again for an open task, with the identity they had: each is retried once per change and launch.
    private var openTaskRetries: [String: Int64] = [:]
    /// Task records were written since the calibration was last computed.
    private var tasksChanged = true
    /// Bumped whenever task records are written.
    private(set) var taskVersion = 0
    private var calibration: [SourceKind: TaskModels] = [:]

    init(database: HistoryDatabase, pricing: PriceBook = PriceBook()) throws {
        self.database = database
        self.pricing = pricing
        entries = try database.loadEntries()
        peaks = try database.peaks()
        let repaired = pricing.restore(bindings: try database.priceBindings(), tables: try database.priceTables())
        if !repaired.isEmpty {
            PricingLog.logger.notice("Price bindings without a valid table rebound to the current table: \(repaired.count, privacy: .public)")
        }
        try saveBindings()
    }

    /// Stores the dates bound since the last save, with a copy of each referenced table.
    func saveBindings() throws {
        let bindings = pricing.takeNewBindings()
        guard !bindings.isEmpty else { return }
        let pricing = self.pricing
        try database.savePriceBindings(bindings) { pricing.table(version: $0)?.data }
    }

    // MARK: - Planning

    private struct PlannedFile {
        var path: String
        var family: TranscriptFamily
        var root: String
        /// For an Antigravity database, the `.db` with its `-wal` folded in.
        var stat: FileStat
        var previous: HistoryDatabase.Entry?
        /// A Factory Droid session's stored identity: its transcript with its sidecar and its parent's sidecar.
        var droidIdentity: DroidHistoryIdentity?
    }

    private struct PlannedSession {
        var info: OpencodeSessionInfo
        var identity: (rows: Int64, modified: Int64)
        var previous: HistoryDatabase.Entry?
    }

    private struct PlannedUsageSession {
        var source: SourceKind = .zcode
        var path: String
        var records: [ResponseRecord]
        var modified: Int64
        var previous: HistoryDatabase.Entry?
    }

    /// One OpenClaw transcript window to analyse again (METRICS 4.14): its responses were read during planning.
    private struct PlannedOpenClawWindow {
        var agentId: String
        var records: [ResponseRecord]
        var keys: [String]
        var identity: (rows: Int64, modified: Int64)
        var previous: HistoryDatabase.Entry?
        /// Its task lines with the model and effort after each (METRICS 6.9); composed in `run`, where the dedupe set
        /// knows the windows read before it.
        var taskLines: [(line: TaskLine, model: String?, effort: String?)] = []
        var lastLineMs: Int64?
    }

    private struct PlannedOpenClawDatabase {
        var path: String
        var identity: String
        var windows: [PlannedOpenClawWindow] = []
    }

    /// One Hermes Agent log file to analyse again, stored under its identity so a rotation keeps its contributions.
    private struct PlannedHermesFile {
        var entryKey: String
        var path: String
        var folder: String
        var stat: FileStat
        var previous: HistoryDatabase.Entry?
    }

    private struct Plan {
        var files: [PlannedFile] = []
        var discovered = 0
        var opencode: (database: OpencodeDatabase, sessions: [PlannedSession], identity: String)?
        var opencodeIdentity: String?
        var zcode: [PlannedUsageSession] = []
        var zcodeIdentity: String?
        var openclaw: [PlannedOpenClawDatabase] = []
        var hermes: [PlannedHermesFile] = []
        var trae: [PlannedUsageSession] = []
        var traeIdentities: [String: String] = [:]

        var openclawWindows: Int { openclaw.reduce(0) { $0 + $1.windows.count } }

        var bytes: Int64 { files.reduce(0) { $0 + $1.stat.size } + hermes.reduce(0) { $0 + $1.stat.size } }
    }

    /// The work a run would do: discovered files whose identity or analysis version changed, and changed OpenCode
    /// sessions; after a price table change also those with unpriced responses. Cheap when nothing changed.
    private func plan(configuration: MonitorConfiguration, options: Options, zoneChanged: Bool, repricing: Bool) throws -> Plan {
        var plan = Plan()
        let nowMs = options.nowMs ?? Clock.milliseconds(Date())
        var seen: Set<String> = []
        for (family, root) in Self.roots(configuration) {
            guard TranscriptDiscovery.isAvailable(root: root) else { continue }
            // The daily history has no file-count limit (METRICS 8).
            var index = indexes[root] ?? DirectoryIndex(root: root, names: family.names, maxCandidates: .max)
            let paths = index.scan()
            indexes[root] = index
            for path in paths where seen.insert(path).inserted {
                guard let stat = family.stat(path) else { continue }
                plan.discovered += 1
                let previous = entries[path]
                let droidIdentity = family == .droid ? DroidHistoryIdentity(path: path, stat: stat, parents: &droidParents) : nil
                let size = droidIdentity?.size ?? stat.size
                let modified = droidIdentity?.modified ?? stat.modifiedNs
                if let previous, !zoneChanged, previous.version == options.analysisVersion,
                   previous.family == family.rawValue, previous.size == size, previous.modified == modified,
                   !(repricing && previous.hasUnpriced),
                   !reopensTask(path, previous, modifiedMs: stat.modifiedNs / 1_000_000, nowMs: nowMs) {
                    continue
                }
                plan.files.append(PlannedFile(path: path, family: family, root: root, stat: stat, previous: previous,
                                              droidIdentity: droidIdentity))
            }
        }
        plan.files.sort { lhs, rhs in
            TranscriptDiscovery.startupOrder(DiscoveredFile(path: lhs.path, stat: lhs.stat), DiscoveredFile(path: rhs.path, stat: rhs.stat))
        }
        if configuration.readsOpencode {
            try planOpencode(configuration: configuration, options: options, zoneChanged: zoneChanged, repricing: repricing, into: &plan)
        }
        if configuration.readsZcode {
            try planZcode(configuration: configuration, options: options, zoneChanged: zoneChanged, repricing: repricing, into: &plan)
        }
        if configuration.readsTrae {
            try planTrae(configuration: configuration, options: options, zoneChanged: zoneChanged, repricing: repricing, into: &plan)
        }
        if configuration.readsOpenclaw {
            try planOpenclaw(configuration: configuration, options: options, zoneChanged: zoneChanged, repricing: repricing, into: &plan)
        }
        if configuration.readsHermes {
            planHermes(configuration: configuration, options: options, zoneChanged: zoneChanged, repricing: repricing, into: &plan)
        }
        return plan
    }

    /// A file whose records include an incomplete task, quiet for 30 minutes, is analysed again once (METRICS 6.11).
    private func reopensTask(_ path: String, _ previous: HistoryDatabase.Entry, modifiedMs: Int64, nowMs: Int64) -> Bool {
        guard previous.hasOpenTask, nowMs - modifiedMs >= Self.openTaskQuietMs,
              openTaskRetries[path] != previous.modified else { return false }
        openTaskRetries[path] = previous.modified
        return true
    }

    /// Hermes Agent's log files (METRICS 4.15, 8): per log folder in startup order, each stored under its identity
    /// (device and inode), so a renamed (rotated) file is unchanged and the deleted oldest keeps its contributions.
    private func planHermes(configuration: MonitorConfiguration, options: Options, zoneChanged: Bool,
                            repricing: Bool, into plan: inout Plan) {
        var seen: Set<String> = []
        for folder in configuration.resolvedHermesFolders {
            for file in HermesSource.discover(folder).sorted(by: TranscriptDiscovery.startupOrder) {
                let key = Self.hermesEntryKey(file.stat.identity)
                guard seen.insert(key).inserted else { continue }
                plan.discovered += 1
                let previous = entries[key]
                if let previous, !zoneChanged, previous.version == options.analysisVersion, previous.family == Self.hermesFamily,
                   previous.size == file.stat.size, previous.modified == file.stat.modifiedNs,
                   !(repricing && previous.hasUnpriced) {
                    continue
                }
                plan.hermes.append(PlannedHermesFile(entryKey: key, path: file.path, folder: folder, stat: file.stat,
                                                     previous: previous))
            }
        }
    }

    /// The store key of a Hermes Agent log: its identity, not its path.
    static func hermesEntryKey(_ identity: FileIdentity) -> String {
        "hermes:\(identity.device):\(identity.inode)"
    }

    /// OpenClaw's agent databases in startup order (METRICS 4.14, 8). A database whose files did not change is not
    /// opened; otherwise every window whose highest `seq` or `transcript_updated_at` changed is parsed completely.
    /// Responses are read here and claimed in `run`, in this order, with the shared dedupe set.
    private func planOpenclaw(configuration: MonitorConfiguration, options: Options, zoneChanged: Bool,
                              repricing: Bool, into plan: inout Plan) throws {
        var seen: Set<String> = []
        let paths = configuration.resolvedOpenclawRoots.flatMap(OpenClawHome.databases(in:)).filter { seen.insert($0).inserted }
        let reprices = repricing && entries.values.contains { $0.family == Self.openclawFamily && $0.hasUnpriced }
        let nowMs = options.nowMs ?? Clock.milliseconds(Date())
        for path in OpenClawSource.startupOrder(paths) {
            guard let stat = FileStat.lstat(path), stat.isRegularFile else { continue }
            plan.discovered += 1
            let wal = FileStat.lstat(path + "-wal")
            let identity = [path, "\(stat.identity.device):\(stat.identity.inode)", "\(stat.size):\(stat.modifiedNs)",
                            wal.map { "\($0.size):\($0.modifiedNs)" } ?? "-", "v\(options.analysisVersion)"].joined(separator: "|")
            let prefix = path + "#"
            let openTask = entries.contains { key, entry in
                key.hasPrefix(prefix) && entry.hasOpenTask && nowMs - entry.modified >= Self.openTaskQuietMs
                    && openTaskRetries[key] != entry.modified
            }
            if !zoneChanged, !reprices, !openTask, try database.meta(Self.openclawMetaPrefix + path) == identity { continue }
            var planned = PlannedOpenClawDatabase(path: path, identity: identity)
            let source: OpenClawDatabase
            do {
                source = try OpenClawDatabase(path: path)
            } catch .unsupported {
                // No session tables (2026.7 and older): opened again only once its files change.
                plan.openclaw.append(planned)
                continue
            } catch {
                // A busy or unreadable database is tried again on the next run.
                continue
            }
            do {
                try source.read {
                    for window in try source.windows() {
                        let agentId = OpenClawSource.agentId(path, window.sessionId)
                        let rows = try source.maxSeq(session: window.sessionId) ?? 0
                        let previous = entries[agentId]
                        if let previous, !zoneChanged, previous.version == options.analysisVersion, previous.size == rows,
                           previous.modified == window.activityMs, !(repricing && previous.hasUnpriced),
                           !reopensTask(agentId, previous, modifiedMs: window.activityMs, nowMs: nowMs) {
                            continue
                        }
                        // The window's own dedupe set orders its keys; ownership across windows is decided in `run`.
                        var local = Deduper(capacity: .max)
                        var parser = OpenClawParser(agentId: agentId, fileID: 0, window: window)
                        var latest: [String: ResponseRecord] = [:]
                        var order: [String] = []
                        var tasks = TaskLineReader(source: .openclaw)
                        tasks?.mirrorWindow = window.mirrorsTurns
                        var taskLines: [(line: TaskLine, model: String?, effort: String?)] = []
                        try source.rows(session: window.sessionId, after: .min, maxLineBytes: FileTailer.defaultMaxLineBytes,
                                        readsTools: false, tasks: &tasks) { row in
                            guard let entry = row.entry else { return }
                            parser.consume(entry, deduper: &local) { upsert in
                                if latest[upsert.record.key] == nil { order.append(upsert.record.key) }
                                latest[upsert.record.key] = upsert.record
                            }
                            guard case .line = row.task else { return }
                            let effort = parser.state.modes.attribution(for: .effort)
                            taskLines.append((row.task, parser.state.model, effort.isUnknown ? nil : effort.mode))
                        }
                        planned.windows.append(PlannedOpenClawWindow(
                            agentId: agentId, records: order.compactMap { latest[$0] }, keys: order,
                            identity: (rows, window.activityMs), previous: previous, taskLines: taskLines,
                            lastLineMs: parser.state.lastLineMs))
                    }
                }
            } catch {
                continue
            }
            plan.openclaw.append(planned)
        }
    }

    private func planZcode(configuration: MonitorConfiguration, options: Options, zoneChanged: Bool,
                           repricing: Bool, into plan: inout Plan) throws {
        let path = ZcodeDatabase.databasePath(forRoot: configuration.resolvedZcodeRoot)
        guard let stat = FileStat.stat(path), stat.isRegularFile else { return }
        plan.discovered += 1
        let wal = FileStat.stat(path + "-wal")
        let identity = [path, "\(stat.identity.device):\(stat.identity.inode)", "\(stat.size):\(stat.modifiedNs)",
                        wal.map { "\($0.size):\($0.modifiedNs)" } ?? "-", "v\(options.analysisVersion)"].joined(separator: "|")
        let reprices = repricing && entries.values.contains { $0.family == "zcode" && $0.hasUnpriced }
        if !zoneChanged, !reprices, try database.meta("zcode") == identity { return }
        guard let source = try? ZcodeDatabase(path: path), let batch = try? source.read() else { return }
        plan.zcodeIdentity = identity
        let grouped = Dictionary(grouping: batch.rows, by: \.sessionId)
        for session in batch.sessions.sorted(by: { $0.id < $1.id }) {
            let key = source.agentId(session.id)
            let records = (grouped[session.id] ?? []).compactMap(source.record)
            plan.zcode.append(PlannedUsageSession(path: key, records: records,
                                                  modified: max(stat.modifiedNs, wal?.modifiedNs ?? 0), previous: entries[key]))
        }
    }

    private func planTrae(configuration: MonitorConfiguration, options: Options, zoneChanged: Bool,
                          repricing: Bool, into plan: inout Plan) throws {
        for path in Set(configuration.resolvedTraeRoots.map(TraeDatabase.databasePath)).sorted() {
            guard let stat = FileStat.lstat(path), stat.isRegularFile else { continue }
            plan.discovered += 1
            let signature = TraeSource.signature(path)
            let identity = signature.map { value in
                value.map { "\($0.identity.device):\($0.identity.inode):\($0.size):\($0.modifiedNs)" } ?? "-"
            }.joined(separator: "|") + "|v\(options.analysisVersion)|trae-v\(TraeDatabase.analysisRevision)"
            let meta = "trae:" + path
            let reprices = repricing && entries.values.contains { $0.family == "trae" && $0.hasUnpriced }
            if !zoneChanged, !reprices, try database.meta(meta) == identity { continue }
            guard let source = try? TraeDatabase(path: path), let rows = try? source.read() else { continue }
            plan.traeIdentities[meta] = identity
            let grouped = Dictionary(grouping: rows.map(\.record), by: \.agentId)
            for agent in grouped.keys.sorted() {
                plan.trae.append(PlannedUsageSession(source: .trae, path: agent, records: grouped[agent] ?? [],
                    modified: signature.compactMap { $0?.modifiedNs }.max() ?? stat.modifiedNs, previous: entries[agent]))
            }
        }
    }

    private func planOpencode(
        configuration: MonitorConfiguration,
        options: Options,
        zoneChanged: Bool,
        repricing: Bool,
        into plan: inout Plan
    ) throws {
        let path = OpencodeDatabase.databasePath(forRoot: configuration.resolvedOpencodeRoot)
        guard let stat = FileStat.stat(path), stat.isRegularFile else { return }
        plan.discovered += 1
        let wal = FileStat.stat(path + "-wal")
        let identity = [path, "\(stat.identity.device):\(stat.identity.inode)", "\(stat.size):\(stat.modifiedNs)",
                        wal.map { "\($0.size):\($0.modifiedNs)" } ?? "-", "v\(options.analysisVersion)"].joined(separator: "|")
        let reprices = repricing && entries.contains { $0.value.family == Self.opencodeFamily && $0.value.hasUnpriced }
        if !zoneChanged, !reprices, try database.meta("opencode") == identity { return }
        // A busy, unreadable or unsupported database is tried again on the next run.
        guard let opencode = try? OpencodeDatabase(path: path), let activity = try? opencode.sessionActivity(),
              let infos = try? opencode.sessions() else { return }
        plan.opencodeIdentity = identity
        var sessions: [PlannedSession] = []
        for info in infos.sorted(by: { $0.id < $1.id }) {
            let rows = activity[info.id]
            let modified = max(info.updatedMs, rows?.lastUpdatedMs ?? 0)
            let previous = entries[OpencodeRules.agentId(session: info.id)]
            let count = rows?.rows ?? 0
            if let previous, !zoneChanged, previous.version == options.analysisVersion,
               previous.size == count, previous.modified == modified, !(repricing && previous.hasUnpriced) {
                continue
            }
            sessions.append(PlannedSession(info: info, identity: (count, modified), previous: previous))
        }
        plan.opencode = (opencode, sessions, identity)
    }

    /// Transcript roots of the enabled sources (both Gemini CLI roots, every omp root that is not Pi's, every readable
    /// Antigravity store, every Pi root); Codex also contributes its `archived_sessions` sibling.
    static func roots(_ configuration: MonitorConfiguration) -> [(TranscriptFamily, String)] {
        var roots: [(TranscriptFamily, String)] = []
        for root in configuration.allRoots() where configuration.reads(root.family) {
            if root.readable { roots.append((root.family, root.path)) }
            if root.family == .codex { roots.append((root.family, MonitorConfiguration.codexArchiveRoot(of: root.path))) }
        }
        return roots
    }

    // MARK: - Running

    /// Analyses what changed and stores it. `progress` is called after every file with the work done so far.
    @discardableResult
    func run(
        configuration: MonitorConfiguration,
        options: Options = Options(),
        isCancelled: () -> Bool = { false },
        progress: (Progress) -> Void = { _ in }
    ) throws -> Report {
        let started = DispatchTime.now().uptimeNanoseconds
        var report = Report()
        let zone = options.timeZone.identifier
        let storedZone = try database.meta("timeZone")
        let zoneChanged = !entries.isEmpty && storedZone != zone
        let priceVersion = String(pricing.currentVersion)
        let storedPriceVersion = try database.meta(Self.priceVersionKey)
        let repricing = !entries.isEmpty && storedPriceVersion != priceVersion
        let plan = try plan(configuration: configuration, options: options, zoneChanged: zoneChanged, repricing: repricing)
        report.filesDiscovered = plan.discovered
        let sessions = plan.opencode?.sessions ?? []
        var state = Progress(filesTotal: plan.files.count + sessions.count + plan.zcode.count + plan.openclawWindows
                                + plan.hermes.count + plan.trae.count, bytesTotal: plan.bytes)
        progress(state)
        func elapsedMs() -> Double { Double(DispatchTime.now().uptimeNanoseconds - started) / 1_000_000 }

        if plan.files.isEmpty && sessions.isEmpty && plan.zcode.isEmpty && plan.openclaw.isEmpty && plan.hermes.isEmpty && plan.trae.isEmpty {
            try finish(plan: plan, zone: zone, priceVersion: priceVersion)
            report.durationMs = elapsedMs()
            return report
        }

        var deduper = Deduper.hashed()
        let replaced = Set(plan.files.compactMap(\.previous?.id) + sessions.compactMap(\.previous?.id) + plan.zcode.compactMap(\.previous?.id)
            + plan.openclaw.flatMap { $0.windows.compactMap(\.previous?.id) } + plan.hermes.compactMap(\.previous?.id) + plan.trae.compactMap(\.previous?.id))
        try database.loadKeys(excluding: replaced, into: &deduper)
        var labeler = DayLabeler(timeZone: options.timeZone)
        var pending: [(path: String, entry: HistoryDatabase.Entry, keys: [UInt64])] = []
        var pendingTasks: [String: [TaskRecord]] = [:]
        var lastSave = DispatchTime.now().uptimeNanoseconds
        let analysisMs = options.nowMs ?? Clock.milliseconds(Date())

        func flush(force: Bool) throws {
            let now = DispatchTime.now().uptimeNanoseconds
            guard !pending.isEmpty, force || Int64(now - lastSave) / 1_000_000 >= options.saveIntervalMs else { return }
            let ids = try database.save(pending, taskRecords: pendingTasks)
            for (path, id) in ids { entries[path]?.id = id }
            try saveBindings()
            pending.removeAll(keepingCapacity: true)
            pendingTasks.removeAll(keepingCapacity: true)
            lastSave = now
            tasksChanged = true
            taskVersion += 1
        }

        if !plan.files.isEmpty {
            let jobs = plan.files.map { OrderedDecoder.Job(path: $0.path, family: $0.family, size: $0.stat.size) }
            let decoder = OrderedDecoder(jobs: jobs)
            decoder.start(workers: options.workers)
            defer {
                decoder.cancel()
                decoder.waitForWorkers()
            }
            for (index, file) in plan.files.enumerated() {
                if isCancelled() {
                    report.cancelled = true
                    break
                }
                guard let output = decoder.next() else { break }
                state.filesDone += 1
                state.bytesDone += file.stat.size
                if output.read.failed {
                    // Unreadable or vanished: keep what was stored, including its key ownership.
                    report.filesUnreadable += 1
                    if let id = file.previous?.id { deduper.insertSettled(try database.keys(ofFile: id)) }
                    progress(state)
                    continue
                }
                // Parsers bridge paths through NSString; drain those per file on this long-running thread.
                let (days, keys, unknown, tasks) = autoreleasepool {
                    () -> ([UsageKey: DayAggregate], [UInt64], Bool, [TaskRecord]?) in
                    var parser = TranscriptParser(
                        family: file.family, agentId: file.path, fileID: FileID(truncatingIfNeeded: index), root: file.root,
                        tasks: .history)
                    if file.family == .droid { parser.droidUnreadableParent = droidUnreadableParents[file.path] }
                    var latest: [String: ResponseRecord] = [:]
                    parser.consume(output.lines, taskLines: output.tasks, deduper: &deduper) { latest[$0.record.key] = $0.record }
                    // Factory Droid's history rule: the whole transcript, then one sidecar read (METRICS 4.16).
                    _ = parser.finishReadCycle { latest[$0.record.key] = $0.record }
                    if file.family == .droid {
                        droidUnreadableParents[file.path] = parser.droidUnreadableParent
                        if parser.droidOwnCountersUnknown { return ([:], deduper.finishFile(), true, nil) }
                    }
                    parser.finishHistory { latest[$0.record.key] = $0.record }
                    let days = DayAggregate.contributions(
                        of: latest.values, source: parser.state.source, labeler: &labeler, pricing: pricing)
                    // The task records of the file read whole (METRICS 6.11 *Task records*).
                    let tasks = parser.tasks.map {
                        TaskTranscriptReading.records(of: $0, agentId: file.path, source: parser.state.source,
                                                      lastLineMs: parser.state.lastLineMs, analysisMs: analysisMs,
                                                      timeZone: options.timeZone)
                    }
                    return (days, deduper.finishFile(), false, tasks)
                }
                if unknown {
                    // A Factory Droid session whose parent sidecar could not be read: not stored, so the next analysis
                    // tries again (METRICS 4.16 Own counters).
                    progress(state)
                    continue
                }
                let stat = file.family == .grokBuild ? file.family.stat(file.path) ?? output.read.stat ?? file.stat
                    : output.read.stat ?? file.stat
                var entry = HistoryDatabase.Entry(
                    id: file.previous?.id,
                    family: file.family.rawValue,
                    size: file.droidIdentity?.size ?? stat.size,
                    modified: file.droidIdentity?.modified ?? stat.modifiedNs,
                    version: options.analysisVersion,
                    days: days
                )
                entry.hasOpenTask = tasks?.contains { !$0.complete } ?? false
                entries[file.path] = entry
                pending.append((file.path, entry, keys))
                if let tasks { pendingTasks[file.path] = tasks }
                report.filesAnalyzed += 1
                report.bytesAnalyzed += stat.size
                try flush(force: false)
                progress(state)
            }
        }

        var unreadSessions = 0
        if let opencode = plan.opencode, !report.cancelled {
            let fileID = TranscriptSession.opencodeFileID
            var database = opencode.database
            for session in opencode.sessions {
                // An immutable connection is only valid while OpenCode does not write (METRICS 4.5).
                if database.isStale, let reopened = try? OpencodeDatabase(path: database.path) { database = reopened }
                if isCancelled() {
                    report.cancelled = true
                    break
                }
                guard let rows = try? database.rows(session: session.info.id) else {
                    unreadSessions += 1
                    continue
                }
                var records: [ResponseRecord] = []
                for row in rows {
                    guard let record = OpencodeRules.response(for: row, session: session.info),
                          deduper.claim(row.id, for: fileID) != .otherFile else { continue }
                    records.append(record)
                }
                let keys = deduper.finishFile()
                let path = OpencodeRules.agentId(session: session.info.id)
                let entry = HistoryDatabase.Entry(
                    id: session.previous?.id,
                    family: Self.opencodeFamily,
                    size: session.identity.rows,
                    modified: session.identity.modified,
                    version: options.analysisVersion,
                    days: DayAggregate.contributions(of: records, source: .opencode, labeler: &labeler, pricing: pricing)
                )
                entries[path] = entry
                pending.append((path, entry, keys))
                report.sessionsAnalyzed += 1
                state.filesDone += 1
                try flush(force: false)
                progress(state)
            }
        }

        if !report.cancelled {
            for session in plan.zcode + plan.trae {
                if isCancelled() { report.cancelled = true; break }
                let records = session.records.filter { deduper.claim($0.key, for: session.source == .trae ? TranscriptSession.traeFileID : TranscriptSession.zcodeFileID) != .otherFile }
                let keys = deduper.finishFile()
                let entry = HistoryDatabase.Entry(id: session.previous?.id, family: session.source.rawValue, size: Int64(records.count),
                                                  modified: session.modified, version: options.analysisVersion,
                                                  days: DayAggregate.contributions(of: records, source: session.source, labeler: &labeler, pricing: pricing))
                entries[session.path] = entry
                pending.append((session.path, entry, keys))
                report.sessionsAnalyzed += 1
                state.filesDone += 1
                try flush(force: false)
                progress(state)
            }
        }

        if !report.cancelled {
            // Each window owns its keys like a file (METRICS 3.4, 8); window ids only need to differ from each other.
            var windowID = OpenClawSource.firstFileID
            for database in plan.openclaw {
                for window in database.windows {
                    if isCancelled() { report.cancelled = true; break }
                    let fileID = windowID
                    windowID &+= 1
                    // Before the window claims its keys: an assistant entry another window owns is a duplicate (6.9).
                    var composer = TaskComposer(source: .openclaw, keepsEveryTask: true)
                    for (line, model, effort) in window.taskLines {
                        if let t = composer.feed(line, isDuplicate: { deduper.isOwnedByOtherFile($0, for: fileID) }) {
                            composer.note(t: t, model: model, effort: effort)
                        }
                    }
                    let tasks = TaskTranscriptReading.records(of: composer, agentId: window.agentId, source: .openclaw,
                                                              lastLineMs: window.lastLineMs, analysisMs: analysisMs,
                                                              timeZone: options.timeZone)
                    let records = window.records.filter { deduper.claim($0.key, for: fileID) != .otherFile }
                    let keys = deduper.finishFile()
                    var entry = HistoryDatabase.Entry(
                        id: window.previous?.id, family: Self.openclawFamily, size: window.identity.rows,
                        modified: window.identity.modified, version: options.analysisVersion,
                        days: DayAggregate.contributions(of: records, source: .openclaw, labeler: &labeler, pricing: pricing))
                    entry.hasOpenTask = tasks.contains { !$0.complete }
                    entries[window.agentId] = entry
                    pending.append((window.agentId, entry, keys))
                    pendingTasks[window.agentId] = tasks
                    report.sessionsAnalyzed += 1
                    state.filesDone += 1
                    try flush(force: false)
                    progress(state)
                }
                if report.cancelled { break }
            }
        }

        if !report.cancelled {
            let zone = configuration.resolvedHermesTimeZone
            for (index, file) in plan.hermes.enumerated() {
                if isCancelled() { report.cancelled = true; break }
                state.filesDone += 1
                state.bytesDone += file.stat.size
                let fileID = HermesSource.firstFileID &+ FileID(truncatingIfNeeded: index)
                guard let (days, size, modified) = analyseHermes(file, fileID: fileID, zone: zone, deduper: &deduper,
                                                                 labeler: &labeler) else {
                    // Unreadable, vanished or renamed meanwhile: keep what was stored, including its key ownership.
                    report.filesUnreadable += 1
                    if let id = file.previous?.id { deduper.insertSettled(try database.keys(ofFile: id)) }
                    progress(state)
                    continue
                }
                let entry = HistoryDatabase.Entry(id: file.previous?.id, family: Self.hermesFamily, size: size,
                                                  modified: modified, version: options.analysisVersion, days: days)
                entries[file.entryKey] = entry
                pending.append((file.entryKey, entry, deduper.finishFile()))
                report.filesAnalyzed += 1
                report.bytesAnalyzed += size
                try flush(force: false)
                progress(state)
            }
        }

        try flush(force: true)
        try saveBindings()
        if !report.cancelled {
            try finish(plan: plan, zone: zone, priceVersion: priceVersion, opencodeComplete: unreadSessions == 0)
        }
        report.durationMs = elapsedMs()
        return report
    }

    /// One Hermes Agent log read whole with the live rules. A session's day counts as one agent in the file that first
    /// claims it, so a session spread over rotated files is one agent of the day (METRICS 8). Nil when the file cannot
    /// be read or its path names another file now.
    private func analyseHermes(
        _ file: PlannedHermesFile,
        fileID: FileID,
        zone: TimeZone,
        deduper: inout Deduper,
        labeler: inout DayLabeler
    ) -> (days: [UsageKey: DayAggregate], size: Int64, modified: Int64)? {
        var parser = HermesLogSet(folder: file.folder, timeZone: zone, fileID: fileID)
        var tailer = FileTailer(path: file.path, start: .beginning)
        var records: [ResponseRecord] = []
        let read = autoreleasepool {
            tailer.read(expecting: file.stat.identity) { line in
                parser.consume(line, deduper: &deduper) { records.append($0.record) }
            }
        }
        guard !read.failed, !read.otherFile, let stat = read.stat else {
            _ = deduper.finishFile()
            return nil
        }
        var days = DayAggregate.contributions(of: records, source: .hermes, labeler: &labeler, pricing: pricing)
        var agents: [String: Int] = [:]
        var claimed: Set<String> = []
        for record in records where DayLabeler.hasDay(record.endMs) {
            let day = labeler.label(forMs: record.endMs)
            let marker = "hermes-agent-day:" + day + ":" + record.agentId
            guard claimed.insert(marker).inserted else { continue }
            if deduper.claim(marker, for: fileID) == .new { agents[day, default: 0] += 1 }
        }
        for key in days.keys { days[key]?.agents = agents[key.day] ?? 0 }
        return (days, stat.size, stat.modifiedNs)
    }

    private func finish(plan: Plan, zone: String, priceVersion: String, opencodeComplete: Bool = true) throws {
        try database.setMeta("timeZone", zone)
        try database.setMeta(Self.priceVersionKey, priceVersion)
        if opencodeComplete, let identity = plan.opencodeIdentity { try database.setMeta("opencode", identity) }
        if let identity = plan.zcodeIdentity { try database.setMeta("zcode", identity) }
        for planned in plan.openclaw { try database.setMeta(Self.openclawMetaPrefix + planned.path, planned.identity) }
        for (key, identity) in plan.traeIdentities { try database.setMeta(key, identity) }
    }

    // MARK: - Results

    // MARK: - Task records (METRICS 6.11)

    /// Each scored source's prediction coefficients, from the stored records; computed again only after records were
    /// written (in memory, never stored).
    func taskCalibration() throws -> [SourceKind: TaskModels] {
        guard tasksChanged else { return calibration }
        calibration = try database.taskCalibration()
        tasksChanged = false
        return calibration
    }

    /// The complete records of the enabled sources, without their feature rows: the comparison's input.
    func comparisonRecords(sources: Set<SourceKind>) throws -> [TaskRecord] {
        try database.taskRecords(rows: false, completeOnly: true).filter { sources.contains($0.source) }
    }

    /// Daily totals: sums over files, for the given sources, sorted by day then source. Each contribution's
    /// unpriced facts are read on their own and then summed (METRICS 9.10 *Reading*).
    func days(sources: Set<SourceKind>) -> [DailyUsage] {
        var totals: [UsageKey: DayAggregate] = [:]
        var unpriced: [UsageKey: [UnpricedPart]] = [:]
        for entry in entries.values {
            for (key, day) in entry.days where sources.contains(key.source) {
                totals[key, default: DayAggregate()].merge(day)
                let reading = day.unpricedReading
                unpriced[key, default: []].append(UnpricedPart(
                    day: key.day, source: key.source, requests: reading.requests, noCostData: reading.noCostData,
                    derived: reading.derived, facts: reading.facts))
            }
        }
        return totals
            .sorted { lhs, rhs in
                lhs.key.day != rhs.key.day ? lhs.key.day < rhs.key.day : lhs.key.source.rawValue < rhs.key.source.rawValue
            }
            .map {
                let part = UnpricedPart.merged(unpriced[$0.key] ?? []).first
                return $0.value.usage(
                    day: $0.key.day, source: $0.key.source, peak: peaks[$0.key.day],
                    priceVersion: pricing.boundVersion(forDay: $0.key.day),
                    unpriced: UnpricedReading(requests: part?.requests ?? 0, noCostData: part?.noCostData ?? 0,
                                              facts: part?.facts ?? [], derived: part?.derived ?? 0))
            }
    }

    /// Stores a day's live peak (all sources); returns true when it raised the stored value.
    @discardableResult
    func recordPeak(_ peak: Double, day: String) throws -> Bool {
        guard peak > (peaks[day] ?? 0) else { return false }
        try database.recordPeak(peak, day: day)
        peaks[day] = peak
        return true
    }
}
