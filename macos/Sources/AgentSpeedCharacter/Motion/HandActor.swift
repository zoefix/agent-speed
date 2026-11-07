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

    mutating func choose(_ band: SpeedBand, _ beat: Double, _ rng: inout Rng) -> Motion {
        if holds {
            switch rng.index(3) {
            case 0: return HandActor.bind(HandActor.tap(beat))
            case 1: return HandActor.bind(HandActor.regrip())
            default: return HandActor.bind(HandActor.lift())
            }
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
        wrist = (holds ? 0.03 : 0.09) * sin(2 * Double.pi * time / 5.3 + phase)

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
            action = choose(band, beat, &rng)
            t = 0
        }
        if let motion = action {
            t += dt
            let local = motion.localTime(t)
            for entry in motion.entries {
                let v = entry.curve(local)
                switch entry.slot {
                case HandActor.wristSlot: wrist += v * (holds ? 0.3 : 1.0)
                case HandActor.spreadSlot: spread += v
                default: fingers[entry.slot] += v
                }
            }
            if t >= motion.duration {
                action = nil
                next = rng.uniform(1.6, 4.2)
            }
        }
    }
}
