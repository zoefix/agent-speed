import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// How a board's 性能 changes from one fresh tick to the next (DESIGN 12 *Motion*; 2c *Updates, motion and quiet*): the
/// hero counts a change of at least 1 % and 3 through the integers, up or down; rows replace their digits in place;
/// `–` ↔ a number crossfades (0.15 s); live ↔ resting crossfades the color (0.2 s, kept with Reduce Motion); the
/// provisional mark fades in and out (0.25 s, at once with Reduce Motion); Reduce Motion turns every value change into a
/// 0.15 s crossfade.
enum BoardScoreMotion {
    struct Hero: Equatable {
        /// Seconds of a count from the shown value to the new one.
        var count: Double?
        /// The number's image crossfades over this many seconds (its provisional mark fades on its own).
        var numberFade: Double?
        /// The arc and tip, or the meter, crossfade over this many seconds (their resting and provisional looks).
        var shapeFade: Double?

        static let none = Hero()
    }

    static func hero(from old: PerfSetReading, to new: PerfSetReading, reduceMotion: Bool) -> Hero {
        var motion = Hero()
        switch PerfMotion.change(from: old.shown, to: new.shown, reduceMotion: reduceMotion) {
        case .count(let seconds): motion.count = seconds
        case .crossfade(let seconds): motion.numberFade = seconds
        case .none, .replace: break
        }
        if old.form != .none, new.form != .none {
            if old.form != new.form {
                motion.numberFade = max(motion.numberFade ?? 0, PerfMotion.form)
                motion.shapeFade = PerfMotion.form
            }
            if old.isProvisional != new.isProvisional, !reduceMotion {
                motion.shapeFade = max(motion.shapeFade ?? 0, ProvisionalMark.fadeSeconds)
            }
        }
        return motion
    }

    /// The crossfade of a row's 性能 cell, nil when its new image replaces the old in place.
    static func row(from old: BoardRowScore, to new: BoardRowScore, reduceMotion: Bool) -> Double? {
        var fade: Double?
        if case .crossfade(let seconds) = PerfMotion.change(from: old.shown, to: new.shown, reduceMotion: reduceMotion,
                                                            counts: false) {
            fade = seconds
        }
        if old.shown != nil, new.shown != nil {
            if old.live != new.live { fade = max(fade ?? 0, PerfMotion.form) }
            if old.provisional != new.provisional, !reduceMotion { fade = max(fade ?? 0, ProvisionalMark.fadeSeconds) }
        }
        return fade
    }
}

/// A count of the hero's 性能 (DESIGN 12 *Motion*): every integer from `from` to `to` over
/// `clamp(0.3 + 0.35 × |log2(to / from)|, 0.3, 0.7)` s with easeOutCubic timing; the arc's end and the meter follow the
/// shown value.
struct BoardScoreCount: Equatable {
    var from: Int
    var to: Int
    var start: TimeInterval

    var seconds: Double { PerfMotion.countSeconds(from: from, to: to) }
    var end: TimeInterval { start + seconds }

    func shown(at now: TimeInterval) -> Int {
        now >= end ? to : PerfMotion.countValue(from: from, to: to, elapsed: max(0, now - start))
    }
}

/// The hero's 性能 across layouts: the value it shows now (a count passes through the integers between ticks, laid out
/// again on every frame of the roll class) and the motion a fresh tick plays. A new value during a count continues from
/// the value on screen with a fresh duration.
struct BoardHeroScore {
    private(set) var count: BoardScoreCount?

    /// When a running count lands; nil when none runs.
    func climbsUntil(after now: TimeInterval) -> TimeInterval? {
        guard let count, count.end > now else { return nil }
        return count.end
    }

    /// One layout of `reading`. `previous`: what the last fresh tick showed (nil before the first); `fresh`: this layout
    /// is a new tick, so its changes move. Returns the integer to draw and the motion to play.
    mutating func update(_ reading: PerfSetReading, previous: PerfSetReading?, fresh: Bool, now: TimeInterval,
                         reduceMotion: Bool) -> (shown: Int?, motion: BoardScoreMotion.Hero) {
        if let running = count, now >= running.end || running.to != reading.shown && !fresh { count = nil }
        guard fresh, let old = previous else { return (count?.shown(at: now) ?? reading.shown, .none) }
        let displayed = count?.shown(at: now) ?? old.shown
        var motion = BoardScoreMotion.hero(from: old, to: reading, reduceMotion: reduceMotion)
        if let running = count, running.to == reading.shown {
            // The same value while counting: the count goes on.
            motion.count = nil
            return (running.shown(at: now), motion)
        }
        if count != nil, !reduceMotion, let from = displayed, let to = reading.shown, to != from,
           PerfMotion.countsChange(from: from, to: to) {
            // A further change while counting counts on from where the number is.
            motion.count = PerfMotion.countSeconds(from: from, to: to)
        }
        if motion.count != nil, let from = displayed, let to = reading.shown, to != from {
            count = BoardScoreCount(from: from, to: to, start: now)
            return (from, motion)
        }
        count = nil
        motion.count = nil
        return (reading.shown, motion)
    }
}
