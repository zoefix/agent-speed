import AgentSpeedCore
import Foundation

/// What an orb's two-line readout is set to show (DESIGN 10): network speed, with the unit cell `Mb/s` wide for bits,
/// or 性能 and 思考. Known while the readout is quiet, so its width is.
public enum OrbLineFormat: Equatable, Hashable, Sendable {
    case traffic(bits: Bool)
    case intelligence
}

/// The values of the two lines (DESIGN 10, Floating orb readout and Intelligence readout), as text for fixed cells.
public enum OrbLineContent: Equatable, Sendable {
    /// Download over upload.
    case traffic(rx: TrafficFormat.Parts, tx: TrafficFormat.Parts)
    /// The performance score of the orb's set (METRICS 6.9, *Sets and groups*) and thinking tok/s as shown.
    case intelligence(score: PerfSetReading, thinking: String)

    /// The thinking rate as shown reads 0 (DESIGN 10, Thinking hidden at zero).
    public var thinkingReadsZero: Bool {
        guard case .intelligence(_, let thinking) = self else { return false }
        return OrbThinkingLine.readsZero(thinking)
    }

    /// The intelligence readout's score; nil for network speed.
    public var score: PerfSetReading? {
        guard case .intelligence(let score, _) = self else { return nil }
        return score
    }

    fileprivate func isSameKind(as other: OrbLineContent) -> Bool {
        switch (self, other) {
        case (.traffic, .traffic), (.intelligence, .intelligence): true
        default: false
        }
    }
}

/// An orb's readout while it is on: what it is set to show and, once it was loud, its values.
public struct OrbLines: Equatable, Sendable {
    public var format: OrbLineFormat
    /// nil before the first loud moment.
    public var reading: OrbLineReading?

    public init(format: OrbLineFormat, reading: OrbLineReading?) {
        self.format = format
        self.reading = reading
    }
}

/// The readout's values with the quiet rule's two moments. Times are monotonic seconds (the app clock).
public struct OrbLineReading: Equatable, Sendable {
    public var content: OrbLineContent
    /// When the readout began fading in; fully shown `fadeIn` later.
    public var shownAt: TimeInterval
    /// When it begins fading out, `quietAfter` past the last loud moment; gone `fadeOut` later.
    public var quietAt: TimeInterval
    /// The intelligence readout's thinking line, hidden while its rate reads 0.
    public var thinking: OrbThinkingLine
    /// How the intelligence readout's score moves when it changes.
    public var scoreMotion: OrbScoreMotion

    public init(content: OrbLineContent, shownAt: TimeInterval, quietAt: TimeInterval, thinking: OrbThinkingLine = OrbThinkingLine(),
                scoreMotion: OrbScoreMotion? = nil) {
        self.content = content
        self.shownAt = shownAt
        self.quietAt = quietAt
        self.thinking = thinking
        self.scoreMotion = scoreMotion ?? OrbScoreMotion(content.score ?? .none)
    }

    public static let fadeIn: TimeInterval = 0.25
    public static let fadeOut: TimeInterval = 0.6

    /// Opacity at `time`; Reduce Motion makes both fades instant.
    public func alpha(at time: TimeInterval, reduceMotion: Bool) -> Double {
        let fadeIn = reduceMotion ? 0 : Self.fadeIn
        let fadeOut = reduceMotion ? 0 : Self.fadeOut
        let incoming = fadeIn > 0 ? ((time - shownAt) / fadeIn) : (time >= shownAt ? 1 : 0)
        let outgoing = fadeOut > 0 ? 1 - ((time - quietAt) / fadeOut) : (time < quietAt ? 1 : 0)
        return min(max(incoming, 0), 1, max(outgoing, 0))
    }

    /// A fade runs at `time` (frames at 30 fps).
    public func isFading(at time: TimeInterval, reduceMotion: Bool) -> Bool {
        guard !reduceMotion else { return false }
        return (time >= shownAt && time < shownAt + Self.fadeIn) || (time >= quietAt && time < quietAt + Self.fadeOut)
    }

    /// Fully faded out at `time`: nothing is drawn and new quiet values change nothing.
    public func isGone(at time: TimeInterval) -> Bool { time >= quietAt + Self.fadeOut }

    /// Under Reduce Motion a step still renders over this, so its frame is drawn.
    public static let reducedStep: TimeInterval = 0.05

    /// The frame policy's view (DESIGN 10, Frame policy): whether a fade, the thinking line's glide or a change of the
    /// score runs at `time` (frames at 30 fps), and when a still orb must wake next — the quiet moment or the end of
    /// the thinking hold.
    public func motion(at time: TimeInterval, reduceMotion: Bool) -> (frames: Bool, wake: TimeInterval) {
        let step = reduceMotion ? Self.reducedStep : nil
        let gone = quietAt + Self.fadeOut
        let fading = (time >= shownAt && time < shownAt + (step ?? Self.fadeIn)) || (time >= quietAt && time < quietAt + (step ?? Self.fadeOut))
        let moving = time < gone
            && (thinking.isMoving(at: time, step: step) || scoreMotion.isMoving(at: time, reduceMotion: reduceMotion))
        var wake = time < quietAt ? quietAt : .infinity
        if let end = thinking.holdEnd(after: time), end < gone { wake = min(wake, end) }
        return (fading || moving, wake)
    }
}

/// The quiet rule shared by both readouts (DESIGN 10): visible while loud; 10 s after the last loud moment it fades
/// out (0.6 s) and returns (0.25 s) with the next one. Quiet values of a gone readout change nothing, so a quiet orb
/// renders no frames; content of another kind starts over.
public struct OrbLineQuietRule: Sendable {
    public static let quietAfter: TimeInterval = 10
    /// Network speed is loud from `↓ + ↑` 1 KB/s.
    public static let trafficThreshold = 1000.0

    public private(set) var reading: OrbLineReading?

    public init() {}

    /// Applies one moment's values; returns whether the reading changed. `content` nil clears the readout.
    @discardableResult
    public mutating func apply(_ content: OrbLineContent?, loud: Bool, at time: TimeInterval) -> Bool {
        guard let content else {
            guard reading != nil else { return false }
            reading = nil
            return true
        }
        var next: OrbLineReading
        if let current = reading, !current.isGone(at: time), current.content.isSameKind(as: content) {
            next = current
            next.content = content
            if let score = content.score { next.scoreMotion.update(score, at: time) }
            if loud {
                if time >= current.quietAt {
                    // Fading out: come back from the current opacity.
                    let alpha = current.alpha(at: time, reduceMotion: false)
                    next.shownAt = time - alpha * OrbLineReading.fadeIn
                }
                next.quietAt = time + Self.quietAfter
            }
        } else {
            guard loud else {
                // A gone readout stays, so quiet values render nothing; one of another kind leaves.
                guard let current = reading, !current.content.isSameKind(as: content) else { return false }
                reading = nil
                return true
            }
            next = OrbLineReading(content: content, shownAt: time, quietAt: time + Self.quietAfter)
        }
        next.thinking.update(zero: content.thinkingReadsZero, at: time)
        guard next != reading else { return false }
        reading = next
        return true
    }

    /// One traffic sample; nil while the readout is unavailable.
    @discardableResult
    public mutating func apply(_ rate: TrafficRate?, bits: Bool, at time: TimeInterval) -> Bool {
        let content = rate.map {
            OrbLineContent.traffic(rx: TrafficFormat.rateParts($0.rx, bits: bits), tx: TrafficFormat.rateParts($0.tx, bits: bits))
        }
        return apply(content, loud: (rate?.sum ?? 0) >= Self.trafficThreshold, at: time)
    }

    /// One engine tick of the orb's set (DESIGN 10, Performance readout, revised quiet rule): loud while 性能 is live or
    /// resting or one of its agents generates, so a resting score stays pale for as long as its agents are listed; a
    /// generating set without a score shows `–`. Once neither holds, the last score holds until the readout has faded.
    @discardableResult
    public mutating func apply(score: PerfSetReading, thinking: Double, generating: Bool, at time: TimeInterval) -> Bool {
        let loud = score.form != .none || generating
        let shown = loud ? score : heldScore(at: time) ?? score
        return apply(.intelligence(score: shown, thinking: SpeedFormat.speed(thinking)), loud: loud, at: time)
    }

    private func heldScore(at time: TimeInterval) -> PerfSetReading? {
        guard let reading, !reading.isGone(at: time) else { return nil }
        return reading.content.score
    }
}
