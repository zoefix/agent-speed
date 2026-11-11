import Foundation

/// The newest instance of one account and window, ready for a report entry or a display line at any later moment.
public struct QuotaEntryBase: Sendable {
    public var account: String
    /// The source of the newest vendor value.
    public var source: String
    /// The sources whose spend counts for the account, in source order.
    public var sources: [String]
    public var plan: String?
    public var tier: String?
    public var kind: QuotaWindowKind
    public var name: String?
    public var minutes: Int
    public var p: Double
    public var t: Int64
    public var resetsMs: Int64
    public var start: Int64
    public var estimate: QuotaCapEstimate
    public var firstAfterReset: Int64?
    /// `plan` and `tier` were inferred from the quota (QUOTAS 10.2).
    public var planInferred = false
    /// Where the entry goes in a report; the account's tag breaks the ties that remain.
    var rank = QuotaEntryRank()

    public var window: String { QuotaEstimateWindow.id(kind: kind, name: name, minutes: minutes) }
    var modelName: String? { QuotaEstimateWindow.modelName(kind: kind, name: name) }
}

/// Report order (REPORTING-EXTENSIONS 11.2): the first source's rank, the window's (QUOTAS 2: unnamed by length,
/// then named in the order the account showed them), then the source of the newest value.
struct QuotaEntryRank: Comparable, Sendable {
    var source = 0
    var named = 0
    var name = 0
    var minutes = 0
    var kind = 0
    var newest = ""

    static func < (a: Self, b: Self) -> Bool {
        (a.source, a.named, a.name, a.minutes, a.kind, a.newest) < (b.source, b.named, b.name, b.minutes, b.kind, b.newest)
    }
}

extension QuotaEstimateBuild {
    static let sourceOrder: [String] = SourceKind.allCases.map(\.rawValue)

    static func sourceRank(_ source: String) -> Int {
        sourceOrder.firstIndex(of: source) ?? sourceOrder.count
    }

    /// One base per account and window, its newest instance, in report order (source order, then QUOTAS 2). An account
    /// without a plan takes its inferred one (QUOTAS 10.2).
    static func current(_ instances: [QuotaBuiltInstance], estimates: [QuotaCapClass: QuotaCapEstimate],
                        inferredPlans: [String: QuotaInferredPlan], spend: [QuotaEstimateSpend], accounts: [String?],
                        observations: [QuotaObservation], nowMs: Int64) -> [QuotaEntryBase] {
        struct Key: Hashable {
            var account: String
            var window: String
        }
        var order: [Key] = []
        var newest: [Key: (QuotaBuiltInstance, QuotaObservation)] = [:]
        for x in instances {
            let key = Key(account: x.account, window: x.window)
            let last = x.instance.observations[x.instance.observations.count - 1]
            if let current = newest[key] {
                if (last.t, last.tLo) > (current.1.t, current.1.tLo) { newest[key] = (x, last) }
            } else {
                order.append(key)
                newest[key] = (x, last)
            }
        }
        var spendOf: [String: [Int]] = [:]
        for (i, account) in accounts.enumerated() {
            if let account { spendOf[account, default: []].append(i) }
        }
        var names: [String: [String]] = [:]
        for o in observations.stableSorted(by: { $0.t < $1.t }) {
            if let name = o.name, !name.isEmpty, !(names[o.account]?.contains(name) ?? false) {
                names[o.account, default: []].append(name)
            }
        }
        var rows: [QuotaEntryBase] = []
        for key in order {
            let (x, last) = newest[key]!
            let instance = x.instance
            let own = spendOf[key.account] ?? []
            var firstAfter: Int64?
            if instance.observations[0].kind == .fiveHour {
                firstAfter = own.compactMap { i -> Int64? in
                    guard let start = spend[i].startMs, start >= instance.resetsMs, spend[i].endMs <= nowMs else { return nil }
                    return start
                }.min()
            }
            var sourceSet = Set(own.filter { spend[$0].endMs >= instance.start }.map { spend[$0].source })
            sourceSet.insert(last.source)
            let sources = sourceSet.sorted { (sourceRank($0), $0) < (sourceRank($1), $1) }
            var rank = QuotaEntryRank(source: sourceRank(sources[0]), minutes: last.minutes, kind: last.kind.rank,
                                      newest: last.source)
            if let name = last.name, !name.isEmpty {
                rank.named = 1
                rank.name = names[key.account]?.firstIndex(of: name) ?? 0
            }
            // It fills only a missing plan and never moves the class.
            let inferred = x.plan == nil ? inferredPlans[key.account] : nil
            rows.append(QuotaEntryBase(
                account: key.account, source: last.source, sources: sources, plan: inferred?.plan ?? x.plan,
                tier: inferred.map(\.tier) ?? x.tier, kind: last.kind, name: last.name, minutes: last.minutes, p: last.p,
                t: last.t, resetsMs: instance.resetsMs, start: instance.start, estimate: estimates[x.capClass] ?? .none,
                firstAfterReset: firstAfter, planInferred: inferred != nil, rank: rank))
        }
        return rows.stableSorted { $0.rank < $1.rank }
    }

    /// The report entries for the service of api origin `origin` at `nowMs` (REPORTING-EXTENSIONS 11.2), at most 16,
    /// of the sources `included` lets through. Each carries its account's tag for that service, which also orders
    /// entries that tie otherwise (two accounts of one source and window).
    public func entries(nowMs: Int64, origin: String, including included: (String) -> Bool = { _ in true })
        -> [QuotaReportEntry] {
        let tagged = current.filter { included($0.source) }.map {
            (base: $0, tag: QuotaAccounts.reportTag(origin: origin, key: $0.account))
        }
        return tagged.stableSorted { ($0.base.rank, $0.tag) < ($1.base.rank, $1.tag) }
            .prefix(QuotaEstimateConstants.maxEntries).map {
                QuotaReportEntry($0.base, account: $0.tag, nowMs: nowMs, ledger: ledger, priceVersion: priceVersion)
            }
    }

    /// The current base of an account and window.
    public func base(account: String, window: String) -> QuotaEntryBase? {
        current.first { $0.account == account && $0.window == window }
    }

    /// The account a source spends on now: Codex's newest line, else the key in force.
    public func account(of source: QuotaEstimateSource) -> String? {
        if let host = source.host {
            return QuotaKeyEntry.inForce(hostTimelines[source] ?? [], at: nowMs)?.key
                ?? QuotaTimelines.ownKey(QuotaEstimateSource(source: source.source, host: host), salt: salt, install: install)
        }
        switch source.source {
        case "codex":
            if let newestCodexAccount { return newestCodexAccount }
            return QuotaKeyEntry.inForce(timelines.local["codex"] ?? [], at: nowMs)?.key
        case "claudeCode", "claudeDesktop":
            return QuotaKeyEntry.inForce(timelines.local[source.source] ?? [], at: nowMs)?.key
        default:
            return QuotaTimelines.ownKey(source, salt: salt, install: install)
        }
    }

    /// Spend of an account's window from `start` to `nowMs`, with the unpriced requests.
    func used(_ base: QuotaEntryBase, from start: Int64, to nowMs: Int64) -> (cost: Int64, unpriced: Int64) {
        ledger.outer([base.account], base.modelName, start, nowMs)
    }
}
