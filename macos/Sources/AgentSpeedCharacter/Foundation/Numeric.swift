import Foundation

/// Arithmetic that matches the Python reference player bit for bit (spec/CHARACTERS.md, Conformance).
public enum CharacterMath {
    /// Python's float `%`: the result takes the sign of the divisor.
    @inlinable public static func mod(_ a: Double, _ b: Double) -> Double {
        var r = fmod(a, b)
        if r != 0, (b < 0) != (r < 0) { r += b }
        return r
    }

    /// Python's `round()`: halves go to the even neighbour.
    @inlinable public static func roundHalfEven(_ x: Double) -> Double {
        x.rounded(.toNearestOrEven)
    }

    @inlinable public static func clamp(_ x: Double, _ lower: Double, _ upper: Double) -> Double {
        min(upper, max(lower, x))
    }

    /// numpy.interp over keys sorted by x: linear between keys, flat outside.
    @inlinable public static func interpolate(_ xs: [Double], _ ys: [Double], _ x: Double) -> Double {
        let n = xs.count
        if n == 0 { return 0 }
        if x <= xs[0] { return ys[0] }
        if x >= xs[n - 1] { return ys[n - 1] }
        var lo = 0
        var hi = n - 1
        while hi - lo > 1 {
            let mid = (lo + hi) / 2
            if xs[mid] <= x { lo = mid } else { hi = mid }
        }
        let slope = (ys[lo + 1] - ys[lo]) / (xs[lo + 1] - xs[lo])
        return slope * (x - xs[lo]) + ys[lo]
    }
}
