import Foundation

/// How a shown 性能 changes (DESIGN 12, *Motion*): a change of at least 1 % and at least 3, up or down, counts through
/// the intermediate integers with easeOutCubic over `clamp(0.3 + 0.35 × |log2(new / old)|, 0.3, 0.7)` s; smaller
/// changes replace the digits; `–` ↔ a number crossfades in 0.15 s; Reduce Motion crossfades every change in 0.15 s.
/// Live ↔ resting crossfades the color (0.2 s, kept with Reduce Motion) and the provisional mark fades (0.25 s).
public enum PerfMotion {
    public static let crossfade: TimeInterval = 0.15
    public static let form: TimeInterval = 0.2
    public static let provisional: TimeInterval = 0.25
    public static let minimumDelta = 3
    public static let minimumShare = 0.01

    public enum Change: Equatable, Sendable {
        case none
        /// Counts through every intermediate integer.
        case count(seconds: TimeInterval)
        case replace
        case crossfade(seconds: TimeInterval)
    }

    /// `counts`: the surface counts (the orb readout, the board hero, the dashboard row, the card); board rows and the
    /// menu bar replace every change in place.
    public static func change(from old: Int?, to new: Int?, reduceMotion: Bool, counts: Bool = true) -> Change {
        guard old != new else { return .none }
        if reduceMotion { return .crossfade(seconds: crossfade) }
        guard let old, let new else { return .crossfade(seconds: crossfade) }
        guard counts, countsChange(from: old, to: new) else { return .replace }
        return .count(seconds: countSeconds(from: old, to: new))
    }

    /// At least 1 % and at least 3, in either direction.
    public static func countsChange(from old: Int, to new: Int) -> Bool {
        let delta = abs(new - old)
        return delta >= minimumDelta && Double(delta) >= minimumShare * Double(abs(old))
    }

    /// +10 % lands in 0.35 s, a doubling in 0.65 s.
    public static func countSeconds(from old: Int, to new: Int) -> TimeInterval {
        guard old > 0, new > 0 else { return 0.7 }
        return min(max(0.3 + 0.35 * abs(Foundation.log2(Double(new) / Double(old))), 0.3), 0.7)
    }

    public static func easeOutCubic(_ progress: Double) -> Double {
        let t = min(max(progress, 0), 1)
        return 1 - pow(1 - t, 3)
    }

    /// The integer a count from `old` to `new` shows `elapsed` seconds in.
    public static func countValue(from old: Int, to new: Int, elapsed: TimeInterval) -> Int {
        let seconds = countSeconds(from: old, to: new)
        let value = Double(old) + Double(new - old) * easeOutCubic(seconds > 0 ? elapsed / seconds : 1)
        return value >= 0 ? Int(value + 0.5) : -Int(-value + 0.5)
    }
}
