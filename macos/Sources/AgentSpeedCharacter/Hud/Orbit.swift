import Foundation

/// A Kepler orbit for a floating readout (orbits.py): an ellipse in a horizontal plane through the character, seen
/// slightly from above; faster near her, slower far out; the ellipse breathes, precesses and bobs.
public struct Orbit: Sendable {
    public var a, e, period, phase, omega, height, tilt, precess, seed: Double

    public init(a: Double, e: Double, period: Double, phase: Double, omega: Double, height: Double,
                tilt: Double = 0.16, precess: Double = 0.012, seed: Double = 0) {
        self.a = a
        self.e = e
        self.period = period
        self.phase = phase
        self.omega = omega
        self.height = height
        self.tilt = tilt
        self.precess = precess
        self.seed = seed
    }

    /// Solves Kepler's equation E - e sin E = mean by six Newton steps.
    public static func eccentricAnomaly(_ mean: Double, _ e: Double) -> Double {
        var E = e < 0.8 ? mean : Double.pi
        for _ in 0..<6 {
            E -= (E - e * sin(E) - mean) / (1 - e * cos(E))
        }
        return E
    }

    /// Screen position and depth (-1 farthest behind, 1 nearest in front) at time t around (cx, cy).
    public func at(_ t: Double, _ cx: Double, _ cy: Double) -> (x: Double, y: Double, depth: Double) {
        let s = seed
        let a = self.a * (1 + 0.07 * sin(0.11 * t + s) + 0.04 * sin(0.27 * t + 2.3 * s))
        let mean = 2 * Double.pi * t / period + phase
        let E = Orbit.eccentricAnomaly(CharacterMath.mod(mean, 2 * Double.pi), e)
        let px = a * (cos(E) - e)
        let pz = a * (1 - e * e).squareRoot() * sin(E)
        let w = omega + precess * t
        let x = px * cos(w) - pz * sin(w)
        let z = px * sin(w) + pz * cos(w)
        let bob = 0.035 * self.a * sin(0.37 * t + 1.7 * s) + 0.02 * self.a * sin(0.91 * t + s)
        let y = cy + height + z * tilt + bob
        return (cx + x, y, max(-1.0, min(1.0, z / (self.a * (1 + e)))))
    }

    /// The slow tumble of the text, in radians.
    public func spin(_ t: Double) -> Double {
        0.055 * sin(2 * Double.pi * t / 13.0 + seed) + 0.03 * sin(2 * Double.pi * t / 5.3 + 2 * seed)
    }
}

/// A 2D point that trails its target on springs and bobs on its own phase.
public struct Follow: Sendable {
    var x: SecondOrder
    var y: SecondOrder
    let bob: (amplitude: Double, period: Double)
    let phase: Double
    public var placed = false

    public init(f: Double = 1.6, z: Double = 0.7, bob: (Double, Double) = (5.0, 3.6), phase: Double = 0) {
        x = SecondOrder(f, z, 0)
        y = SecondOrder(f, z, 0)
        self.bob = bob
        self.phase = phase
    }

    public mutating func snap(_ tx: Double, _ ty: Double) {
        x.snap(tx)
        y.snap(ty)
        placed = true
    }

    public mutating func update(_ dt: Double, _ t: Double, _ tx: Double, _ ty: Double) -> (Double, Double) {
        if !placed { snap(tx, ty) }
        let (amp, period) = bob
        return (x.update(dt, tx) + 0.35 * amp * sin(2 * Double.pi * t / (period * 1.7) + phase),
                y.update(dt, ty) + amp * sin(2 * Double.pi * t / period + phase))
    }
}

/// An integer readout whose digits roll (hud.py): sampled five times a second; a changed digit slides up out of view
/// as the new one slides in over 0.12 s.
public struct Odometer: Sendable {
    public struct Digit: Sendable, Equatable {
        public var current: Character?
        public var previous: Character?
        /// 0 when the digit just changed, 1 when its roll is done.
        public var t: Double

        public init(current: Character?, previous: Character?, t: Double) {
            self.current = current
            self.previous = previous
            self.t = t
        }
    }

    public static let hold = 0.2
    public private(set) var value = 0.0
    public private(set) var digits: [Digit] = []
    public private(set) var held = 0
    var since = 1.0

    public init() {
        digits.reserveCapacity(12)
    }

    @discardableResult
    public mutating func update(_ dt: Double, _ target: Double) -> String {
        value += (target - value) * min(1.0, dt * 7.0)
        since += dt
        if since >= Odometer.hold {
            held = Int(CharacterMath.roundHalfEven(value))
            since = 0
        }
        let text = String(held)
        let count = text.count
        while digits.count < count { digits.insert(Digit(current: nil, previous: nil, t: 1), at: 0) }
        while digits.count > count { digits.removeFirst() }
        for (i, ch) in zip(digits.indices, text) {
            if digits[i].current != ch {
                digits[i].previous = digits[i].current
                digits[i].current = ch
                digits[i].t = 0
            }
            digits[i].t = min(1.0, digits[i].t + dt / 0.12)
        }
        return text
    }
}
