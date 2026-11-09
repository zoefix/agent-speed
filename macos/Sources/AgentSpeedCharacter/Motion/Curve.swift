import Foundation

/// One key of a motion curve: a value at a time.
public struct CurveKey: Sendable, Equatable {
    public var t: Double
    public var v: Double

    public init(_ t: Double, _ v: Double) {
        self.t = t
        self.v = v
    }

    /// Keys in the reference's tuple order: by time, then by value.
    static func sorted(_ keys: [CurveKey]) -> [CurveKey] {
        keys.sorted { $0.t != $1.t ? $0.t < $1.t : $0.v < $1.v }
    }
}

/// A keyframed curve through its keys on a monotone cubic (PCHIP, Fritsch-Carlson): holds stay flat, peaks stop
/// smoothly and a run in one direction never pauses at a key. Ends have zero slope; a periodic curve whose ends meet
/// joins smoothly across the wrap.
public struct Curve: Sendable {
    public let t: [Double]
    public let v: [Double]
    let m: [Double]

    public init(_ keys: [CurveKey], periodic: Bool = false) {
        let sorted = CurveKey.sorted(keys)
        t = sorted.map(\.t)
        v = sorted.map(\.v)
        m = Curve.slopes(t, v, periodic: periodic)
    }

    public var start: Double { v[0] }

    static func slopes(_ t: [Double], _ v: [Double], periodic: Bool) -> [Double] {
        let n = t.count
        var m = [Double](repeating: 0, count: n)
        if n < 3 { return m }
        var h = [Double](repeating: 0, count: n - 1)
        var d = [Double](repeating: 0, count: n - 1)
        for i in 0..<(n - 1) {
            h[i] = t[i + 1] - t[i]
            d[i] = h[i] > 1e-9 ? (v[i + 1] - v[i]) / h[i] : 0
        }
        func blend(_ h0: Double, _ h1: Double, _ d0: Double, _ d1: Double) -> Double {
            if d0 * d1 <= 0 { return 0 }
            let w1 = 2 * h1 + h0
            let w2 = h1 + 2 * h0
            return (w1 + w2) / (w1 / d0 + w2 / d1)
        }
        for i in 1..<(n - 1) {
            m[i] = blend(h[i - 1], h[i], d[i - 1], d[i])
        }
        if periodic, abs(v[0] - v[n - 1]) < 1e-9 {
            let s = blend(h[n - 2], h[0], d[n - 2], d[0])
            m[0] = s
            m[n - 1] = s
        }
        return m
    }

    public func callAsFunction(_ x: Double) -> Double {
        let n = t.count
        if x <= t[0] { return v[0] }
        if x >= t[n - 1] { return v[n - 1] }
        var lo = 0
        var hi = n
        while lo < hi {
            let mid = (lo + hi) / 2
            if x < t[mid] { hi = mid } else { lo = mid + 1 }
        }
        let i = lo - 1
        let h = t[i + 1] - t[i]
        if h <= 1e-9 { return v[i + 1] }
        let s = (x - t[i]) / h
        let sq = (1 - s) * (1 - s)
        var y = (1 + 2 * s) * sq * v[i]
        y += s * sq * h * m[i]
        y += s * s * (3 - 2 * s) * v[i + 1]
        y += s * s * (s - 1) * h * m[i + 1]
        return y
    }
}
