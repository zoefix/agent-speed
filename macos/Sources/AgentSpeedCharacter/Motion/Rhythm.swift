import Foundation

/// The effect tiers of METRICS 6 as the character's speed bands.
public enum SpeedBand: String, Sendable, CaseIterable, Codable, Comparable {
    case idle, cruise, boost, overdrive, redline

    /// The band of a speed level 0...13.
    public init(level: Int) {
        if level >= 12 { self = .redline } else if level >= 9 { self = .overdrive } else if level >= 5 {
            self = .boost
        } else if level >= 1 { self = .cruise } else { self = .idle }
    }

    public var order: Int { SpeedBand.allCases.firstIndex(of: self)! }

    public static func < (a: SpeedBand, b: SpeedBand) -> Bool { a.order < b.order }

    var bpm: Double {
        switch self {
        case .idle: 56
        case .cruise: 84
        case .boost: 104
        case .overdrive: 124
        case .redline: 140
        }
    }
}

/// The beat clock: its tempo eases toward the band's, scaled by the character's tempo.
struct Beat: Sendable {
    let tempo: Double
    var bpm: Double
    var phase = 0.0

    init(tempo: Double = 1) {
        self.tempo = tempo
        bpm = SpeedBand.idle.bpm * tempo
    }

    mutating func update(_ dt: Double, _ band: SpeedBand) {
        bpm += (band.bpm * tempo - bpm) * min(1.0, dt * 1.2)
        phase += dt * bpm / 60
    }
}

/// Body grooves per band on the beat clock: posture, weight shifts, bounce and arm swing (motions/rhythm.py).
enum Groove {
    typealias Out = [(slot: Int, value: Double)]

    /// 0 on the beat, 1 halfway to the next.
    static func up(_ b: Double) -> Double { abs(sin(Double.pi * b)) }

    /// 1 right on the beat, quickly falling off.
    static func hit(_ b: Double) -> Double { pow(1 - abs(sin(Double.pi * b)), 4.0) }

    static func emit(_ band: SpeedBand, _ b: Double, _ t: Double, _ s: Slots, out o: Slots.Side, book k: Slots.Side,
                     rest: Bool = false, into out: inout Out) {
        out.removeAll(keepingCapacity: true)
        switch band {
        case .idle where rest:
            let shift = sin(2 * Double.pi * t / 7.5)
            out.append((s.angleY, -0.08 + 0.05 * sin(t * 0.9)))
            out.append((s.angleZ, 0.1 * sin(t * 0.5)))
            out.append((s.angleX, -0.15))
            out.append((s.eyeBallX, 0.3 * sin(t * 0.7)))
            out.append((s.eyeBallY, -0.1))
            out.append((k.arm, 0.06))
            out.append((s.hip, 0.25 * shift))
            out.append((s.bodyAngleZ, -0.12 * shift))
            out.append((s.lean, 0.04 * shift))
        case .idle:
            let shift = sin(2 * Double.pi * t / 7.5)
            out.append((s.angleY, -0.55 + 0.05 * sin(t * 0.9)))
            out.append((s.angleZ, -0.35 + 0.05 * sin(t * 0.6)))
            out.append((s.angleX, -0.22))
            out.append((s.eyeBallX, -0.4 + 0.25 * sin(t * 1.3)))
            out.append((s.eyeBallY, -0.7))
            out.append((k.arm, 0.15))
            out.append((k.forearm, -0.1))
            out.append((s.hip, 0.28 * shift))
            out.append((s.bodyAngleZ, -0.15 * shift))
            out.append((s.lean, 0.05 * shift))
        case .cruise:
            let sn = sin(Double.pi * b)
            out.append((s.hip, 0.35 * sn))
            out.append((s.bodyAngleZ, -0.22 * sn))
            out.append((s.angleZ, 0.12 + 0.14 * sin(Double.pi * b + 0.7)))
            out.append((s.angleX, -0.3))
            out.append((s.angleY, 0.04 * cos(2 * Double.pi * b)))
            out.append((s.eyeBallX, -0.35))
            out.append((o.arm, 0.08 * sn))
            out.append((k.arm, 0.04 * sn))
            out.append((s.skirtFlare, 0.05))
        case .boost:
            let sn = sin(Double.pi * b), u = up(b), h = hit(b)
            out.append((s.posY, 0.035 * u))
            out.append((s.squash, 0.1 * u - 0.22 * h))
            out.append((s.hip, 0.45 * sn))
            out.append((s.bodyAngleZ, -0.3 * sn))
            out.append((s.angleZ, -0.25 * sn))
            out.append((s.angleY, 0.08 - 0.12 * h))
            out.append((s.angleX, -0.25))
            out.append((o.arm, 0.05 + 0.25 * sn))
            out.append((o.forearm, -0.12 - 0.1 * sn))
            out.append((k.arm, 0.12 * sn))
            out.append((o.shoulder, 0.25 * sn))
            out.append((k.shoulder, -0.25 * sn))
            out.append((s.skirtFlare, 0.12 + 0.12 * u))
            out.append((s.footPoint, 0.3 * u))
        case .overdrive:
            let sn = sin(Double.pi * b), u = up(b), h = hit(b)
            out.append((s.posY, 0.06 * u))
            out.append((s.squash, 0.15 * u - 0.35 * h))
            out.append((s.hip, 0.5 * sn))
            out.append((s.bodyAngleZ, -0.4 * sn))
            out.append((s.lean, 0.12 * sn))
            out.append((s.angleZ, -0.35 * sn))
            out.append((s.angleY, 0.2 - 0.15 * h))
            out.append((s.angleX, -0.35))
            out.append((o.arm, 0.1 + 0.35 * sn))
            out.append((o.forearm, -0.3 - 0.2 * sn))
            out.append((k.arm, 0.08 + 0.2 * sn))
            out.append((o.shoulder, 0.35 * sn))
            out.append((k.shoulder, -0.35 * sn))
            out.append((s.skirtFlare, 0.25 + 0.15 * u))
            out.append((s.footPoint, 0.5 * u))
            out.append((s.footLift, 0.25 * u))
        case .redline:
            let sn = sin(Double.pi * b), u = up(b), h = hit(b)
            out.append((s.posY, 0.1 * u))
            out.append((s.squash, 0.2 * u - 0.45 * h))
            out.append((s.hip, 0.35 * sn))
            out.append((s.bodyAngleZ, -0.35 * sn))
            out.append((s.lean, 0.1 * sn))
            out.append((s.angleZ, 0.35 * sn))
            out.append((s.angleY, 0.35 - 0.2 * h))
            out.append((s.angleX, -0.45))
            out.append((o.arm, 0.4 + 0.12 * sn))
            out.append((o.forearm, -0.55 - 0.1 * sn))
            out.append((k.arm, 0.25 + 0.08 * sn))
            out.append((o.shoulder, 0.35 + 0.2 * sn))
            out.append((k.shoulder, 0.25 - 0.2 * sn))
            out.append((s.skirtFlare, 0.4 + 0.25 * u))
            out.append((s.footPoint, 0.8 * u))
            out.append((s.footLift, 0.4 * u))
        }
    }
}
