import Foundation

/// One interval of the cap from an instance's observations (QUOTAS 10.5).
struct QuotaSegment: Equatable {
    enum Kind: String { case cumulative, increment }

    var kind: Kind
    var lo: Double
    var hi: Double
    /// The percent (cumulative) or the percent step (increment).
    var dp: Double
    var t: Int64
}

/// One instance's interval `(L, H)` with its weight inputs.
public struct QuotaInstanceBounds: Equatable, Sendable {
    public var resetsMs: Int64
    public var lo: Double
    /// Infinite when unbounded.
    public var hi: Double
    public var pMax: Double
    public var tLast: Int64
    /// The instance used its increments alone (its start went uncounted).
    public var offset: Bool
}

enum QuotaSegments {
    /// A run: consecutive observations with equal values.
    private struct Run {
        var p: Double
        var first: QuotaObservation
        var last: QuotaObservation
        var tLoMax: Int64
        var q: Double
    }

    private static func runs(_ observations: [QuotaObservation], _ q: [String: Double]) -> [Run] {
        var out: [Run] = []
        for o in observations {
            let resolution = q[o.source] ?? 1
            if let last = out.last, abs(last.p - o.p) < 1e-9 {
                out[out.count - 1].last = o
                out[out.count - 1].tLoMax = max(last.tLoMax, o.tLo)
                out[out.count - 1].q = max(last.q, resolution)
            } else {
                out.append(Run(p: o.p, first: o, last: o, tLoMax: o.tLo, q: resolution))
            }
        }
        return out
    }

    /// The cumulative and increment segments of one instance. Spend after `nowMs` does not exist yet.
    static func segments(_ instance: QuotaInstance, ledger: QuotaEstimateLedger, accounts: Set<String>, name: String?,
                         lag: Int64 = QuotaEstimateConstants.lagMs, nowMs: Int64? = nil) -> [QuotaSegment] {
        let s = instance.start, sHigh = instance.startHigh
        func upTo(_ t: Int64) -> Int64 { nowMs.map { min(t, $0) } ?? t }
        let rs = runs(instance.observations, instance.resolutions)
        var segments: [QuotaSegment] = []
        for r in rs where r.p > 0 {
            let cLo = ledger.inner(accounts, name, s, r.tLoMax - lag).cost
            let cHi = ledger.outerHigh(accounts, name, sHigh, upTo(r.first.t + lag))
            let lo = r.p >= 100 ? 0 : 100 * Double(cLo) / (r.p + r.q)
            let top = min(r.p, 100)
            let hi = top - r.q <= 0 ? Double.infinity : 100 * cHi / (top - r.q)
            segments.append(QuotaSegment(kind: .cumulative, lo: lo, hi: hi, dp: r.p, t: r.last.t))
        }
        var anchors: [Run] = []
        for r in rs where r.p > 0 {
            if let last = anchors.last {
                if r.p >= last.p + max(3 * max(r.q, last.q), QuotaEstimateConstants.incrementStep) - 1e-9 {
                    anchors.append(r)
                }
            } else {
                anchors.append(r)
            }
        }
        var pairs: [(a: Run, b: Run)] = []
        for (i, a) in anchors.enumerated() {
            for b in anchors[(i + 1)...] { pairs.append((a, b)) }
        }
        let widest = pairs.stableSorted { (-($0.b.p - $0.a.p), $0.a.p) < (-($1.b.p - $1.a.p), $1.a.p) }
            .prefix(QuotaEstimateConstants.segmentsPerInstance / 2)
        for (a, b) in Array(widest).stableSorted(by: { ($0.a.p, $0.b.p) < ($1.a.p, $1.b.p) }) {
            let q2 = a.q + b.q
            let dp = b.p - a.p
            let cLo = max(0, ledger.inner(accounts, name, s, b.tLoMax - lag).cost
                - ledger.outer(accounts, name, s, upTo(a.first.t + lag)).cost)
            let hiEnd = ledger.outerHigh(accounts, name, sHigh, upTo(b.first.t + lag))
            let hiStart = ledger.inner(accounts, name, sHigh, a.tLoMax - lag).cost
            let lo = b.p >= 100 ? 0 : 100 * Double(cLo) / (dp + q2)
            let top = min(b.p, 100) - a.p
            let hi = top - q2 <= 0 ? Double.infinity : 100 * max(0, hiEnd - Double(hiStart)) / (top - q2)
            segments.append(QuotaSegment(kind: .increment, lo: lo, hi: hi, dp: dp, t: b.last.t))
        }
        segments = segments.filter { $0.lo > 0 || ($0.hi.isFinite && $0.hi > 0) }
        let limit = QuotaEstimateConstants.segmentsPerInstance
        if segments.count > limit {
            let cumulative = segments.filter { $0.kind == .cumulative }
            let increments = segments.filter { $0.kind == .increment }
            let room = limit - increments.count
            let stride = Double(cumulative.count) / Double(room)
            var kept: [QuotaSegment] = []
            for i in 0..<max(0, room) {
                let index = min(cumulative.count - 1, Int(Double(cumulative.count - 1) - Double(i) * stride))
                kept.append(cumulative[index])
            }
            segments = kept.reversed() + increments
        }
        return segments
    }

    /// One interval per instance: the largest lower bound and the smallest upper bound not below it; increments alone
    /// when a cumulative lower bound lies above an increment's upper bound.
    static func bounds(_ segments: [QuotaSegment], resetsMs: Int64) -> QuotaInstanceBounds? {
        func interval(_ group: [QuotaSegment]) -> (Double, Double)? {
            let los = group.map(\.lo).filter { $0 > 0 }
            guard let low = los.max() else { return nil }
            let his = group.map(\.hi).filter { $0.isFinite && $0 >= low }
            return (low, his.min() ?? .infinity)
        }
        let increments = segments.filter { $0.kind == .increment }
        let cumulativeLo = segments.filter { $0.kind == .cumulative }.map(\.lo).max() ?? 0
        let incrementHi = increments.map(\.hi).min() ?? .infinity
        let offset = cumulativeLo > incrementHi
        let chosen = offset && interval(increments) != nil ? increments : segments
        guard let (lo, hi) = interval(chosen) else { return nil }
        return QuotaInstanceBounds(resetsMs: resetsMs, lo: lo, hi: hi, pMax: segments.map(\.dp).max() ?? 0,
                                   tLast: segments.map(\.t).max() ?? 0, offset: offset)
    }
}
