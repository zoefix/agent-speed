import Foundation
import Security

/// Keeps the quota estimate (QUOTAS 10) up to date: it gathers the history's and the live engine's spend, Codex's
/// token counts, the sign-in keys and every snapshot, builds the estimate at most every 5 s on its own queue,
/// hands each build to `onBuild` and keeps `estimate.json` (at most once a minute, and at quit). Ids become keys as
/// they arrive; neither ids, keys nor the salt are ever logged.
public final class QuotaEstimator: QuotaSpendSink, @unchecked Sendable {
    public static let buildIntervalMs: Int64 = 5_000
    public static let heartbeatMs: Int64 = 60_000
    public static let writeIntervalMs: Int64 = 60_000

    /// The estimate salt as hex for the remote hosts' quota config (REMOTE 11.1).
    public let saltHex: String
    /// Where sign-in ids become keys.
    public var keys: QuotaAccountKeys { QuotaAccountKeys(salt: salt) }
    /// Called on the estimator's queue with every build.
    public var onBuild: (@Sendable (QuotaEstimateBuild) -> Void)?

    private let queue = DispatchQueue(label: "app.agentspeed.estimate", qos: .utility)
    private let salt: Data
    private let data: URL?
    private let persists: Bool
    private let pricing: PriceBook?
    private let models: QuotaModelIds?
    private let clock: @Sendable () -> Int64

    private var history: QuotaEstimateHistory?
    /// Responses of the live engine by key: this computer's until the history covers them, hosts' for 8 days.
    private var live: [String: QuotaEstimateSpend] = [:]
    private var liveLines: [QuotaCodexLine.ID: QuotaCodexLine] = [:]
    private var codexKeys: [QuotaKeyEntry] = []
    private var claudeProfiles: [QuotaClaudeProfile] = []
    private var snapshots: [QuotaEstimateSnapshot] = []
    private var lastSnapshot: [QuotaEstimateSource: Int64] = [:]
    private var stored = QuotaEstimateStore.empty
    private var enabled: Set<String> = []

    private var dirty = true
    private var scheduled = false
    private var lastBuildMs: Int64?
    private var lastWriteMs: Int64?
    private var written: QuotaEstimateStore?
    private var heartbeat: DispatchSourceTimer?
    public private(set) var latest: QuotaEstimateBuild?

    /// `data` is the app's data folder (`<data>/quota/estimate-salt`, `estimate.json`); nil keeps everything in
    /// memory, as does a salt that cannot be written.
    public init(data: URL?, pricing: PriceBook?, clock: @escaping @Sendable () -> Int64 = { Int64((Date().timeIntervalSince1970 * 1000).rounded(.down)) }) {
        self.data = data
        self.pricing = pricing
        models = pricing.map(QuotaModelIds.init(pricing:))
        self.clock = clock
        if let data, let salt = QuotaEstimateSalt.loadOrCreate(data: data) {
            self.salt = salt
            persists = true
        } else {
            var bytes = [UInt8](repeating: 0, count: QuotaEstimateSalt.byteCount)
            _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
            salt = Data(bytes)
            persists = false
        }
        saltHex = QuotaEstimateSalt.hex(salt)
        if persists, let data {
            queue.async { [self] in stored = QuotaEstimateStore.load(data: data) ?? .empty }
        }
    }

    deinit {
        heartbeat?.cancel()
    }

    /// Starts the minute heartbeat (weights decay and resets pass without new input).
    public func start() {
        queue.async { [self] in
            guard heartbeat == nil else { return }
            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now() + .milliseconds(Int(Self.heartbeatMs)),
                           repeating: .milliseconds(Int(Self.heartbeatMs)), leeway: .seconds(5))
            timer.setEventHandler { [weak self] in
                self?.dirty = true
                self?.buildNow()
            }
            timer.resume()
            heartbeat = timer
            needsBuild()
        }
    }

    public func stop() {
        queue.sync {
            heartbeat?.cancel()
            heartbeat = nil
        }
    }

    // MARK: Inputs

    /// The sources whose quota switch is on: only their values are observed.
    public func setEnabled(_ sources: Set<SourceKind>) {
        let names = Set(sources.map(\.rawValue))
        queue.async { [self] in
            guard enabled != names else { return }
            enabled = names
            needsBuild()
        }
    }

    public func historyUpdated(_ next: QuotaEstimateHistory) {
        queue.async { [self] in
            history = next
            live = live.filter { $0.value.host != nil || $0.value.endMs >= next.cutMs }
            needsBuild()
        }
    }

    public func spend(_ record: ResponseRecord) {
        guard QuotaEstimateSpend.sources.contains(record.source), let models,
              record.endMs >= clock() - QuotaEstimateConstants.keepMs else { return }
        queue.async { [self] in
            guard let item = QuotaEstimateSpend(record, cost: record.costMicroUsd, models: models) else { return }
            if item.host == nil, let cut = history?.cutMs, item.endMs < cut { return }
            guard live[record.key] != item else { return }
            live[record.key] = item
            needsBuild()
        }
    }

    public func codexLines(_ lines: [QuotaCodexLine]) {
        queue.async { [self] in
            for line in lines { liveLines[line.id] = line }
            needsBuild()
        }
    }

    /// The key of `auth.json`'s `tokens.account_id` as read at `at`; only a change is kept.
    public func codexSignIn(key: String, at: Int64) {
        queue.async { [self] in
            if let last = codexKeys.last {
                guard last.key != key else { return }
            } else if latestStored("codex")?.key == key {
                return
            }
            codexKeys.append(QuotaKeyEntry(at: at, key: key))
            needsBuild()
        }
    }

    /// `.claude.json`'s `oauthAccount` as keys; only a change is kept.
    public func claudeSignIn(_ profile: QuotaClaudeProfile) {
        queue.async { [self] in
            if let last = claudeProfiles.last, Self.same(last.entry, profile.entry), last.orgKey == profile.orgKey { return }
            if claudeProfiles.isEmpty, let stored = latestStored("claudeCode"), Self.same(stored, profile.entry),
               stored.orgKey == profile.orgKey { return }
            claudeProfiles.append(profile)
            needsBuild()
        }
    }

    /// A source's snapshot (Claude CLI, Desktop with its organization's key, phase 2, a host's with the relay's key).
    public func snapshot(_ snapshot: QuotaSnapshot, host: String? = nil, orgKey: String? = nil,
                         account: String? = nil, plan: String? = nil, tier: String? = nil) {
        queue.async { [self] in
            let source = QuotaEstimateSource(source: snapshot.source.rawValue, host: host)
            guard snapshot.source != .codex || host != nil, lastSnapshot[source] != snapshot.observedMs else { return }
            lastSnapshot[source] = snapshot.observedMs
            snapshots.append(QuotaEstimateSnapshot(
                source: source.source, host: host, observedMs: snapshot.observedMs, windows: snapshot.windows,
                orgKey: orgKey, account: account, plan: plan, tier: tier))
            needsBuild()
        }
    }

    /// Builds now if anything changed and writes the file (quit).
    public func flush() {
        queue.sync {
            if dirty { buildNow() }
            write(force: true)
        }
    }

    // MARK: Building

    private func needsBuild() {
        dirty = true
        guard !scheduled else { return }
        let now = clock()
        let wait = lastBuildMs.map { max(0, $0 + Self.buildIntervalMs - now) } ?? 0
        scheduled = true
        queue.asyncAfter(deadline: .now() + .milliseconds(Int(wait))) { [weak self] in
            guard let self else { return }
            scheduled = false
            if dirty { buildNow() }
        }
    }

    private func buildNow() {
        let now = clock()
        prune(nowMs: now)
        let build = QuotaEstimateBuild(inputs(nowMs: now), salt: salt, nowMs: now)
        dirty = false
        lastBuildMs = now
        latest = build
        onBuild?(build)
        write(force: false)
    }

    /// Everything gathered, the history's spend before its cut and the live engine's after it.
    func inputs(nowMs: Int64) -> QuotaEstimateInputs {
        var inputs = QuotaEstimateInputs()
        let cut = history?.cutMs ?? .min
        let keep = nowMs - QuotaEstimateConstants.keepMs
        var lines: [QuotaCodexLine.ID: QuotaCodexLine] = [:]
        // A file left unchanged keeps the share it was analysed with: only its last 8 days count.
        for file in (history?.files ?? [:]).keys.sorted() {
            let share = history!.files[file]!
            inputs.spend += share.spend.filter { $0.endMs < cut && $0.endMs >= keep }
            for line in share.lines where line.ts >= keep { lines[line.id] = line }
        }
        for (_, item) in live.sorted(by: { ($0.value.endMs, $0.key) < ($1.value.endMs, $1.key) })
        where item.host != nil || item.endMs >= cut {
            inputs.spend.append(item)
        }
        for (id, line) in liveLines where lines[id] == nil { lines[id] = line }
        let observesCodex = enabled.contains("codex")
        inputs.codexLines = observesCodex ? lines.values.sorted { ($0.file, $0.ts) < ($1.file, $1.ts) } : []
        inputs.codexKeys = codexKeys
        inputs.claudeProfiles = claudeProfiles
        // New values only of the sources whose quota switch is on; stored ones stay until they age out.
        inputs.snapshots = snapshots.filter { enabled.contains($0.source) }
        inputs.stored = stored
        inputs.priceVersion = pricing?.currentVersion ?? 0
        return inputs
    }

    private func prune(nowMs: Int64) {
        let keep = nowMs - QuotaEstimateConstants.keepMs
        live = live.filter { $0.value.endMs >= keep }
        liveLines = liveLines.filter { $0.value.ts >= keep }
        snapshots.removeAll { $0.observedMs < keep }
    }

    private func write(force: Bool) {
        guard persists, let data, let build = latest else { return }
        let now = clock()
        guard force || lastWriteMs.map({ now - $0 >= Self.writeIntervalMs }) ?? true else { return }
        let store = build.store()
        guard store != written else { return }
        do {
            try store.write(data: data)
            written = store
            lastWriteMs = now
        } catch {
            // Tried again at the next build.
        }
    }

    private func latestStored(_ source: String) -> QuotaEstimateStore.Key? {
        stored.keys.filter { $0.source == source }.max { $0.at < $1.at }
    }

    private static func same(_ a: QuotaKeyEntry, _ b: QuotaKeyEntry) -> Bool {
        a.key == b.key && a.plan == b.plan && a.tier == b.tier
    }

    private static func same(_ stored: QuotaEstimateStore.Key, _ entry: QuotaKeyEntry) -> Bool {
        stored.key == entry.key && stored.plan == entry.plan && stored.tier == entry.tier
    }
}
