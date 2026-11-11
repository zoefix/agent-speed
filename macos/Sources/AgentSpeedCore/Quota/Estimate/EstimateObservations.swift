import Foundation

/// One vendor value of one window (QUOTAS 10.4): `(account, window, t, tLo, p, R)` with the source that gave it and
/// the plan in force.
public struct QuotaObservation: Hashable, Sendable {
    public var account: String
    public var source: String
    public var kind: QuotaWindowKind
    public var name: String?
    public var minutes: Int
    /// When the vendor computed the value.
    public var t: Int64
    /// The earliest moment the value may describe.
    public var tLo: Int64
    public var p: Double
    /// The reset.
    public var resetsMs: Int64
    public var plan: String?
    public var tier: String?

    public init(account: String, source: String, kind: QuotaWindowKind, name: String?, minutes: Int, t: Int64,
                tLo: Int64, p: Double, resetsMs: Int64, plan: String? = nil, tier: String? = nil) {
        self.account = account
        self.source = source
        self.kind = kind
        self.name = name
        self.minutes = minutes
        self.t = t
        self.tLo = tLo
        self.p = p
        self.resetsMs = resetsMs
        self.plan = plan
        self.tier = tier
    }

    var windowID: String { QuotaEstimateWindow.id(kind: kind, name: name, minutes: minutes) }

    /// Equal values of one account and window at one time count once (fresh and stored).
    var identity: Identity {
        Identity(account: account, kind: kind, name: name, minutes: minutes, t: t, p: p, resetsMs: resetsMs)
    }

    struct Identity: Hashable {
        var account: String
        var kind: QuotaWindowKind
        var name: String?
        var minutes: Int
        var t: Int64
        var p: Double
        var resetsMs: Int64
    }
}

public enum QuotaEstimateWindow {
    /// `kind:name:minutes`, the window part of a cap class.
    public static func id(kind: QuotaWindowKind, name: String?, minutes: Int) -> String {
        "\(kind.rawValue):\(name ?? ""):\(minutes)"
    }

    /// The model filter of a window: only a `modelWeek` window counts its model family alone.
    static func modelName(kind: QuotaWindowKind, name: String?) -> String? {
        kind == .modelWeek ? name : nil
    }

    static func halfLifeMs(minutes: Int?) -> Int64 {
        if let minutes, minutes >= 10_000 { return 7 * QuotaEstimateConstants.dayMs }
        return 2 * QuotaEstimateConstants.dayMs
    }

    /// A value's own resolution: 1 for integers (`23.0` included), 0.1 for one decimal, else 0.01.
    static func resolution(_ p: Double) -> Double {
        for digits in 0...1 {
            let scaled = p * pow(10, Double(digits))
            if abs(scaled - scaled.rounded(.toNearestOrEven)) < 1e-9 { return pow(10, -Double(digits)) }
        }
        return 0.01
    }
}

/// One window instance: observations of an account and window whose resets lie within 2 minutes of the first.
struct QuotaInstance {
    var resetsMs: Int64
    /// `max R − minutes`: the lower-bound sums start here.
    var start: Int64
    /// `min R − minutes` (an hour earlier for a five-hour window on a whole hour): the upper-bound sums start here.
    var startHigh: Int64
    /// By `t`, then `tLo`.
    var observations: [QuotaObservation]

    /// Groups one account-window's observations (QUOTAS 10.4).
    static func group(_ observations: [QuotaObservation]) -> [QuotaInstance] {
        let minute = QuotaEstimateConstants.minuteMs
        let sorted = observations.stableSorted { ($0.resetsMs, $0.t) < ($1.resetsMs, $1.t) }
        var clusters: [(first: Int64, items: [QuotaObservation])] = []
        for o in sorted {
            if let last = clusters.last, o.resetsMs - last.first <= QuotaEstimateConstants.instanceJoinMs {
                clusters[clusters.count - 1].items.append(o)
            } else {
                clusters.append((o.resetsMs, [o]))
            }
        }
        var out: [QuotaInstance] = []
        for cluster in clusters {
            let first = cluster.items[0]
            let rs = cluster.items.map(\.resetsMs)
            let high = rs.max()!, low = rs.min()!
            let length = Int64(first.minutes) * minute
            let slack = first.kind == .fiveHour && low % QuotaEstimateConstants.hourMs == 0
                ? QuotaEstimateConstants.hourRoundedSlackMs : 0
            let startHigh = low - length - slack
            let kept = cluster.items.filter { startHigh - minute <= $0.t && $0.t <= high + minute }
                .stableSorted { ($0.t, $0.tLo) < ($1.t, $1.tLo) }
            guard !kept.isEmpty else { continue }
            out.append(QuotaInstance(resetsMs: high, start: high - length, startHigh: startHigh, observations: kept))
        }
        return out
    }

    /// The finest resolution per source among the instance's observations.
    var resolutions: [String: Double] {
        var q: [String: Double] = [:]
        for o in observations {
            q[o.source] = min(q[o.source] ?? 1.0, QuotaEstimateWindow.resolution(o.p))
        }
        return q
    }
}

extension Array {
    /// Python's `sorted`: stable.
    func stableSorted(by less: (Element, Element) -> Bool) -> [Element] {
        enumerated().sorted { lhs, rhs in
            if less(lhs.element, rhs.element) { return true }
            if less(rhs.element, lhs.element) { return false }
            return lhs.offset < rhs.offset
        }.map(\.element)
    }
}
