import Foundation

/// What the 性能 readout of a set shows (METRICS 6.9): the highest performance score of its agents (*Sets and groups*)
/// and the live thinking sums (*Live thinking*), per second of the live window. The thinking sums are additive over sources
/// and agents; the score combines by the highest-score rule, so an orb or a group adds the values of its own sources or
/// members (6.8).
public struct IntelligenceSums: Equatable, Sendable {
    /// Output tok/s in the window (`Σ w` ÷ window seconds).
    public var output = 0.0
    /// Reasoning tok/s (`Σ ρ`): `liveThinking`.
    public var thinking = 0.0
    /// Output tok/s of the sources that record reasoning apart from output (`Σ w'`).
    public var thinkingBase = 0.0
    /// The highest performance score of the set (live, else resting, else none), naming the agent that supplied it; an
    /// agent's own sums carry its own value.
    public var score = PerfSetValue.empty

    public init() {}

    public static let zero = IntelligenceSums()

    public var thinkingShare: Double { thinkingBase > 0 ? thinking / thinkingBase : 0 }

    /// The score as shown: an integer rounded half away from zero; nil for none. Live and resting alike (`score.form`
    /// tells them apart).
    public var shownIndex: Int? { score.shown }

    /// One contribution: `output` tokens of `source`, `reasoning` of them reasoning.
    mutating func add(output: Double, reasoning: Double, from source: SourceKind) {
        self.output += output
        guard source.recordsReasoningApart else { return }
        thinking += reasoning
        thinkingBase += output
    }

    public mutating func add(_ other: IntelligenceSums) {
        output += other.output
        thinking += other.thinking
        thinkingBase += other.thinkingBase
        score = score.merged(with: other.score)
    }

    func scaled(by factor: Double) -> IntelligenceSums {
        var scaled = self
        scaled.output *= factor
        scaled.thinking *= factor
        scaled.thinkingBase *= factor
        return scaled
    }
}

extension SourceKind {
    /// DeepSeek Harness counts reasoning inside its output (METRICS 4.3): its tokens add their weight but nothing to the
    /// thinking share.
    public var recordsReasoningApart: Bool { self != .deepseek }
}
