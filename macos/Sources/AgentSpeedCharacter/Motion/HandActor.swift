import Foundation

/// Hand acting (motions/hands.py): a slow ripple across the fingers, a drifting wrist and spread, fidgets now and then
/// (taps, regrips and lifts on the object hand; flexes, fans, counting and drumming on the free hand), and the wrist
/// trailing the arm.
struct HandActor: Sendable {
    /// Local slots of hand motions: fingers 0...4, then the wrist and the spread.
    static let wristSlot = 5
    static let spreadSlot = 6

    let side: CharacterSide
    var holds: Bool
    let phase: Double
    /// The character's fidget sets for the free hand (empty: flex, fan, count, drum).
    var freeSets: [String] = []
    /// The pose's hand set, played back to back (`setMode`).
    var mode: String?
    var modeAction = false
    var action: Motion?
    var t = 0.0
    var next: Double
    var previousArm: Double?
    var trail = 0.0
    /// Finger 0...4, spread, wrist offsets of the last update.
    var fingers = [Double](repeating: 0, count: 5)
    var spread = 0.0
    var wrist = 0.0

    init(side: CharacterSide, holds: Bool, phase: Double, rng: inout Rng) {
        self.side = side
        self.holds = holds
        self.phase = phase
        next = rng.uniform(1.0, 3.0)
    }

    static func bind(_ spec: MotionSpec) -> Motion {
        spec.bind { name in
            switch name {
            case "W": wristSlot
            case "S": spreadSlot
            default: Int(name.dropFirst())
            }
        }
    }

    static func stagger(_ name: String, _ d: Double, _ perFinger: [(Double, Double)], _ order: [Int],
                        _ gap: Double) -> MotionSpec {
        var c = CurveSet()
        for (k, i) in order.enumerated() {
            let dt = Double(k) * gap
            c.set("F\(i)", [(0, 0)] + perFinger.map { ($0.0 + dt, $0.1) } + [(d + dt + 0.2, 0)])
        }
        return MotionSpec(name, d + gap * Double(order.count - 1) + 0.25, c)
    }

    static func tap(_ beat: Double) -> MotionSpec {
        let one: [(Double, Double)] = [(0.08, -0.25), (0.18, 0.3), (0.3, 0.0)]
        let keys = one + one.map { ($0.0 + beat, $0.1) }
        return stagger("tap", 0.3 + beat, keys, [1, 2], 0.12)
    }

    static func regrip() -> MotionSpec {
        var m = stagger("regrip", 0.7, [(0.2, 0.32), (0.45, 0.28), (0.7, 0.0)], [4, 3, 2, 1, 0], 0.05)
        m.curves.set("W", [(0, 0), (0.25, -0.12), (0.8, 0)])
        return m
    }

    static func lift() -> MotionSpec {
        stagger("lift", 0.6, [(0.15, -0.22), (0.35, -0.22), (0.6, 0.0)], [1, 2, 3, 4], 0.07)
    }

    static func flex() -> MotionSpec {
        var m = stagger("flex", 1.2, [(0.25, 0.4), (0.55, 0.35), (0.85, -0.15), (1.2, 0.0)], [4, 3, 2, 1, 0], 0.06)
        m.curves.set("S", [(0, 0), (0.6, 0), (0.95, 0.45), (1.5, 0)])
        m.curves.set("W", [(0, 0), (0.3, 0.2), (0.9, -0.15), (1.5, 0)])
        return m
    }

    static func fan() -> MotionSpec {
        var m = stagger("fan", 1.0, [(0.3, -0.18), (0.7, -0.15), (1.0, 0.0)], [0, 1, 2, 3, 4], 0.04)
        m.curves.set("S", [(0, 0), (0.35, 0.6), (0.75, 0.5), (1.2, 0)])
        m.curves.set("W", [(0, 0), (0.35, -0.2), (1.2, 0)])
        return m
    }

    static func count(_ beat: Double) -> MotionSpec {
        var c = CurveSet()
        for i in 0..<5 {
            let t = 0.1 + Double(i) * beat
            c.set("F\(i)", [(0, 0), (t, 0), (t + 0.12, 0.42), (5 * beat + 0.2, 0.42), (5 * beat + 0.55, 0.0)])
        }
        let w = (0..<5).map { i in (0.1 + Double(i) * beat + 0.12, 0.06 * (i % 2 == 0 ? 1.0 : -1.0)) }
        c.set("W", [(0, 0)] + w + [(5 * beat + 0.6, 0)])
        return MotionSpec("count", 5 * beat + 0.6, c)
    }

    static func drum(_ beat: Double, bars: Int = 2) -> MotionSpec {
        var keys: [Int: [(Double, Double)]] = [1: [(0, 0)], 2: [(0, 0)], 3: [(0, 0)]]
        var t = 0.05
        for _ in 0..<(4 * bars) {
            for (i, dt) in [(3, 0.0), (2, 0.06), (1, 0.12)] {
                keys[i]! += [(t + dt, -0.15), (t + dt + 0.07, 0.3), (t + dt + 0.16, 0.0)]
            }
            t += beat
        }
        var c = CurveSet()
        for i in [1, 2, 3] { c.set("F\(i)", keys[i]! + [(t + 0.2, 0)]) }
        return MotionSpec("drum", t + 0.2, c)
    }

    /// Fingering a transverse flute: index, middle and ring fingers lift and press through a fixed tune.
    static func flute(_ beat: Double) -> MotionSpec {
        let pattern: [[Int]] = [[1], [1, 2], [2, 3], [1, 3], [2], [1, 2, 3], [3], [1, 2]]
        let step = 0.5 * beat
        var keys: [Int: [(Double, Double)]] = [1: [(0, 0)], 2: [(0, 0)], 3: [(0, 0)]]
        for (k, lifted) in pattern.enumerated() {
            let t = 0.05 + Double(k) * step
            for i in [1, 2, 3] {
                let v = lifted.contains(i) ? -0.3 : 0.12
                keys[i]! += [(t + 0.06, v), (t + step - 0.04, v)]
            }
        }
        let end = 0.05 + Double(pattern.count) * step + 0.1
        var c = CurveSet()
        for i in [1, 2, 3] { c.set("F\(i)", keys[i]! + [(end, 0)]) }
        return MotionSpec("flute", end, c)
    }

    static func fanflutter(_ beat: Double) -> MotionSpec {
        let d = 1.9
        var c = CurveSet()
        c.set("W", [(0, 0)] + Keys.oscillate(0.1, d - 0.1, 0.6, 0.22) + [(d, 0)])
        c.set("S", [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)])
        return MotionSpec("fanflutter", d + 0.5 * beat, c)
    }

    static func abacus(_ beat: Double) -> MotionSpec {
        let step = max(0.5 * beat, 0.24)
        var f1: [(Double, Double)] = [(0, 0)], f0: [(Double, Double)] = [(0, 0)]
        for k in 0..<6 {
            let t = 0.05 + Double(k) * step
            f1 += [(t, 0.0), (t + 0.05, 0.5), (t + 0.11, -0.12), (t + 0.2, 0.0)]
            if k % 2 == 0 { f0 += [(t, 0.0), (t + 0.06, 0.25), (t + 0.16, 0.0)] }
        }
        let end = 0.05 + 6 * step + 0.1
        var c = CurveSet()
        c.set("F1", f1 + [(end, 0)])
        c.set("F0", f0 + [(end, 0)])
        c.set("W", [(0, 0)] + Keys.oscillate(0.05, end - 0.05, 2 * step, 0.05) + [(end, 0)])
        return MotionSpec("abacus", end, c)
    }

    static func sleeveflop(_ beat: Double) -> MotionSpec {
        let d = 2.4
        let droop: [(Double, Double)] = [(0, 0), (0.4, 0.2), (d - 0.4, 0.2), (d, 0)]
        var c = CurveSet()
        c.set("W", [(0, 0)] + Keys.oscillate(0.1, d - 0.1, 1.6, 0.3) + [(d, 0)])
        for i in [1, 2, 3, 4] { c.set("F\(i)", droop) }
        return MotionSpec("sleeveflop", d, c)
    }

    static func ripple(_ beat: Double) -> MotionSpec {
        var c = CurveSet()
        c.set("S", [(0, 0), (2.0, 0)])
        return MotionSpec("ripple", 2.0, c)
    }

    /// Both hands holding one object up: the fingers tighten a little on every other beat, the wrists still, so
    /// the hands never leave it and the body sways it from side to side.
    static func grip(_ beat: Double) -> MotionSpec {
        let d = 2 * beat
        var c = CurveSet()
        for i in [1, 2, 3, 4] { c.set("F\(i)", [(0, 0), (0.35 * d, 0.1), (0.6 * d, 0.1), (d, 0)]) }
        return MotionSpec("grip", d, c)
    }

    /// Every hand set by name (motions/hands.py, make).
    static let sets = ["tap", "regrip", "lift", "flex", "fan", "count", "drum", "flute", "fanflutter", "abacus",
                       "sleeveflop", "ripple", "grip"]

    static func make(_ name: String, _ beat: Double) -> MotionSpec {
        switch name {
        case "tap": tap(beat)
        case "regrip": regrip()
        case "lift": lift()
        case "flex": flex()
        case "fan": fan()
        case "count": count(beat)
        case "drum": drum(beat)
        case "flute": flute(beat)
        case "fanflutter": fanflutter(beat)
        case "abacus": abacus(beat)
        case "sleeveflop": sleeveflop(beat)
        case "grip": grip(beat)
        default: ripple(beat)
        }
    }

    /// Which hands a pose's hand set plays on: both, the hand holding the object, or the free one.
    static func modeSides(_ name: String) -> String {
        switch name {
        case "flute", "sleeveflop", "ripple", "grip": "both"
        case "fanflutter", "tap", "regrip", "lift": "holder"
        default: "free"
        }
    }

    var modeOn: Bool {
        guard let mode else { return false }
        let sides = HandActor.modeSides(mode)
        return sides == "both" || (sides == "holder") == holds
    }

    /// The pose's hand set: played back to back on the hands it belongs to, from the next action on.
    mutating func setMode(_ mode: String?) {
        self.mode = mode
        if modeOn, action == nil { next = min(next, 0) }
    }

    mutating func choose(_ band: SpeedBand, _ beat: Double, _ rng: inout Rng) -> Motion {
        if modeOn, let mode { return HandActor.bind(HandActor.make(mode, beat)) }
        if holds {
            switch rng.index(3) {
            case 0: return HandActor.bind(HandActor.tap(beat))
            case 1: return HandActor.bind(HandActor.regrip())
            default: return HandActor.bind(HandActor.lift())
            }
        }
        if !freeSets.isEmpty {
            let options = freeSets.map { HandActor.make($0, beat) }
            return HandActor.bind(options[rng.index(options.count)])
        }
        let busy = band == .boost || band == .overdrive || band == .redline
        switch rng.index(busy ? 4 : 2) {
        case 0: return HandActor.bind(HandActor.flex())
        case 1: return HandActor.bind(HandActor.fan())
        case 2: return HandActor.bind(HandActor.count(beat))
        default: return HandActor.bind(HandActor.drum(beat))
        }
    }

    static func busyness(_ band: SpeedBand) -> Double {
        switch band {
        case .idle: 0.5
        case .cruise: 0.8
        case .boost: 1.1
        case .overdrive: 1.4
        case .redline: 1.7
        }
    }

    /// Advances one frame; the offsets land in `fingers`, `spread` and `wrist`.
    mutating func update(_ dt: Double, time: Double, band: SpeedBand, beat: Double, arm: Double, rng: inout Rng) {
        let rest = holds ? 0.06 : 0.14
        let amp = holds ? 0.04 : 0.07
        for i in 0..<5 {
            fingers[i] = rest + amp * sin(2 * Double.pi * time / 3.4 - 0.55 * Double(i) + phase)
        }
        spread = 0.08 * sin(2 * Double.pi * time / 4.6 + 1.1 + phase)
        let sway = mode.map(MotionProfile.twoHanded.contains) == true ? 0.0 : (holds ? 0.03 : 0.09)
        wrist = sway * sin(2 * Double.pi * time / 5.3 + phase)

        if let previousArm {
            let vel = (arm - previousArm) / max(dt, 1e-6)
            trail += (max(-1.0, min(1.0, -0.35 * vel)) - trail) * min(1.0, dt * 10)
        }
        previousArm = arm
        wrist += trail * (holds ? 0.25 : 0.6)
        for i in 0..<5 {
            fingers[i] -= abs(trail) * 0.25
        }

        next -= dt * HandActor.busyness(band)
        if action == nil, next <= 0 {
            modeAction = modeOn
            action = choose(band, beat, &rng)
            t = 0
        }
        if let motion = action {
            t += dt
            let local = motion.localTime(t)
            let wristScale = modeAction || !holds ? 1.0 : 0.3
            for entry in motion.entries {
                let v = entry.curve(local)
                switch entry.slot {
                case HandActor.wristSlot: wrist += v * wristScale
                case HandActor.spreadSlot: spread += v
                default: fingers[entry.slot] += v
                }
            }
            if t >= motion.duration {
                action = nil
                next = modeOn ? 0 : rng.uniform(1.6, 4.2)
            }
        }
    }
}
