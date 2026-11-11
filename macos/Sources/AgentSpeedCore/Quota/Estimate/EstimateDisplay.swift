import Foundation

/// What a quota line shows of the estimate (QUOTAS 10.7, 10.8): the live percent, the amount used this window and
/// the estimated cap with its details. Amounts in micro-USD.
public struct QuotaEstimateDisplay: Equatable, Sendable {
    public var percent: Double
    /// Extrapolated from this computer's spend since the vendor's value.
    public var estimated: Bool
    public var resetsMs: Int64?
    /// This computer's and its hosts' spend in the current instance.
    public var usedMicroUsd: Int64
    public var unpricedRequests: Int64
    public var confidence: QuotaEstimateConfidence
    /// `≈cap`; nil with only a lower bound or none.
    public var capMicroUsd: Int64?
    /// The published range; `highMicroUsd` nil with only `≥low`.
    public var lowMicroUsd: Int64?
    public var highMicroUsd: Int64?
    /// The share of the window's usage this computer saw, 0–1.
    public var coverage: Double?
    public var accountUsedMicroUsd: Int64?
    public var windows: Int
    public var plan: String?
    public var tier: String?
    /// The plan was inferred from the quota (QUOTAS 10.2), shown as such (10.8).
    public var planInferred: Bool

    public init(percent: Double, estimated: Bool, resetsMs: Int64?, usedMicroUsd: Int64, unpricedRequests: Int64 = 0,
                confidence: QuotaEstimateConfidence, capMicroUsd: Int64? = nil, lowMicroUsd: Int64? = nil,
                highMicroUsd: Int64? = nil, coverage: Double? = nil, accountUsedMicroUsd: Int64? = nil, windows: Int = 0,
                plan: String? = nil, tier: String? = nil, planInferred: Bool = false) {
        self.percent = percent
        self.estimated = estimated
        self.resetsMs = resetsMs
        self.usedMicroUsd = usedMicroUsd
        self.unpricedRequests = unpricedRequests
        self.confidence = confidence
        self.capMicroUsd = capMicroUsd
        self.lowMicroUsd = lowMicroUsd
        self.highMicroUsd = highMicroUsd
        self.coverage = coverage
        self.accountUsedMicroUsd = accountUsedMicroUsd
        self.windows = windows
        self.plan = plan
        self.tier = tier
        self.planInferred = planInferred
    }

    /// An estimate exists (a cap or a lower bound).
    public var hasEstimate: Bool { confidence != .none }

    /// The amount cell is empty without spend and without an estimate (DESIGN 4 *Amount*).
    public var showsAmount: Bool { usedMicroUsd > 0 || hasEstimate }
}

extension QuotaEstimateBuild {
    /// The estimate of one source's window at `nowMs`; nil while the account has no value of that window.
    public func display(source: QuotaEstimateSource, window: QuotaWindow, nowMs: Int64) -> QuotaEstimateDisplay? {
        guard let minutes = window.minutes, let account = account(of: source),
              let base = base(account: account, window: QuotaEstimateWindow.id(kind: window.kind, name: window.name,
                                                                                minutes: minutes))
        else { return nil }
        let entry = QuotaReportEntry(base, account: "", nowMs: nowMs, ledger: ledger, priceVersion: priceVersion)
        return QuotaEstimateDisplay(
            percent: entry.livePercent, estimated: entry.liveEstimated, resetsMs: entry.resetsInMs.map { nowMs + $0 },
            usedMicroUsd: entry.usedMicroUsd, unpricedRequests: entry.unpricedRequests,
            confidence: entry.estimate.confidence, capMicroUsd: entry.estimate.capMicroUsd,
            lowMicroUsd: entry.estimate.lowMicroUsd, highMicroUsd: entry.estimate.highMicroUsd,
            coverage: entry.estimate.coverage, accountUsedMicroUsd: entry.estimate.accountUsedMicroUsd,
            windows: entry.estimate.windows, plan: entry.plan, tier: entry.tier, planInferred: base.planInferred)
    }
}
