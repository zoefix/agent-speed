import Foundation

/// Head gestures with the body joining in (motions/gestures.py). Arm gestures, two-arm combos and face gestures need
/// pose or expression variants; packs without them leave those out (spec/CHARACTERS.md, Motion).
enum Gestures {
    typealias K = [(Double, Double)]

    static let head = ["nod", "shake", "tilt", "lookaround", "giggle", "doubletake"]
    static let face = ["yawn", "hum"]
    static let arm = ["wave", "peace", "fist", "idea", "chin", "hairtuck", "present", "hip", "hug", "raise", "open"]
    static let combos = ["cheer", "hooray", "eureka", "ponder", "cuddle", "proud"]

    static func headGesture(_ name: String) -> MotionSpec {
        var c = CurveSet()
        switch name {
        case "nod":
            c.set("AngleY", [(0, 0), (0.18, 0.4), (0.38, -0.15), (0.58, 0.35), (0.8, -0.05), (1.1, 0)])
            c.set("Squash", [(0, 0), (0.18, -0.12), (0.38, 0.04), (0.58, -0.1), (0.8, 0), (1.1, 0)])
            c.set("EyeOpen.left", [(0, 1), (0.2, 0.7), (0.6, 0.7), (1.1, 1)])
            c.set("EyeOpen.right", [(0, 1), (0.2, 0.7), (0.6, 0.7), (1.1, 1)])
            return MotionSpec("nod", 1.1, c, expression: "smile")
        case "shake":
            c.set("AngleX", [(0, 0)] + Keys.oscillate(0.1, 1.1, 0.42, 0.4) + [(1.3, 0)])
            c.set("BodyAngleZ", [(0, 0)] + Keys.oscillate(0.15, 1.15, 0.42, 0.12) + [(1.3, 0)])
            c.set("AngleY", [(0, 0), (0.2, -0.15), (1.1, -0.15), (1.3, 0)])
            return MotionSpec("shake", 1.3, c)
        case "tilt":
            c.set("AngleZ", [(0, 0), (0.35, 0.7), (1.8, 0.65), (2.2, 0)])
            c.set("AngleX", [(0, 0), (0.35, 0.25), (1.8, 0.25), (2.2, 0)])
            c.set("EyeBallX", [(0, 0), (0.3, 0.5), (1.8, 0.5), (2.2, 0)])
            c.set("Hip", [(0, 0), (0.45, 0.35), (1.8, 0.35), (2.2, 0)])
            c.set("BodyAngleZ", [(0, 0), (0.45, -0.2), (1.8, -0.2), (2.2, 0)])
            return MotionSpec("tilt", 2.2, c, fade: 0.2, expression: "smile")
        case "lookaround":
            c.set("EyeBallX", [(0, 0), (0.2, -0.9), (0.9, -0.9), (1.1, 0.9), (2.2, 0.9), (2.8, 0)])
            c.set("AngleX", [(0, 0), (0.45, -0.55), (1.0, -0.55), (1.6, 0.55), (2.3, 0.5), (2.8, 0)])
            c.set("AngleY", [(0, 0), (0.45, 0.1), (1.3, -0.05), (2.3, 0.1), (2.8, 0)])
            c.set("Hip", [(0, 0), (0.5, -0.25), (1.0, -0.25), (1.7, 0.25), (2.3, 0.2), (2.8, 0)])
            c.set("Lean", [(0, 0), (0.5, -0.15), (1.0, -0.15), (1.7, 0.15), (2.3, 0.1), (2.8, 0)])
            return MotionSpec("lookaround", 2.8, c, fade: 0.2)
        case "giggle":
            c.set("BodyAngleZ", [(0, 0)] + Keys.oscillate(0.1, 1.4, 0.17, 0.12) + [(1.6, 0)])
            c.set("Shoulder.left", [(0, 0)] + Keys.oscillate(0.1, 1.4, 0.34, 0.25, base: 0.3) + [(1.6, 0)])
            c.set("Shoulder.right", [(0, 0)] + Keys.oscillate(0.1, 1.4, 0.34, 0.25, base: 0.3) + [(1.6, 0)])
            c.set("Squash", [(0, 0)] + Keys.oscillate(0.1, 1.4, 0.34, 0.08, base: -0.05) + [(1.6, 0)])
            c.set("AngleY", [(0, 0), (0.2, -0.25), (1.4, -0.2), (1.6, 0)])
            c.set("AngleZ", [(0, 0), (0.2, 0.3), (1.4, 0.3), (1.6, 0)])
            c.set("Breath", [(0, 0), (0.3, 1), (1.6, 0.3)])
            return MotionSpec("giggle", 1.6, c, expression: "happy")
        default:
            c.set("AngleX", [(0, 0), (0.15, 0.35), (0.35, 0.3), (0.5, -0.1), (0.65, 0.6), (1.2, 0.5), (1.5, 0)])
            c.set("AngleY", [(0, 0), (0.6, 0.35), (1.2, 0.3), (1.5, 0)])
            c.set("EyeBallX", [(0, 0), (0.6, 0.8), (1.2, 0.8), (1.5, 0)])
            c.set("PosY", [(0, 0), (0.55, 0), (0.66, 0.07), (0.8, 0), (1.5, 0)])
            c.set("Squash", [(0, 0), (0.5, -0.25), (0.62, 0.3), (0.82, -0.15), (1.0, 0), (1.5, 0)])
            c.set("Shoulder.left", [(0, 0), (0.62, 0.6), (1.2, 0.3), (1.5, 0)])
            c.set("Shoulder.right", [(0, 0), (0.62, 0.6), (1.2, 0.3), (1.5, 0)])
            return MotionSpec("doubletake", 1.5, c, fade: 0.1, expression: "surprised")
        }
    }
}
