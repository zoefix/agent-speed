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

    public var window: String { QuotaEstimateWindow.id(kind: kind, name: name, minutes: minutes) }
    var modelName: String? { QuotaEstimateWindow.modelName(kind: kind, name: name) }
}

extension QuotaEstimateBuild {
    static let sourceOrder: [String] = SourceKind.allCases.map(\.rawValue)

    static func sourceRank(_ source: String) -> Int {
        sourceOrder.firstIndex(of: source) ?? sourceOrder.count
    }

    /// One base per account and window, its newest instance, in report order (source order, then QUOTAS 2).
    static func current(_ instances: [QuotaBuiltInstance], estimates: [QuotaCapClass: QuotaCapEstimate],
                        spend: [QuotaEstimateSpend], accounts: [String?], observations: [QuotaObservation],
                        nowMs: Int64) -> [QuotaEntryBase] {
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
        var rows: [(rank: (Int, Int, Int, Int, Int), source: String, base: QuotaEntryBase)] = []
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
            let base = QuotaEntryBase(
                account: key.account, source: last.source, sources: sources, plan: x.plan, tier: x.tier, kind: last.kind,
                name: last.name, minutes: last.minutes, p: last.p, t: last.t, resetsMs: instance.resetsMs,
                start: instance.start, estimate: estimates[x.capClass] ?? .none, firstAfterReset: firstAfter)
            let windowRank: (Int, Int, Int, Int)
            if let name = last.name, !name.isEmpty {
                windowRank = (1, names[key.account]?.firstIndex(of: name) ?? 0, last.minutes, last.kind.rank)
            } else {
                windowRank = (0, 0, last.minutes, last.kind.rank)
            }
            let rank = (sourceRank(sources[0]), windowRank.0, windowRank.1, windowRank.2, windowRank.3)
            rows.append((rank, last.source, base))
        }
        return rows.stableSorted { $0.rank < $1.rank || ($0.rank == $1.rank && $0.source < $1.source) }.map(\.base)
    }

    /// The report entries at `nowMs` (REPORTING-EXTENSIONS 11.2), at most 16, of the sources `included` lets through.
    public func entries(nowMs: Int64, including included: (String) -> Bool = { _ in true }) -> [QuotaReportEntry] {
        Array(current.filter { included($0.source) }.prefix(QuotaEstimateConstants.maxEntries).map {
            QuotaReportEntry($0, nowMs: nowMs, ledger: ledger, priceVersion: priceVersion)
        })
    }

    /// The current base of an account and window.
    public func base(account: String, window: String) -> QuotaEntryBase? {
        current.first { $0.account == account && $0.window == window }
    }

    /// The account a source spends on now: Codex's newest line, else the key in force.
    public func account(of source: QuotaEstimateSource) -> String? {
        if let host = source.host {
            return QuotaKeyEntry.inForce(hostTimelines[source] ?? [], at: nowMs)?.key
                ?? QuotaTimelines.ownKey(QuotaEstimateSource(source: source.source, host: host), salt: salt)
        }
        switch source.source {
        case "codex":
            if let newestCodexAccount { return newestCodexAccount }
            return QuotaKeyEntry.inForce(timelines.local["codex"] ?? [], at: nowMs)?.key
        case "claudeCode", "claudeDesktop":
            return QuotaKeyEntry.inForce(timelines.local[source.source] ?? [], at: nowMs)?.key
        default:
            return QuotaTimelines.ownKey(source, salt: salt)
        }
    }

    /// Spend of an account's window from `start` to `nowMs`, with the unpriced requests.
    func used(_ base: QuotaEntryBase, from start: Int64, to nowMs: Int64) -> (cost: Int64, unpriced: Int64) {
        ledger.outer([base.account], base.modelName, start, nowMs)
    }
}
