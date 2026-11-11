import Foundation

/// The moves of the motion profiles (motions/moves.py: tailswish, wingflap, eartwitch, floatbob, pounce, swagger) and
/// the face gestures that play their motion for a character who has them as signature actions (yawn, hum).
extension Moves {
    /// Profile moves and the rig parameters each needs (any one of them).
    static let profile: [String: [String]] = [
        "tailswish": ["TailSwish"], "wingflap": ["WingFlap"], "eartwitch": ["EarTwitch.left", "EarTwitch.right"],
        "floatbob": ["PosY"], "swagger": ["Hip"],
    ]

    static func profileMove(_ name: String, out: CharacterSide, book: CharacterSide) -> MotionSpec? {
        switch name {
        case "tailswish": tailswish()
        case "wingflap": wingflap()
        case "eartwitch": eartwitch()
        case "floatbob": floatbob(out, book)
        case "swagger": swagger(out, book)
        default: nil
        }
    }

    static func tailswish() -> MotionSpec {
        let d = 1.6
        var c = CurveSet()
        c.set("TailSwish", [(0, 0)] + Keys.oscillate(0.1, d - 0.15, 0.7, 0.9) + [(d, 0)])
        c.set("Hip", [(0, 0)] + Keys.oscillate(0.1, d - 0.15, 0.7, 0.18, phase: Double.pi) + [(d, 0)])
        c.set("AngleZ", [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)])
        return MotionSpec("tailswish", d, c)
    }

    static func wingflap() -> MotionSpec {
        let d = 1.5
        var c = CurveSet()
        c.set("WingFlap", [(0, 0), (0.2, -0.25), (0.42, 1.0), (0.62, -0.1), (0.84, 0.9), (1.06, -0.05), (1.3, 0.3), (d, 0)])
        c.set("PosY", [(0, 0), (0.42, 0.03), (0.62, 0.0), (0.84, 0.035), (1.06, 0.0), (d, 0)])
        c.set("Squash", [(0, 0), (0.2, -0.15), (0.42, 0.12), (0.62, -0.05), (0.84, 0.1), (d, 0)])
        both(&c, "Shoulder", [(0, 0), (0.42, 0.3), (0.84, 0.25), (d, 0)])
        return MotionSpec("wingflap", d, c, events: [(0.42, "petals")])
    }

    static func eartwitch() -> MotionSpec {
        let d = 1.0
        func flick(_ t0: Double) -> K {
            [(0, 0), (t0, 0), (t0 + 0.06, 1.0), (t0 + 0.14, -0.35), (t0 + 0.24, 0.15), (t0 + 0.36, 0), (d, 0)]
        }
        var c = CurveSet()
        c.set("EarTwitch.left", flick(0.08))
        c.set("EarTwitch.right", flick(0.3))
        c.set("AngleZ", [(0, 0), (0.2, 0.12), (0.6, -0.08), (d, 0)])
        c.set("EyeBallX", [(0, 0), (0.2, 0.5), (0.6, -0.3), (d, 0)])
        return MotionSpec("eartwitch", d, c)
    }

    static func floatbob(_ out: CharacterSide, _ book: CharacterSide) -> MotionSpec {
        let d = 3.4
        var c = CurveSet()
        c.set("PosY", [(0, 0)] + Keys.oscillate(0.2, d - 0.3, 1.6, 0.035, base: 0.03) + [(d, 0)])
        c.set("Lean", swing(d, 1.7, 0.18))
        c.set("AngleZ", swing(d, 3.2, 0.3))
        c.set("Hip", swing(d, 1.7, 0.22, phase: Double.pi))
        c.set("Arm.\(out.rawValue)", swing(d, 1.6, 0.15, base: 0.3, ease: 0.5))
        c.set("Arm.\(book.rawValue)", swing(d, 1.6, 0.1, base: 0.2, phase: Double.pi, ease: 0.5))
        c.set("SkirtFlare", [(0, 0), (0.5, 0.3), (d - 0.5, 0.3), (d, 0)])
        c.set("TailSwish", swing(d, 1.7, 0.5))
        c.set("AngleY", [(0, 0), (0.6, 0.15), (d - 0.6, 0.15), (d, 0)])
        return MotionSpec("floatbob", d, c, fade: 0.25, expression: "smile")
    }

    static func pounce(_ out: CharacterSide, _ book: CharacterSide, _ orb: Double) -> MotionSpec {
        let d = 1.25
        var c = CurveSet()
        c.set("Squash", [(0, 0), (0.3, -0.6), (0.5, -0.62), (0.56, 0.4), (0.76, 0.1), (0.86, -0.45), (1.0, 0.1), (d, 0)])
        c.set("PosY", [(0, 0), (0.5, 0)] + Keys.ballistic(0.54, 0.32, 0.22) + [(d, 0)])
        c.set("Lean", [(0, 0), (0.3, 0.3 * orb), (0.5, 0.35 * orb), (0.7, 0.1 * orb), (d, 0)])
        c.set("Arm.\(out.rawValue)", [(0, 0), (0.3, -0.1), (0.6, 0.55), (0.9, 0.2), (d, 0)])
        c.set("Forearm.\(out.rawValue)", [(0, 0), (0.3, 0.2), (0.6, -0.4), (d, 0)])
        c.set("Arm.\(book.rawValue)", [(0, 0), (0.6, 0.3), (d, 0)])
        c.set("AngleY", [(0, 0), (0.3, -0.3), (0.6, 0.25), (0.9, -0.1), (d, 0)])
        c.set("FootPoint", [(0, 0), (0.54, 0.2), (0.6, 1.0), (0.84, 0.8), (0.86, 0), (d, 0)])
        c.set("SkirtFlare", [(0, 0), (0.7, 0.5), (0.9, 0.6), (d, 0)])
        c.set("TailSwish", [(0, 0), (0.3, -0.6), (0.6, 0.9), (d, 0)])
        return MotionSpec("pounce", d, c, expression: "happy", events: [(0.56, "petals")])
    }

    static func swagger(_ out: CharacterSide, _ book: CharacterSide) -> MotionSpec {
        let d = 1.9
        let o = out.rawValue
        var c = CurveSet()
        c.set("Hip", [(0, 0), (0.4, 0.6), (1.4, 0.6), (d, 0)])
        c.set("BodyAngleZ", [(0, 0), (0.4, -0.35), (1.4, -0.3), (d, 0)])
        c.set("Lean", [(0, 0), (0.4, -0.15), (1.4, -0.12), (d, 0)])
        c.set("AngleZ", [(0, 0), (0.45, 0.35), (1.4, 0.3), (d, 0)])
        c.set("AngleY", [(0, 0), (0.45, 0.2), (1.4, 0.2), (d, 0)])
        c.set("Shoulder.\(o)", [(0, 0), (0.4, 0.5), (0.8, 0.2), (1.2, 0.5), (d, 0)])
        c.set("Arm.\(o)", [(0, 0), (0.4, 0.2), (1.4, 0.2), (d, 0)])
        c.set("EyeBallX", [(0, 0), (0.45, 0.6), (1.4, 0.6), (d, 0)])
        return MotionSpec("swagger", d, c, expression: "determined")
    }
}

extension Gestures {
    static func faceGesture(_ name: String, duration: Double = 2.4) -> MotionSpec {
        var c = CurveSet()
        if name == "yawn" {
            c.set("AngleY", [(0, 0), (0.5, 0.45), (1.6, 0.4), (duration, 0)])
            c.set("AngleZ", [(0, 0), (0.6, -0.35), (duration, 0)])
            c.set("Breath", [(0, 0), (0.7, 1.0), (1.8, 0.2), (duration, 0)])
            c.set("Squash", [(0, 0), (0.7, 0.15), (1.5, 0.13), (duration, 0)])
            c.set("Shoulder.left", [(0, 0), (0.7, 0.9), (1.6, 0.8), (duration, 0)])
            c.set("Shoulder.right", [(0, 0), (0.7, 0.9), (1.6, 0.8), (duration, 0)])
            c.set("FootPoint", [(0, 0), (0.7, 0.5), (1.5, 0.5), (duration, 0)])
            c.set("Lean", [(0, 0), (0.8, 0.1), (1.6, -0.08), (duration, 0)])
            return MotionSpec("yawn", duration, c, fade: 0.25, expression: "sleepy")
        }
        let d = duration + 1.0
        c.set("AngleZ", Keys.oscillate(0, d, 1.2, 0.3))
        c.set("AngleX", [(0, 0), (0.4, -0.25), (d, 0)])
        c.set("Hip", Keys.oscillate(0, d, 1.2, 0.3, phase: Double.pi))
        c.set("BodyAngleZ", Keys.oscillate(0, d, 1.2, 0.15))
        return MotionSpec("hum", d, c, fade: 0.25, expression: "smile", talk: true)
    }
}
