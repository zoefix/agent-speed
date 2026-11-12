import Foundation

/// Arm gestures that swap in a pose variant, and combos of one variant per arm (motions/gestures.py): the old arm
/// turns to meet the new one, they cross-fade, the new arm settles into its pose, holds with a flourish, and the swap
/// runs backwards. The shoulder rises with the arm, the weight shifts to the other leg and the head follows the hand.
extension Gestures {
    struct ArmSpec: Sendable {
        let hold: Double
        let flourish: String
        let expression: String?
        let tiptoe: Bool
    }

    static let armSpecs: [String: ArmSpec] = [
        "wave": ArmSpec(hold: 1.6, flourish: "wave", expression: "smile", tiptoe: true),
        "peace": ArmSpec(hold: 1.3, flourish: "bob", expression: "happy", tiptoe: true),
        "fist": ArmSpec(hold: 1.2, flourish: "pump", expression: "determined", tiptoe: false),
        "idea": ArmSpec(hold: 1.4, flourish: "pop", expression: "surprised", tiptoe: true),
        "chin": ArmSpec(hold: 2.4, flourish: "think", expression: nil, tiptoe: false),
        "hairtuck": ArmSpec(hold: 1.6, flourish: "slow", expression: "smile", tiptoe: true),
        "present": ArmSpec(hold: 1.8, flourish: "offer", expression: "talk", tiptoe: false),
        "hip": ArmSpec(hold: 1.8, flourish: "lean", expression: "determined", tiptoe: false),
        "hug": ArmSpec(hold: 2.4, flourish: "sway", expression: "smile", tiptoe: true),
        "raise": ArmSpec(hold: 1.4, flourish: "pump", expression: "happy", tiptoe: false),
        "open": ArmSpec(hold: 3.4, flourish: "read", expression: nil, tiptoe: false),
        "thumbsup": ArmSpec(hold: 1.4, flourish: "bob", expression: "smile", tiptoe: false),
        "shh": ArmSpec(hold: 1.6, flourish: "slow", expression: "smile", tiptoe: false),
        "cheek": ArmSpec(hold: 1.8, flourish: "slow", expression: "shy", tiptoe: false),
        "salute": ArmSpec(hold: 1.2, flourish: "bob", expression: "smile", tiptoe: false),
        "reach": ArmSpec(hold: 1.6, flourish: "slow", expression: "sleepy", tiptoe: true),
    ]

    struct ComboSpec: Sendable {
        let book: String
        let free: String
        let hold: Double
        let expression: String?
    }

    static let comboSpecs: [String: ComboSpec] = [
        "cheer": ComboSpec(book: "raise", free: "fist", hold: 1.4, expression: "happy"),
        "hooray": ComboSpec(book: "raise", free: "wave", hold: 1.6, expression: "happy"),
        "eureka": ComboSpec(book: "open", free: "idea", hold: 1.8, expression: "surprised"),
        "ponder": ComboSpec(book: "open", free: "chin", hold: 2.4, expression: nil),
        "cuddle": ComboSpec(book: "hug", free: "peace", hold: 1.6, expression: "happy"),
        "proud": ComboSpec(book: "hug", free: "hip", hold: 1.8, expression: "determined"),
    ]

    static func timeline(_ hold: Double) -> [Double] {
        let t1 = 0.22
        let t2 = t1 + 0.1
        let t3 = t2 + 0.32
        let t4 = t3 + hold
        let t5 = t4 + 0.24
        let t6 = t5 + 0.1
        let t7 = t6 + 0.36
        return [t1, t2, t3, t4, t5, t6, t7]
    }

    /// The gesture of an arm variant (`kind: arm`); nil when the variant lacks the parameters it drives.
    static func armGesture(_ meta: RigDocument.Variant, hold: Double, flourish: String, expression: String?,
                           tiptoe: Bool = false) -> MotionSpec? {
        guard let arm = meta.arm, let fore = meta.forearm, let side = meta.side else { return nil }
        let sign = side == "right" ? 1.0 : -1.0
        let t = timeline(hold)
        let (t1, t2, t3, t4, t5, t6, t7) = (t[0], t[1], t[2], t[3], t[4], t[5], t[6])
        var c = CurveSet()
        c.set(meta.param, [(0, 0), (t1, 0), (t2, 1), (t5, 1), (t6, 0), (t7, 0)])
        c.set("Arm.\(side)", [(0, 0), (t7, 0)])
        c.set("Forearm.\(side)", [(0, 0), (t7, 0)])
        for (pid, lead) in (meta.lead ?? [:]).sorted(by: { $0.key < $1.key }) {
            let dip = abs(lead) > 0.2 ? -0.12 * (lead.sign == .minus ? -1.0 : 1.0) : 0.0
            c.set(pid, [(0, 0), (0.08, dip), (t1 - 0.04, lead), (t6 + 0.02, lead), (t7, 0)])
        }
        for pid in [arm, fore] {
            let a = meta.approach?[pid] ?? 0
            c.set(pid, [(0, a), (t1, a), (t3, 0), (t4, 0), (t5 - 0.04, a), (t7, a)])
        }
        func during(_ pid: String, _ keys: K) {
            guard let old = c[pid], old.count >= 6 else { return }
            c[pid] = Array(old.prefix(3)) + keys.map { CurveKey($0.0, $0.1) } + Array(old.suffix(3))
        }

        let lift = meta.front == true ? 0.6 : 0.25
        var body = CurveSet()
        body.set("Shoulder.\(side)", [(0, 0), (t1, 0.3 * lift), (t3, lift), (t4, 0.9 * lift), (t6, 0.3 * lift), (t7, 0)])
        body.set("Hip", [(0, 0), (t3, -0.3 * sign), (t4, -0.3 * sign), (t7, 0)])
        body.set("BodyAngleZ", [(0, 0), (t3, 0.2 * sign), (t4, 0.2 * sign), (t7, 0)])
        if tiptoe {
            body.set("FootPoint", [(0, 0), (t2, 0), (t3, 0.8), (t4, 0.8), (t5, 0), (t7, 0)])
            body.set("FootLift", [(0, 0), (t2, 0), (t3, 0.25), (t4, 0.25), (t5, 0), (t7, 0)])
            body.set("PosY", [(0, 0), (t2, 0), (t3, 0.025), (t4, 0.025), (t5, 0), (t7, 0)])
        }

        var head = CurveSet()
        switch flourish {
        case "wave":
            during(fore, Keys.oscillate(t3, t4, 0.42, 0.45))
            head.set("AngleZ", [(0, 0), (t3, 0.25), (t4, 0.25), (t7, 0)])
            head.set("AngleX", [(0, 0), (t3, -0.3), (t4, -0.3), (t7, 0)])
        case "bob":
            during(arm, Keys.oscillate(t3, t4, 0.5, 0.06))
            head.set("AngleZ", [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)])
        case "pump":
            during(arm, Keys.oscillate(t3, t4, 0.34, 0.12))
            head.set("AngleY", [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)])
            head.set("BodyAngleZ", [(0, 0), (t3, -0.4 * sign), (t4, -0.4 * sign), (t7, 0)])
            body.set("PosY", [(0, 0), (t2, 0)] + Keys.oscillate(t3, t4, 0.68, 0.03, base: 0.03) + [(t5, 0), (t7, 0)])
        case "pop":
            during(fore, [(t3 + 0.12, -0.2), (t3 + 0.3, 0)])
            head.set("AngleY", [(0, 0), (t3, 0.5), (t4, 0.45), (t7, 0)])
            head.set("EyeBallY", [(0, 0), (t3, -0.7), (t4, -0.7), (t7, 0)])
        case "think":
            head.set("AngleZ", [(0, 0), (t3, 0.35), (t4, 0.4), (t7, 0)])
            head.set("AngleY", [(0, 0), (t3, 0.25), (t4, 0.2), (t7, 0)])
            head.set("EyeBallY", [(0, 0), (t3, -0.8), (t4, -0.8), (t7, 0)])
            head.set("EyeBallX", [(0, 0), (t3, 0.5), (t4, 0.6), (t7, 0)])
        case "slow":
            during(fore, [(t3 + hold * 0.5, 0.15)])
            head.set("AngleZ", [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)])
            head.set("EyeBallX", [(0, 0), (t3, 0.4), (t4, 0.4), (t7, 0)])
        case "offer":
            during(arm, [(t3 + hold * 0.5, -0.05)])
            head.set("AngleX", [(0, 0), (t3, 0.45), (t4, 0.45), (t7, 0)])
            head.set("EyeBallX", [(0, 0), (t3, 0.7), (t4, 0.7), (t7, 0)])
            body.set("Lean", [(0, 0), (t3, 0.25 * sign), (t4, 0.25 * sign), (t7, 0)])
        case "lean":
            head.set("BodyAngleZ", [(0, 0), (t3, 0.55 * sign), (t4, 0.55 * sign), (t7, 0)])
            head.set("AngleZ", [(0, 0), (t3, -0.3 * sign), (t4, -0.3 * sign), (t7, 0)])
            body.set("Hip", [(0, 0), (t3, 0.55 * sign), (t4, 0.55 * sign), (t7, 0)])
        case "sway":
            head.set("AngleZ", [(0, 0), (t3, -0.3), (t3 + hold / 2, 0.2), (t4, -0.3), (t7, 0)])
            head.set("BodyAngleZ", Keys.oscillate(0, t7, 1.6, 0.25))
            body.set("Hip", Keys.oscillate(0, t7, 1.6, 0.3, phase: Double.pi))
        case "read":
            head.set("AngleY", [(0, 0), (t3, -0.6), (t4, -0.6), (t7, 0)])
            head.set("EyeBallY", [(0, 0), (t3, 0.6), (t4, 0.6), (t7, 0)])
            head.set("EyeBallX", [(0, 0), (t3, -0.6)] + Keys.oscillate(t3, t4, 1.1, 0.5, base: 0) + [(t7, 0)])
        default:
            break
        }
        if !["wave", "pop", "slow"].contains(flourish) {
            during(fore, Keys.oscillate(t3, t4, 1.3, 0.04))
        }
        for (name, keys) in zip(body.names, body.keys) { c[name] = keys }
        for (name, keys) in zip(head.names, head.keys) { c[name] = keys }
        return MotionSpec(meta.id, t7, c, expression: expression)
    }

    /// Two arm gestures at once: the object arm's, then 0.1 s later the free arm's, which carries the expression.
    static func combo(_ name: String, book: RigDocument.Variant, free: RigDocument.Variant, hold: Double,
                      expression: String?) -> MotionSpec? {
        guard let a = armSpecs[book.id], let b = armSpecs[free.id],
              let first = armGesture(book, hold: hold, flourish: a.flourish, expression: nil),
              let second = armGesture(free, hold: hold, flourish: b.flourish, expression: expression)
        else { return nil }
        return Keys.merge(name, [first, second], delays: [0, 0.1], expression: expression)
    }
}
