import Foundation

/// One agent's, set's or group's 性能 (METRICS 6.9 *Values*), in exactly one form: live (the agent works), resting (idle,
/// shown pale) or none.
public enum AgentIntelligence: Equatable, Sendable {
    case live(Double)
    case resting(Double)
    case none

    public init(_ value: PerfSetValue) {
        switch (value.form, value.score) {
        case (.live, let score?): self = .live(score)
        case (.resting, let score?): self = .resting(score)
        default: self = .none
        }
    }

    /// The unrounded score.
    public var value: Double? {
        switch self {
        case .live(let score), .resting(let score): score
        case .none: nil
        }
    }

    /// As shown: an integer rounded half away from zero.
    public var shown: Int? { value.map(perfRoundHalfAway) }

    public var isLive: Bool {
        if case .live = self { return true }
        return false
    }
}
