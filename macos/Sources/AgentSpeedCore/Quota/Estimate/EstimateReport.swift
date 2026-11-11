import Foundation

/// One `quotas` entry of a report (REPORTING-EXTENSIONS 11.2): one account's window, the account named only by its tag
/// for the service.
public struct QuotaReportEntry: Equatable, Sendable {
    public struct Mix: Equatable, Sendable {
        public var model: String
        public var share: Double
    }

    public struct Estimate: Equatable, Sendable {
        public var capMicroUsd: Int64?
        public var lowMicroUsd: Int64?
        public var highMicroUsd: Int64?
        public var confidence: QuotaEstimateConfidence
        public var coverage: Double?
        public var accountUsedMicroUsd: Int64?
        public var windows: Int
    }

    /// The account's tag for the service (16 hex digits): never its id or key.
    public var account: String
    public var source: String
    public var sources: [String]
    public var plan: String?
    public var tier: String?
    public var kind: QuotaWindowKind
    public var name: String?
    public var minutes: Int
    /// The vendor's value at two decimals; nil once the reset passed.
    public var usedPercent: Double?
    public var percentAgeMs: Int64?
    public var resetsInMs: Int64?
    public var livePercent: Double
    public var liveEstimated: Bool
    public var usedMicroUsd: Int64
    public var observedUsedMicroUsd: Int64?
    public var unpricedRequests: Int64
    public var estimate: Estimate
    public var mix: [Mix]
    public var priceVersion: Int

    /// `account`: the tag sent for the account (`QuotaAccounts.reportTag`), empty where nothing is sent.
    init(_ base: QuotaEntryBase, account: String, nowMs: Int64, ledger: QuotaEstimateLedger, priceVersion: Int) {
        let accounts: Set<String> = [base.account]
        let filter = base.modelName
        let cap = base.estimate.cap
        let live = QuotaLivePercent.compute(p: base.p, t: base.t, resetsMs: base.resetsMs, start: base.start,
                                            minutes: base.minutes, kind: base.kind, cap: cap, ledger: ledger,
                                            accounts: accounts, name: filter, nowMs: nowMs,
                                            firstAfterReset: base.firstAfterReset)
        let passed = nowMs >= base.resetsMs
        let length = Int64(base.minutes) * QuotaEstimateConstants.minuteMs
        let start = !passed ? base.start : (live.resetsMs.map { $0 - length } ?? nowMs)
        let used = ledger.outer(accounts, filter, start, nowMs)
        let observed = ledger.outer(accounts, filter, base.start, base.t).cost
        var coverage: Double?
        if let cap, cap > 0, base.p >= 5 {
            coverage = QuotaEstimateRounding.halfAway(min(1, max(0, Double(observed) / (cap * base.p / 100))), digits: 3)
        }
        self.account = account
        source = base.source
        sources = base.sources
        plan = base.plan
        tier = base.tier
        kind = base.kind
        name = base.name
        minutes = base.minutes
        usedPercent = passed ? nil : QuotaEstimateRounding.halfAway(base.p, digits: 2)
        percentAgeMs = passed ? nil : max(0, nowMs - base.t)
        resetsInMs = live.resetsMs.map { max(0, $0 - nowMs) }
        livePercent = QuotaEstimateRounding.halfAway(live.percent, digits: 1)
        liveEstimated = live.estimated
        usedMicroUsd = used.cost
        observedUsedMicroUsd = passed ? nil : observed
        unpricedRequests = used.unpriced
        estimate = Estimate(
            capMicroUsd: QuotaEstimateRounding.micro(cap), lowMicroUsd: QuotaEstimateRounding.micro(base.estimate.low),
            highMicroUsd: QuotaEstimateRounding.micro(base.estimate.high), confidence: base.estimate.confidence,
            coverage: coverage, accountUsedMicroUsd: cap.flatMap { QuotaEstimateRounding.micro($0 * live.percent / 100) },
            windows: base.estimate.windows)
        mix = ledger.mix(accounts, filter, start, nowMs).map { Mix(model: $0.model, share: $0.share) }
        self.priceVersion = priceVersion
    }

    /// The fields the send cadence compares, for entries that only carry those.
    public init(account: String, sources: [String], kind: QuotaWindowKind, name: String?, minutes: Int,
                usedPercent: Double?, resetsInMs: Int64?, usedMicroUsd: Int64, confidence: QuotaEstimateConfidence) {
        self.account = account
        source = sources.first ?? ""
        self.sources = sources
        self.kind = kind
        self.name = name
        self.minutes = minutes
        self.usedPercent = usedPercent
        percentAgeMs = usedPercent == nil ? nil : 0
        self.resetsInMs = resetsInMs
        livePercent = usedPercent ?? 0
        liveEstimated = false
        self.usedMicroUsd = usedMicroUsd
        unpricedRequests = 0
        estimate = Estimate(confidence: confidence, windows: 0)
        mix = []
        priceVersion = 1
    }

    /// The entry as sent, members in the schema's order.
    public var json: ReportJSON {
        .object([
            "account": .string(account),
            "source": .string(source),
            "sources": .array(sources.map(ReportJSON.string)),
            "plan": plan.map(ReportJSON.string) ?? .null,
            "tier": tier.map(ReportJSON.string) ?? .null,
            "window": .object(["kind": .string(kind.rawValue), "name": name.map(ReportJSON.string) ?? .null,
                               "minutes": .int(minutes)]),
            "usedPercent": usedPercent.map { ReportJSON.decimal($0, decimals: 2) } ?? .null,
            "percentAgeMs": .optionalInt(percentAgeMs),
            "resetsInMs": .optionalInt(resetsInMs),
            "livePercent": .decimal(livePercent, decimals: 1),
            "liveEstimated": .bool(liveEstimated),
            "usedMicroUsd": .int(usedMicroUsd),
            "observedUsedMicroUsd": .optionalInt(observedUsedMicroUsd),
            "unpricedRequests": .int(unpricedRequests),
            "estimate": .object([
                "capMicroUsd": .optionalInt(estimate.capMicroUsd),
                "lowMicroUsd": .optionalInt(estimate.lowMicroUsd),
                "highMicroUsd": .optionalInt(estimate.highMicroUsd),
                "confidence": .string(estimate.confidence.rawValue),
                "coverage": estimate.coverage.map { ReportJSON.decimal($0, decimals: 3) } ?? .null,
                "accountUsedMicroUsd": .optionalInt(estimate.accountUsedMicroUsd),
                "windows": .int(estimate.windows),
            ]),
            "mix": .array(mix.map { .object(["model": .string($0.model), "share": .decimal($0.share, decimals: 3)]) }),
            "priceVersion": .int(priceVersion),
        ])
    }

    /// `(account, window)`: one entry per account and window in a report.
    struct Identity: Hashable {
        var account: String
        var kind: QuotaWindowKind
        var name: String?
        var minutes: Int
    }

    var identity: Identity { Identity(account: account, kind: kind, name: name, minutes: minutes) }
}

extension QuotaReportEntry.Estimate {
    init(confidence: QuotaEstimateConfidence, windows: Int) {
        self.init(capMicroUsd: nil, lowMicroUsd: nil, highMicroUsd: nil, confidence: confidence, coverage: nil,
                  accountUsedMicroUsd: nil, windows: windows)
    }
}

/// The send cadence of REPORTING-EXTENSIONS 11.3 over one run's reports.
public struct QuotaReportCadence: Sendable {
    private var last: [QuotaReportEntry]?
    private var lastMs: Int64?
    private var cleared = true

    public init() {}

    /// Whether this report carries `quotas`: the run's first, a changed set, vendor percent, confidence or reset
    /// state, 60 s since the last one sent, or leaving.
    public static func due(_ entries: [QuotaReportEntry], last: [QuotaReportEntry]?, lastMs: Int64, nowMs: Int64,
                           leaving: Bool = false) -> Bool {
        guard let last, !leaving else { return true }
        if nowMs - lastMs >= QuotaEstimateConstants.sendEveryMs { return true }
        let before = Dictionary(last.map { ($0.identity, $0) }, uniquingKeysWith: { _, b in b })
        let now = Dictionary(entries.map { ($0.identity, $0) }, uniquingKeysWith: { _, b in b })
        if Set(before.keys) != Set(now.keys) { return true }
        for (key, entry) in now {
            let previous = before[key]!
            if entry.usedPercent != previous.usedPercent || entry.estimate.confidence != previous.estimate.confidence {
                return true
            }
            if (entry.resetsInMs == nil) != (previous.resetsInMs == nil) { return true }
        }
        return false
    }

    /// This report's `quotas`: nil leaves the member out. With the section off, `[]` once. `fits` false: the report
    /// had no room for it, so it counts as unsent.
    public mutating func step(nowMs: Int64, entries: [QuotaReportEntry], on: Bool = true, leaving: Bool = false,
                              fits: Bool = true) -> [QuotaReportEntry]? {
        let carried = pending(nowMs: nowMs, entries: entries, on: on, leaving: leaving)
        guard fits else { return nil }
        commit(carried, nowMs: nowMs, on: on)
        return carried
    }

    /// What a report at `nowMs` should carry, before the size rule.
    public func pending(nowMs: Int64, entries: [QuotaReportEntry], on: Bool, leaving: Bool = false) -> [QuotaReportEntry]? {
        guard on else { return cleared ? nil : [] }
        return Self.due(entries, last: last, lastMs: lastMs ?? 0, nowMs: nowMs, leaving: leaving) ? entries : nil
    }

    /// Records what the finished report carried: nil when it carried nothing (or the size rule dropped it); `[]`
    /// with the section off clears, so the next report sent with it on starts over.
    public mutating func commit(_ carried: [QuotaReportEntry]?, nowMs: Int64, on: Bool) {
        guard let carried else { return }
        guard on else {
            cleared = true
            last = nil
            lastMs = nil
            return
        }
        last = carried
        lastMs = nowMs
        cleared = false
    }
}
