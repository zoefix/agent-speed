import Foundation

/// The live percent of QUOTAS 10.7: the vendor's value plus the spend measured since, over the cap.
public struct QuotaLivePercent: Equatable, Sendable {
    public var percent: Double
    /// Exceeds the vendor value by 0.5 points or more, or counts a new instance after the reset.
    public var estimated: Bool
    /// The reset of the current instance; nil while unknown.
    public var resetsMs: Int64?

    /// `p`, `t`, `R` and `S` of the newest vendor value; `firstAfterReset` the start of the first local response that
    /// started at or after `R` and has ended (five-hour windows).
    static func compute(p: Double, t: Int64, resetsMs: Int64, start: Int64, minutes: Int, kind: QuotaWindowKind,
                        cap: Double?, ledger: QuotaEstimateLedger, accounts: Set<String>, name: String?, nowMs: Int64,
                        firstAfterReset: Int64?) -> QuotaLivePercent {
        let length = Int64(minutes) * QuotaEstimateConstants.minuteMs
        if nowMs < resetsMs {
            guard let cap else { return QuotaLivePercent(percent: p, estimated: false, resetsMs: resetsMs) }
            let since = ledger.outer(accounts, name, start, nowMs).cost - ledger.outer(accounts, name, start, t).cost
            let live = min(max(p + 100 * Double(since) / cap, p), max(100, p))
            return QuotaLivePercent(percent: live, estimated: live - p >= 0.5, resetsMs: resetsMs)
        }
        let restart: Int64
        if kind == .fiveHour {
            guard let firstAfterReset else { return QuotaLivePercent(percent: 0, estimated: false, resetsMs: nil) }
            restart = QuotaEstimateLedger.minute(of: firstAfterReset)
        } else {
            let periods = QuotaEstimateLedger.floorDiv(nowMs - resetsMs, length)
            restart = resetsMs + periods * length
        }
        let next = restart + length
        guard let cap else { return QuotaLivePercent(percent: 0, estimated: false, resetsMs: next) }
        let live = min(100, 100 * Double(ledger.outer(accounts, name, restart, nowMs).cost) / cap)
        return QuotaLivePercent(percent: live, estimated: true, resetsMs: next)
    }
}
