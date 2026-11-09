import Foundation

/// Named key lists in insertion order, like the reference's curve dictionaries (assigning an existing name replaces
/// its keys in place).
public struct CurveSet: Sendable {
    public private(set) var names: [String] = []
    public private(set) var keys: [[CurveKey]] = []

    public init() {}

    public subscript(name: String) -> [CurveKey]? {
        get { names.firstIndex(of: name).map { keys[$0] } }
        set {
            if let i = names.firstIndex(of: name) {
                if let newValue { keys[i] = newValue } else {
                    names.remove(at: i)
                    keys.remove(at: i)
                }
            } else if let newValue {
                names.append(name)
                keys.append(newValue)
            }
        }
    }

    mutating func set(_ name: String, _ values: [(Double, Double)]) {
        self[name] = values.map { CurveKey($0.0, $0.1) }
    }
}

/// A motion before its curves are bound to parameter slots.
public struct MotionSpec: Sendable {
    public var name: String
    public var duration: Double
    public var curves: CurveSet
    public var loop = false
    public var fade = 0.12
    public var expression: String?
    public var talk = false
    public var events: [(time: Double, name: String)] = []
    public var travel = 0.0

    public init(_ name: String, _ duration: Double, _ curves: CurveSet, loop: Bool = false, fade: Double = 0.12,
                expression: String? = nil, talk: Bool = false, events: [(time: Double, name: String)] = [],
                travel: Double = 0) {
        self.name = name
        self.duration = duration
        self.curves = curves
        self.loop = loop
        self.fade = fade
        self.expression = expression
        self.talk = talk
        self.events = events.sorted { $0.time != $1.time ? $0.time < $1.time : $0.name < $1.name }
        self.travel = travel
    }

    /// The motion with its curves bound to slots; names `slot` returns nil for are dropped.
    public func bind(_ slot: (String) -> Int?) -> Motion {
        var entries: [Motion.Entry] = []
        for (name, keys) in zip(curves.names, curves.keys) {
            guard let s = slot(name) else { continue }
            entries.append(Motion.Entry(slot: s, name: name, curve: Curve(keys, periodic: loop)))
        }
        return Motion(name: name, duration: duration, loop: loop, fade: fade, expression: expression, talk: talk,
                      events: events, travel: travel, entries: entries)
    }
}

/// Curves over time, bound to parameter slots. One-shot motions may carry an expression, lip-flap (`talk`), timed
/// events and a sideways `travel` that stays after they end.
public struct Motion: Sendable {
    public struct Entry: Sendable {
        public let slot: Int
        public let name: String
        public let curve: Curve
    }

    public let name: String
    public let duration: Double
    public let loop: Bool
    public let fade: Double
    public let expression: String?
    public let talk: Bool
    public let events: [(time: Double, name: String)]
    public let travel: Double
    public let entries: [Entry]

    /// The time a sample at `t` reads: wrapped when looping, held at the end otherwise.
    @inlinable public func localTime(_ t: Double) -> Double {
        loop ? CharacterMath.mod(t, duration) : min(t, duration)
    }
}

/// Key builders of the reference (motions/curves.py).
public enum Keys {
    public static func loop(_ period: Double, _ values: [Double]) -> [(Double, Double)] {
        let n = Double(values.count)
        var out = values.enumerated().map { (period * Double($0.offset) / n, $0.element) }
        out.append((period, values[0]))
        return out
    }

    /// Quarter-period keys of a sine from start to end, settling on `base`.
    public static func oscillate(_ start: Double, _ end: Double, _ period: Double, _ amplitude: Double,
                                 base: Double = 0, phase: Double = 0) -> [(Double, Double)] {
        var keys: [(Double, Double)] = []
        var t = start
        var k = 0
        let step = period / 4
        while t < end - 1e-6 {
            keys.append((t, base + amplitude * sin(phase + Double(k) * Double.pi / 2)))
            t += step
            k += 1
        }
        keys.append((end, base))
        return keys
    }

    /// A jump arc: up from the ground at `start`, `height` at the top, back down after `air` seconds.
    public static func ballistic(_ start: Double, _ air: Double, _ height: Double, samples: Int = 10) -> [(Double, Double)] {
        let n = Double(samples)
        return (0...samples).map { k in
            let kd = Double(k)
            return (start + air * kd / n, height * 4 * (kd / n) * (1 - kd / n))
        }
    }

    /// Keys delayed by dt, holding the first value meanwhile.
    public static func shift(_ keys: [CurveKey], _ dt: Double) -> [CurveKey] {
        if dt <= 0 { return keys }
        return [CurveKey(0, keys[0].v)] + keys.map { CurveKey($0.t + dt, $0.v) }
    }

    /// Plays motions together; a later motion wins a parameter both use.
    public static func merge(_ name: String, _ motions: [MotionSpec], delays: [Double], expression: String?) -> MotionSpec {
        var curves = CurveSet()
        var events: [(time: Double, name: String)] = []
        var end = 0.0
        for (m, dt) in zip(motions, delays) {
            for (n, k) in zip(m.curves.names, m.curves.keys) {
                curves[n] = shift(CurveKey.sorted(k), dt)
            }
            events += m.events.map { ($0.time + dt, $0.name) }
            end = max(end, m.duration + dt)
        }
        return MotionSpec(name, end, curves, expression: expression, talk: motions.contains { $0.talk },
                          events: events, travel: motions.reduce(0) { $0 + $1.travel })
    }
}
