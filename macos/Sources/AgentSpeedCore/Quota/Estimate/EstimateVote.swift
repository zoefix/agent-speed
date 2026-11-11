import Foundation

/// How far an estimate can be trusted (QUOTAS 10.6).
public enum QuotaEstimateConfidence: String, Sendable, Equatable {
    case high, medium, low, none
}

/// The estimate of one cap class (QUOTAS 10.6).
public struct QuotaCapEstimate: Equatable, Sendable {
    public var confidence: QuotaEstimateConfidence
    /// `K̂`, nil with only a lower bound or none.
    public var cap: Double?
    /// The published range; `high` is nil with only `atLeast = low`.
    public var low: Double?
    public var high: Double?
    /// The number of window instances it rests on.
    public var windows: Int

    public static let none = QuotaCapEstimate(confidence: .none, cap: nil, low: nil, high: nil, windows: 0)

    public init(confidence: QuotaEstimateConfidence, cap: Double?, low: Double?, high: Double?, windows: Int) {
        self.confidence = confidence
        self.cap = cap
        self.low = low
        self.high = high
        self.windows = windows
    }
}

/// One instance's interval in a vote, with its weight and an id telling instances apart.
struct QuotaVoteItem {
    var lo: Double
    var hi: Double
    var weight: Double
    var id: Int64
}

struct QuotaVoteResult: Equatable {
    var low: Double
    var high: Double
    /// The weight of the instances holding the region.
    var inside: Double
    /// Holders that are themselves at most 8 % wide.
    var narrow: Int
    var loss: Double
}

enum QuotaVote {
    static func weight(pMax: Double, tLast: Int64, nowMs: Int64, halfLifeMs: Int64) -> Double {
        let age = Double(max(0, nowMs - tLast))
        return min(1, pMax / QuotaEstimateConstants.fullStep) * pow(0.5, age / Double(halfLifeMs))
    }

    static func round9(_ x: Double) -> Double {
        (x * 1e9).rounded(.toNearestOrEven) / 1e9
    }

    /// `loss(K) = Σ w · ([K < L] + 0.25 · [K > H])` on every endpoint and open cell; the touching cells of least loss
    /// form regions, and the region with the most weight inside wins, then the lowest.
    static func vote(_ weighted: [QuotaVoteItem]) -> QuotaVoteResult? {
        let items = weighted.filter {
            ($0.lo > 0 || ($0.hi.isFinite && $0.hi > 0)) && $0.weight >= QuotaEstimateConstants.minWeight
        }
        guard let floor = items.map(\.lo).filter({ $0 > 0 }).min() else { return nil }
        let points = Set(items.flatMap { [$0.lo, $0.hi] }.filter { $0.isFinite && $0 >= floor }).sorted()
        var cells: [(Double, Double)] = []
        for (i, x) in points.enumerated() {
            cells.append((x, x))
            cells.append((x, i + 1 < points.count ? points[i + 1] : .infinity))
        }
        let los = items.map { ($0.lo, $0.weight) }.sorted { $0 < $1 }
        let his = items.filter(\.hi.isFinite).map { ($0.hi, $0.weight) }.sorted { $0 < $1 }
        let loKeys = los.map(\.0), hiKeys = his.map(\.0)
        var loSuffix = [Double](repeating: 0, count: los.count + 1)
        for i in stride(from: los.count - 1, through: 0, by: -1) { loSuffix[i] = loSuffix[i + 1] + los[i].1 }
        var hiPrefix: [Double] = [0]
        for (_, w) in his { hiPrefix.append(hiPrefix.last! + w) }
        var scored: [(Double, Double, Double)] = []
        for (a, b) in cells {
            let probe = a == b ? a : (b.isFinite ? (a * b).squareRoot() : a * 2)
            let loss = loSuffix[upperBound(loKeys, probe)]
                + QuotaEstimateConstants.hiPenalty * hiPrefix[lowerBound(hiKeys, probe)]
            scored.append((a, b, round9(loss)))
        }
        let least = scored.map(\.2).min()!
        var regions: [(Double, Double)] = []
        for (a, b, loss) in scored where loss == least {
            if let last = regions.last, last.1 >= a {
                regions[regions.count - 1].1 = max(last.1, b)
            } else {
                regions.append((a, b))
            }
        }
        func holders(_ region: (Double, Double)) -> [QuotaVoteItem] {
            items.filter { $0.lo <= region.0 * (1 + 1e-12) && $0.hi >= region.1 * (1 - 1e-12) }
        }
        let ranked = regions.stableSorted { lhs, rhs in
            (-round9(holders(lhs).reduce(0) { $0 + $1.weight }), lhs.0)
                < (-round9(holders(rhs).reduce(0) { $0 + $1.weight }), rhs.0)
        }
        let region = ranked[0]
        let held = holders(region)
        let narrow = Set(held.filter { $0.lo > 0 && $0.hi.isFinite && $0.hi / $0.lo - 1 <= QuotaEstimateConstants.highWidth }
            .map(\.id))
        return QuotaVoteResult(low: region.0, high: region.1, inside: held.reduce(0) { $0 + $1.weight },
                               narrow: narrow.count, loss: least)
    }

    /// `above`: the weight of lower bounds above the region (a region below a lower bound is never high).
    static func confidence(low: Double, high: Double, inside: Double, narrow: Int, above: Double) -> QuotaEstimateConfidence {
        guard high.isFinite else { return .low }
        let width = high / low - 1
        if width <= QuotaEstimateConstants.highWidth && inside >= 1.5 && narrow >= 2 && above == 0 { return .high }
        if width <= 0.25 && inside >= 0.5 { return .medium }
        return .low
    }

    static func isNarrow(_ b: QuotaInstanceBounds) -> Bool {
        b.lo > 0 && b.hi.isFinite && b.hi / b.lo - 1 <= QuotaEstimateConstants.highWidth
    }

    /// A lowered cap: when the two newest narrow instances agree, older instances whose lower bound lies above both
    /// are dropped.
    static func afterCut(_ items: [QuotaInstanceBounds]) -> [QuotaInstanceBounds] {
        let narrow = items.filter(isNarrow).stableSorted { $0.tLast < $1.tLast }
        guard narrow.count >= 2 else { return items }
        let a = narrow[narrow.count - 2], b = narrow[narrow.count - 1]
        guard max(a.lo, b.lo) <= min(a.hi, b.hi) else { return items }
        let top = max(a.hi, b.hi)
        return items.filter { !($0.tLast < a.tLast && $0.lo > top) }
    }

    /// The estimate of one cap class from its instances' intervals.
    static func estimate(_ items: [QuotaInstanceBounds], nowMs: Int64, halfLifeMs: Int64) -> QuotaCapEstimate {
        let kept = afterCut(items)
        let weighted = kept.map {
            QuotaVoteItem(lo: $0.lo, hi: $0.hi, weight: weight(pMax: $0.pMax, tLast: $0.tLast, nowMs: nowMs,
                                                                   halfLifeMs: halfLifeMs), id: $0.resetsMs)
        }
        let used = weighted.filter { $0.weight >= QuotaEstimateConstants.minWeight }
        guard let result = vote(weighted) else { return .none }
        let above = used.filter { $0.lo > result.low * (1 + 1e-12) }.reduce(0) { $0 + $1.weight }
        let cap = result.high.isFinite ? (result.low * result.high).squareRoot() : nil
        var low = result.low
        var high: Double? = result.high.isFinite ? result.high : nil
        if let cap, let top = high {
            low = min(low, cap / (1 + QuotaEstimateConstants.precisionFloor))
            high = max(top, cap * (1 + QuotaEstimateConstants.precisionFloor))
        }
        return QuotaCapEstimate(
            confidence: confidence(low: result.low, high: result.high, inside: result.inside, narrow: result.narrow,
                                   above: above),
            cap: cap, low: low, high: high, windows: Set(used.map(\.id)).count)
    }

    /// The newest instances of a class.
    static func pruned(_ items: [QuotaInstanceBounds]) -> [QuotaInstanceBounds] {
        Array(items.stableSorted { $0.tLast < $1.tLast }.suffix(QuotaEstimateConstants.instancesPerClass))
    }

    private static func upperBound(_ values: [Double], _ x: Double) -> Int {
        var low = 0, high = values.count
        while low < high {
            let mid = (low + high) / 2
            if values[mid] <= x { low = mid + 1 } else { high = mid }
        }
        return low
    }

    private static func lowerBound(_ values: [Double], _ x: Double) -> Int {
        var low = 0, high = values.count
        while low < high {
            let mid = (low + high) / 2
            if values[mid] < x { low = mid + 1 } else { high = mid }
        }
        return low
    }
}
