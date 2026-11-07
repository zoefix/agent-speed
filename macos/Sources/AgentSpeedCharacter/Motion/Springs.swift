import Foundation

/// A second-order follower: y + k1 y' + k2 y'' = x + k3 x', k1 = z / (pi f), k2 = 1 / (2 pi f)^2,
/// k3 = r z / (2 pi f); k2 is raised when needed to keep the semi-implicit Euler step stable.
public struct SecondOrder: Sendable {
    let k1: Double
    let k2: Double
    let k3: Double
    var xp = 0.0
    public private(set) var y = 0.0
    var yd = 0.0

    public init(_ f: Double, _ z: Double, _ r: Double, x0: Double = 0) {
        k1 = z / (Double.pi * f)
        let w = 2 * Double.pi * f
        k2 = 1 / (w * w)
        k3 = r * z / (2 * Double.pi * f)
        snap(x0)
    }

    public init(_ tuning: SpringTuning, x0: Double = 0) {
        self.init(tuning.frequency, tuning.damping, tuning.response, x0: x0)
    }

    public mutating func snap(_ x: Double) {
        xp = x
        y = x
        yd = 0
    }

    @discardableResult
    public mutating func update(_ dt: Double, _ x: Double) -> Double {
        guard dt > 0 else { return y }
        let xd = (x - xp) / dt
        xp = x
        let k2 = max(max(self.k2, dt * dt / 2 + dt * k1 / 2), dt * k1)
        y += dt * yd
        yd += dt * (x + k3 * xd - y - k1 * yd) / k2
        return y
    }
}

public struct SpringTuning: Sendable, Equatable {
    public var frequency: Double
    public var damping: Double
    public var response: Double

    public init(_ frequency: Double, _ damping: Double, _ response: Double) {
        self.frequency = frequency
        self.damping = damping
        self.response = response
    }

    /// (prefix or exact id, tuning); the first match wins, nil leaves a parameter raw. "@" matches any id holding it.
    static let table: [(String, SpringTuning?)] = [
        ("SkirtFlare", SpringTuning(2.0, 0.45, 0)),
        ("TailSwish", SpringTuning(2.4, 0.5, 0)), ("Tail", nil), ("WingFlap", SpringTuning(3.0, 0.5, 0)),
        ("EarTwitch", SpringTuning(7.0, 0.4, 0)),
        ("Var.", nil), ("Mouth.", nil), ("EyeOpen.", nil), ("EyeBall", nil), ("Breath", nil), ("Wind", nil),
        ("Spin", nil), ("PosY", nil), ("Squash", nil), ("Hair", nil), ("Skirt", nil),
        ("Lift.", SpringTuning(6.0, 0.85, 0)), ("Bend.", SpringTuning(6.0, 0.85, 0)),
        ("@", SpringTuning(3.2, 0.55, 0)),
        ("Finger.", SpringTuning(5.0, 0.55, 0)), ("Spread.", SpringTuning(3.5, 0.6, 0)),
        ("Wrist.", SpringTuning(3.2, 0.55, 0)),
        ("Arm.", SpringTuning(2.6, 0.55, 0)), ("Forearm.", SpringTuning(2.8, 0.5, 0)),
        ("Shoulder.", SpringTuning(3.0, 0.6, 0)),
        ("Angle", SpringTuning(2.2, 0.72, 0)), ("BodyAngleZ", SpringTuning(1.8, 0.65, 0)),
        ("BodySway", SpringTuning(1.8, 0.65, 0)),
        ("Hip", SpringTuning(1.9, 0.62, 0)), ("Lean", SpringTuning(2.0, 0.6, 0)), ("PosX", SpringTuning(2.4, 0.8, 0)),
        ("Foot", SpringTuning(6.0, 0.7, 0)),
    ]

    /// The spring a parameter rides on, or nil when it stays raw.
    public static func of(_ id: String) -> SpringTuning? {
        for (key, spec) in table {
            if key == "@" ? id.contains("@") : id.hasPrefix(key) { return spec }
        }
        return nil
    }
}
