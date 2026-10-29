import AgentSpeedCore
import Foundation

/// How 性能 moves on the orb readout (DESIGN 12, *Motion*; `PerfMotion`), kept as moments so any frame can be drawn at
/// its time and a still orb renders nothing. A count continues from the integer on screen with a fresh duration; each
/// intermediate value is drawn in the compact form. Times are monotonic seconds (the app clock).
public struct OrbScoreMotion: Equatable, Sendable {
    /// The absent mark (U+2013), never `0`: 0 is a real score.
    public static let absent = "\u{2013}"

    /// One frame of the number.
    public struct Frame: Equatable, Sendable {
        /// The number as drawn (mid count its current integer), `–` while none.
        public var text: String
        public var alpha: Double
        /// The text a crossfade fades out, and its opacity.
        public var outgoing: String?
        public var outgoingAlpha: Double
        /// 0 in the live number color, 1 in the pale one.
        public var pale: Double
        /// The provisional mark's opacity.
        public var provisional: Double
    }

    /// The number shown now; nil while none.
    public private(set) var shown: Int?
    /// Before the last change: as drawn at that moment (mid count, its current integer), and the value then shown,
    /// which Reduce Motion fades from.
    public private(set) var previous: Int?
    public private(set) var previousShown: Int?
    public private(set) var changedAt = -TimeInterval.infinity
    /// Resting or none: the pale color.
    public private(set) var pale: Bool
    public private(set) var paleFrom = -TimeInterval.infinity
    public private(set) var provisional: Bool
    public private(set) var provisionalFrom = -TimeInterval.infinity

    public init(_ score: PerfSetReading = .none) {
        shown = score.shown
        previous = score.shown
        previousShown = score.shown
        pale = score.form != .live
        provisional = score.isProvisional
    }

    public static func text(_ value: Int?) -> String { value.map(PerfFormat.compact) ?? absent }

    /// Applies the set's value at `time`. A reversal part-way starts the color or the mark from where it stands; a
    /// change during a count starts from the integer it has reached.
    public mutating func update(_ score: PerfSetReading, at time: TimeInterval) {
        if score.shown != shown {
            previous = drawn(at: time)
            previousShown = shown
            shown = score.shown
            changedAt = time
        }
        let pale = score.form != .live
        if pale != self.pale {
            self.pale = pale
            paleFrom = Self.reversal(at: time, from: paleFrom, over: PerfMotion.form)
        }
        let provisional = score.isProvisional
        if provisional != self.provisional {
            self.provisional = provisional
            provisionalFrom = Self.reversal(at: time, from: provisionalFrom, over: PerfMotion.provisional)
        }
    }

    /// The last number change as a surface draws it.
    public func change(reduceMotion: Bool) -> PerfMotion.Change {
        PerfMotion.change(from: reduceMotion ? previousShown : previous, to: shown, reduceMotion: reduceMotion)
    }

    public func frame(at time: TimeInterval, reduceMotion: Bool) -> Frame {
        let pale = Self.progress(time, from: paleFrom, over: PerfMotion.form)
        let mark = Self.progress(time, from: provisionalFrom, over: reduceMotion ? 0 : PerfMotion.provisional)
        var frame = Frame(text: Self.text(shown), alpha: 1, outgoing: nil, outgoingAlpha: 0,
                          pale: self.pale ? pale : 1 - pale, provisional: provisional ? mark : 1 - mark)
        let elapsed = max(0, time - changedAt)
        switch change(reduceMotion: reduceMotion) {
        case .count(let seconds) where elapsed < seconds:
            if let previous, let shown {
                frame.text = Self.text(PerfMotion.countValue(from: previous, to: shown, elapsed: elapsed))
            }
        case .crossfade(let seconds) where elapsed < seconds:
            frame.alpha = elapsed / seconds
            frame.outgoing = Self.text(reduceMotion ? previousShown : previous)
            frame.outgoingAlpha = 1 - frame.alpha
        default:
            break
        }
        return frame
    }

    /// A count, a crossfade, the color or the mark moves at `time` (frames at the orb's 30 fps). Replacing digits and
    /// Reduce Motion's mark render the one frame of the content change.
    public func isMoving(at time: TimeInterval, reduceMotion: Bool) -> Bool {
        let number: TimeInterval = switch change(reduceMotion: reduceMotion) {
        case .count(let seconds), .crossfade(let seconds): seconds
        case .none, .replace: 0
        }
        return Self.within(time, from: changedAt, over: number) || Self.within(time, from: paleFrom, over: PerfMotion.form)
            || (!reduceMotion && Self.within(time, from: provisionalFrom, over: PerfMotion.provisional))
    }

    /// The integer drawn at `time` without Reduce Motion: mid count its current value, else the shown one.
    private func drawn(at time: TimeInterval) -> Int? {
        guard case .count(let seconds) = change(reduceMotion: false), let previous, let shown,
              time - changedAt < seconds else { return shown }
        return PerfMotion.countValue(from: previous, to: shown, elapsed: max(0, time - changedAt))
    }

    private static func within(_ time: TimeInterval, from start: TimeInterval, over duration: TimeInterval) -> Bool {
        time >= start && time < start + duration
    }

    private static func progress(_ time: TimeInterval, from start: TimeInterval, over duration: TimeInterval) -> Double {
        guard duration > 0 else { return time >= start ? 1 : 0 }
        return min(max((time - start) / duration, 0), 1)
    }

    /// The start of a motion toward the other side that begins where the current one stands at `time`.
    private static func reversal(at time: TimeInterval, from start: TimeInterval, over duration: TimeInterval) -> TimeInterval {
        time - (1 - progress(time, from: start, over: duration)) * duration
    }
}
