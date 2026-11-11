import Foundation

/// Whole-body moves (motions/moves.py): hops, a jump, a paper-doll spin, side steps, a curtsy, tiptoes, foot taps, a
/// sway dance, running in place, a stretch, a bow and a peek at the orb. `out` is the free arm, `book` the arm
/// holding an object, `orb` is -1 or 1 toward the orb.
enum Moves {
    typealias K = [(Double, Double)]

    static let names = ["hop", "jump", "hops", "spin", "curtsy", "tiptoe", "foottap", "sway", "run", "stretch", "bow"]

    static func make(_ name: String, out: CharacterSide, book: CharacterSide) -> MotionSpec? {
        switch name {
        case "hop": hop(out, book)
        case "jump": jump(out, book)
        case "hops": hops(out, book)
        case "spin": spin(out, book)
        case "curtsy": curtsy(out, book)
        case "tiptoe": tiptoe(out, book)
        case "foottap": foottap(out, book)
        case "sway": sway(out, book)
        case "run": run(out, book)
        case "stretch": stretch(out, book)
        case "bow": bow(out, book)
        default: nil
        }
    }

    static func both(_ c: inout CurveSet, _ prefix: String, _ keys: K) {
        c.set("\(prefix).left", keys)
        c.set("\(prefix).right", keys)
    }

    static func swing(_ d: Double, _ period: Double, _ amplitude: Double, base: Double = 0, phase: Double = 0,
                      ease: Double = 0.15) -> K {
        [(0, 0)] + Keys.oscillate(ease, d - ease, period, amplitude, base: base, phase: phase) + [(d, 0)]
    }

    static func hop(_ out: CharacterSide, _ book: CharacterSide, height: Double = 0.3, air: Double = 0.36,
                    name: String = "hop", expression: String = "happy", arms: Bool = true) -> MotionSpec {
        let t0 = 0.14, t1 = 0.22
        let t2 = t1 + air
        let t3 = t2 + 0.08
        let t4 = t3 + 0.26
        let apex = t1 + air / 2
        var c = CurveSet()
        c.set("PosY", [(0, 0)] + Keys.ballistic(t1, air, height) + [(t4, 0)])
        c.set("Squash", [(0, 0), (t0, -0.55), (t1, 0.45), (apex, 0.08), (t2 - 0.02, 0.3), (t3, -0.6), (t3 + 0.1, 0.12),
                         (t4, 0)])
        c.set("SkirtFlare", [(0, 0), (t1, 0.05), (apex, 0.6), (t2, 0.45), (t3, 0.75), (t4, 0)])
        c.set("FootPoint", [(0, 0), (t1, 0.2), (t1 + 0.08, 1.0), (t2 - 0.06, 0.9), (t2, 0), (t4, 0)])
        c.set("FootLift", [(0, 0), (t1, 0), (t1 + 0.1, 0.5), (t2 - 0.08, 0.3), (t2, 0), (t4, 0)])
        c.set("AngleY", [(0, 0), (t0, -0.3), (t1 + 0.05, 0.35), (t2, 0.0), (t3, -0.35), (t4, 0)])
        both(&c, "Shoulder", [(0, 0), (t0, -0.4), (t1 + 0.05, 0.7), (t2, 0.2), (t3, -0.3), (t4, 0)])
        if arms {
            c.set("Arm.\(out.rawValue)", [(0, 0), (t0, -0.1), (t1 + 0.08, 0.35), (t2, 0.15), (t3, 0), (t4, 0)])
            c.set("Arm.\(book.rawValue)", [(0, 0), (t0, -0.05), (t1 + 0.08, 0.25), (t2, 0.1), (t3, 0), (t4, 0)])
        }
        return MotionSpec(name, t4, c, expression: expression, events: [(t1, "petals")])
    }

    static func jump(_ out: CharacterSide, _ book: CharacterSide, arms: Bool = true) -> MotionSpec {
        hop(out, book, height: 0.45, air: 0.48, name: "jump", arms: arms)
    }

    static func hops(_ out: CharacterSide, _ book: CharacterSide, count: Int = 3, height: Double = 0.22,
                     air: Double = 0.3) -> MotionSpec {
        let ao = "Arm.\(out.rawValue)", ab = "Arm.\(book.rawValue)"
        let order = ["PosY", "Squash", "FootPoint", "SkirtFlare", "AngleY", "Hip", ao, ab]
        var keys: [String: K] = [:]
        for p in order { keys[p] = [(0, 0)] }
        var t = 0.12
        keys["Squash"]!.append((t, -0.4))
        for i in 0..<count {
            let side: Double = i % 2 == 0 ? 1 : -1
            let t1 = t + 0.06
            let apex = t1 + air / 2
            keys["Squash"]! += [(t1, 0.35), (apex, 0.05)]
            keys["PosY"]! += Keys.ballistic(t1, air, height)
            keys["SkirtFlare"]! += [(apex, 0.5), (t1 + air, 0.35)]
            keys["FootPoint"]! += [(t1 + 0.06, 1.0), (t1 + air - 0.05, 0.8), (t1 + air, 0.0)]
            keys["AngleY"]! += [(apex, 0.25), (t1 + air, -0.15)]
            keys["Hip"]! += [(apex, 0.3 * side)]
            keys[ao]! += [(apex, 0.4)]
            keys[ab]! += [(apex, 0.2)]
            t = t1 + air
            keys["Squash"]!.append((t + 0.06, -0.45))
            t += 0.06
        }
        let end = t + 0.28
        var c = CurveSet()
        for p in order { c.set(p, keys[p]! + [(end, 0)]) }
        return MotionSpec("hops", end, c, expression: "happy")
    }

    static func spin(_ out: CharacterSide, _ book: CharacterSide) -> MotionSpec {
        let d = 0.95
        var c = CurveSet()
        c.set("Spin", [(0, 0), (0.12, 0), (0.68, 1.0), (d, 1.0)])
        c.set("PosY", [(0, 0), (0.12, 0)] + Keys.ballistic(0.14, 0.52, 0.22) + [(d, 0)])
        c.set("Squash", [(0, 0), (0.1, -0.35), (0.16, 0.3), (0.42, 0.15), (0.64, 0.2), (0.72, -0.4), (d, 0)])
        c.set("SkirtFlare", [(0, 0), (0.18, 0.6), (0.42, 1.0), (0.7, 0.8), (d, 0)])
        c.set("Arm.\(out.rawValue)", [(0, 0), (0.16, 0.5), (0.62, 0.5), (d, 0)])
        c.set("Arm.\(book.rawValue)", [(0, 0), (0.16, 0.3), (0.62, 0.3), (d, 0)])
        c.set("AngleZ", [(0, 0), (0.18, 0.3), (0.62, 0.3), (d, 0)])
        c.set("AngleY", [(0, 0), (0.18, 0.25), (0.62, 0.2), (d, 0)])
        c.set("FootPoint", [(0, 0), (0.16, 1.0), (0.62, 1.0), (0.7, 0), (d, 0)])
        return MotionSpec("spin", d, c, expression: "happy", events: [(0.25, "petals"), (0.5, "petals")])
    }

    static func sidestep(_ out: CharacterSide, _ book: CharacterSide, _ dx: Double) -> MotionSpec {
        let d = 1.15
        let s = dx.sign == .minus ? -1.0 : 1.0
        var c = CurveSet()
        c.set("PosX", [(0, 0), (0.1, 0), (0.98, dx), (d, dx)])
        c.set("PosY", [(0, 0), (0.12, 0)] + Keys.ballistic(0.15, 0.34, 0.08) + Keys.ballistic(0.57, 0.34, 0.08) + [(d, 0)])
        c.set("Squash", [(0, 0), (0.12, -0.25), (0.18, 0.15), (0.5, -0.22), (0.6, 0.15), (0.92, -0.22), (d, 0)])
        c.set("Lean", [(0, 0), (0.15, 0.4 * s), (0.8, 0.25 * s), (d, 0)])
        c.set("Hip", [(0, 0), (0.3, -0.3 * s), (0.72, 0.3 * s), (d, 0)])
        c.set("SkirtFlare", [(0, 0), (0.3, 0.3), (0.9, 0.2), (d, 0)])
        c.set("FootLift", [(0, 0), (0.22, 0.5), (0.48, 0), (0.64, 0.5), (0.9, 0), (d, 0)])
        c.set("AngleX", [(0, 0), (0.15, 0.35 * s), (0.8, 0.35 * s), (d, 0)])
        c.set("Arm.\(out.rawValue)", [(0, 0), (0.3, 0.3), (0.72, -0.05), (d, 0)])
        c.set("Arm.\(book.rawValue)", [(0, 0), (0.3, -0.05), (0.72, 0.2), (d, 0)])
        return MotionSpec("step", d, c, travel: dx)
    }

    static func scaled(_ hold: K, _ k: Double) -> K { hold.map { ($0.0, $0.1 * k) } }

    static func curtsy(_ out: CharacterSide, _ book: CharacterSide) -> MotionSpec {
        let d = 2.0
        let hold: K = [(0, 0), (0.5, 1.0), (1.3, 1.0), (d, 0)]
        var c = CurveSet()
        c.set("Squash", scaled(hold, -0.17))
        c.set("PosY", scaled(hold, -0.04))
        c.set("AngleY", scaled(hold, -0.6))
        c.set("AngleZ", scaled(hold, 0.25))
        c.set("SkirtFlare", scaled(hold, 0.5))
        c.set("Hip", scaled(hold, -0.2))
        c.set("Arm.\(out.rawValue)", scaled(hold, 0.3))
        c.set("Arm.\(book.rawValue)", scaled(hold, 0.1))
        both(&c, "Shoulder", scaled(hold, -0.3))
        return MotionSpec("curtsy", d, c, fade: 0.2, expression: "smile")
    }

    static func tiptoe(_ out: CharacterSide, _ book: CharacterSide) -> MotionSpec {
        let d = 2.0
        var c = CurveSet()
        c.set("FootPoint", [(0, 0), (0.25, 1.0), (1.7, 1.0), (d, 0)])
        c.set("FootLift", [(0, 0), (0.25, 0.3), (1.7, 0.3), (d, 0)])
        c.set("PosY", [(0, 0), (0.2, 0.04)] + Keys.oscillate(0.25, 1.7, 0.5, 0.025, base: 0.045) + [(d, 0)])
        c.set("Squash", [(0, 0), (0.25, 0.1), (1.7, 0.08), (d, 0)])
        c.set("Hip", [(0, 0)] + Keys.oscillate(0.2, 1.8, 1.0, 0.18) + [(d, 0)])
        c.set("Arm.\(out.rawValue)", [(0, 0), (0.3, 0.25), (1.6, 0.25), (d, 0)])
        c.set("Arm.\(book.rawValue)", [(0, 0), (0.3, 0.15), (1.6, 0.15), (d, 0)])
        c.set("AngleY", [(0, 0), (0.3, 0.2), (1.6, 0.2), (d, 0)])
        both(&c, "Shoulder", [(0, 0), (0.3, 0.3), (1.6, 0.3), (d, 0)])
        return MotionSpec("tiptoe", d, c, expression: "happy")
    }

    static func foottap(_ out: CharacterSide, _ book: CharacterSide, beat: Double = 0.5) -> MotionSpec {
        var taps: K = [(0, 0)]
        for k in 0..<4 {
            let t = 0.3 + Double(k) * beat
            taps += [(t, 0), (t + 0.12, 0.45), (t + 0.26, 0)]
        }
        let d = 0.3 + 4 * beat + 0.3
        var c = CurveSet()
        c.set("FootLift", taps + [(d, 0)])
        c.set("FootPoint", taps.map { ($0.0, 0.6 * $0.1) } + [(d, 0)])
        c.set("AngleY", Keys.oscillate(0, d, beat * 2, 0.08))
        c.set("Hip", [(0, 0), (0.3, 0.25), (d - 0.3, 0.25), (d, 0)])
        c.set("BodyAngleZ", [(0, 0), (0.3, -0.15), (d - 0.3, -0.15), (d, 0)])
        c.set("AngleZ", Keys.oscillate(0, d, beat * 2, 0.12))
        return MotionSpec("foottap", d, c)
    }

    static func sway(_ out: CharacterSide, _ book: CharacterSide, period: Double = 1.6) -> MotionSpec {
        let d = 2 * period
        let flare: K = [(0, 0)] + Keys.oscillate(0.4, d - 0.3, period / 2, 0.15, base: 0.3) + [(d, 0)]
        var bounce: K = [(0, 0)]
        for k in 0..<4 {
            bounce += Keys.ballistic(0.05 + Double(k) * period / 2, period / 2 - 0.1, 0.035)
        }
        var c = CurveSet()
        c.set("Hip", Keys.oscillate(0, d, period, 0.6))
        c.set("BodyAngleZ", Keys.oscillate(0, d, period, 0.35, phase: Double.pi))
        c.set("AngleZ", Keys.oscillate(0, d, period, 0.35))
        c.set("Arm.\(out.rawValue)", swing(d, period, 0.35, base: 0.1))
        c.set("Arm.\(book.rawValue)", Keys.oscillate(0, d, period, 0.2, phase: Double.pi))
        c.set("Shoulder.\(out.rawValue)", Keys.oscillate(0, d, period, 0.4))
        c.set("Shoulder.\(book.rawValue)", Keys.oscillate(0, d, period, 0.4, phase: Double.pi))
        c.set("PosY", bounce + [(d, 0)])
        c.set("SkirtFlare", flare)
        return MotionSpec("sway", d, c, fade: 0.2, expression: "smile", talk: true)
    }

    static func run(_ out: CharacterSide, _ book: CharacterSide, step: Double = 0.32, steps: Int = 7) -> MotionSpec {
        let d = 0.1 + Double(steps) * step + 0.2
        var pos: K = [(0, 0)], squash: K = [(0, 0)], foot: K = [(0, 0)]
        for k in 0..<steps {
            let t = 0.1 + Double(k) * step
            pos += Keys.ballistic(t, step * 0.8, 0.06)
            squash += [(t, -0.25), (t + step * 0.4, 0.12)]
            if k % 2 == 0 {
                foot += [(t + 0.04, 0), (t + 0.14, 0.6), (t + step - 0.02, 0)]
            }
        }
        pos.append((d, 0))
        squash.append((d, 0))
        foot.append((d, 0))
        let o = out.rawValue, b = book.rawValue
        var c = CurveSet()
        c.set("PosY", pos)
        c.set("Squash", squash)
        c.set("FootLift", foot)
        c.set("Arm.\(o)", swing(d, 2 * step, 0.4, base: 0.15))
        c.set("Forearm.\(o)", swing(d, 2 * step, 0.3, base: -0.45, phase: Double.pi / 2))
        c.set("Arm.\(b)", swing(d, 2 * step, 0.3, base: 0.15, phase: Double.pi))
        c.set("Shoulder.\(o)", Keys.oscillate(0, d, 2 * step, 0.3))
        c.set("Shoulder.\(b)", Keys.oscillate(0, d, 2 * step, 0.3, phase: Double.pi))
        c.set("Hip", Keys.oscillate(0, d, 2 * step, 0.2))
        c.set("Lean", [(0, 0), (0.3, 0.1), (d - 0.3, 0.1), (d, 0)])
        c.set("AngleY", [(0, 0), (0.3, 0.15), (d - 0.3, 0.15), (d, 0)])
        c.set("SkirtFlare", [(0, 0), (0.3, 0.4), (d - 0.3, 0.4), (d, 0)])
        return MotionSpec("run", d, c, fade: 0.15, expression: "determined")
    }

    static func stretch(_ out: CharacterSide, _ book: CharacterSide) -> MotionSpec {
        let d = 2.8
        var c = CurveSet()
        c.set("Squash", [(0, 0), (0.9, 0.2), (1.9, 0.18), (d, 0)])
        c.set("AngleY", [(0, 0), (0.9, 0.45), (1.9, 0.4), (d, 0)])
        c.set("Lean", [(0, 0), (1.0, 0.2), (1.5, -0.2), (2.0, 0), (d, 0)])
        c.set("BodyAngleZ", [(0, 0), (1.0, 0.3), (1.5, -0.3), (2.0, 0), (d, 0)])
        c.set("FootPoint", [(0, 0), (0.9, 0.6), (1.9, 0.6), (d, 0)])
        c.set("PosY", [(0, 0), (0.9, 0.03), (1.9, 0.03), (d, 0)])
        c.set("Arm.\(out.rawValue)", [(0, 0), (0.9, 0.35), (1.9, 0.35), (d, 0)])
        c.set("Arm.\(book.rawValue)", [(0, 0), (0.9, 0.2), (1.9, 0.2), (d, 0)])
        both(&c, "Shoulder", [(0, 0), (0.9, 0.9), (1.9, 0.85), (d, 0)])
        return MotionSpec("stretch", d, c, fade: 0.25, expression: "sleepy")
    }

    static func bow(_ out: CharacterSide, _ book: CharacterSide) -> MotionSpec {
        let d = 1.8
        var c = CurveSet()
        c.set("AngleY", [(0, 0), (0.5, -0.85), (1.2, -0.85), (d, 0)])
        c.set("Squash", [(0, 0), (0.5, -0.15), (1.2, -0.15), (d, 0)])
        both(&c, "Shoulder", [(0, 0), (0.5, -0.2), (1.2, -0.2), (d, 0)])
        return MotionSpec("bow", d, c, fade: 0.2, expression: "smile")
    }

    static func peek(_ out: CharacterSide, _ book: CharacterSide, _ orb: Double) -> MotionSpec {
        let d = 2.4
        let hold: K = [(0, 0), (0.45, 1.0), (1.9, 1.0), (d, 0)]
        var c = CurveSet()
        c.set("Lean", scaled(hold, 0.45 * orb))
        c.set("AngleX", scaled(hold, 0.6 * orb))
        c.set("EyeBallX", scaled(hold, 0.9 * orb))
        c.set("Hip", scaled(hold, -0.3 * orb))
        c.set("AngleZ", scaled(hold, 0.25 * orb))
        c.set("BodyAngleZ", scaled(hold, 0.2 * orb))
        c.set("PosY", [(0, 0), (0.3, 0), (0.42, 0.04), (0.55, 0), (d, 0)])
        return MotionSpec("peek", d, c, fade: 0.2, expression: "surprised")
    }
}
