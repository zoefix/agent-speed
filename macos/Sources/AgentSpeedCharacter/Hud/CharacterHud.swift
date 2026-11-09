import Foundation

/// A soft round light of the floating readout (hud.py dots): an ellipse with radii (rx, ry) around (x, y). Coverage
/// at normalized radius r: `softness` ≥ 0.99 is a glow, (1 - r)²; otherwise 1 - smoothstep(1 - softness, 1, r).
/// Colors are straight RGB 0…1 with `alpha`; `additive` lights add up, the others blend over.
public struct HudDot: Sendable, Equatable {
    public var x, y, rx, ry: Float
    public var r, g, b, alpha: Float
    public var softness: Float
    public var additive: Bool
}

/// The reference heat palette (green → yellow → orange → red), as 0…255 integer RGB like hud.py.
public enum HeatColor {
    static let stops: [(Double, (Double, Double, Double))] = [
        (0.0, (61, 220, 132)), (0.35, (255, 213, 74)), (0.7, (255, 138, 61)), (1.0, (255, 59, 59)),
    ]
    public static let whiteHot: (Double, Double, Double) = (255, 247, 214)

    public static func rgb(_ h: Double) -> (Double, Double, Double) {
        let h = min(max(h, 0), 1)
        for i in 0..<(stops.count - 1) {
            let (h0, c0) = stops[i]
            let (h1, c1) = stops[i + 1]
            if h <= h1 {
                let k = (h - h0) / (h1 - h0)
                return ((c0.0 + (c1.0 - c0.0) * k).rounded(.towardZero), (c0.1 + (c1.1 - c0.1) * k).rounded(.towardZero),
                        (c0.2 + (c1.2 - c0.2) * k).rounded(.towardZero))
            }
        }
        return stops[stops.count - 1].1
    }
}

/// Where the new-peak pill sits this frame.
public struct HudBody: Sendable, Equatable {
    public var x, y: Double
    public var scale: Double
    public var alpha: Double

    public init(x: Double, y: Double, scale: Double, alpha: Double) {
        self.x = x
        self.y = y
        self.scale = scale
        self.alpha = alpha
    }
}

/// The geometry the readout is laid around, in screen points (y down).
public struct HudLayout: Sendable {
    /// The gauge ring's ellipse at the feet.
    public var ground: (cx: Double, cy: Double, rx: Double, ry: Double)
    /// The agents' orbit around the waist.
    public var waist: (cx: Double, cy: Double, rx: Double, ry: Double)
    /// The idle character's height.
    public var characterHeight: Double
    /// Points per reference unit (the reference film is 1350 px tall: unit = height / 1350).
    public var unit: Double

    public init(ground: (Double, Double, Double, Double), waist: (Double, Double, Double, Double), characterHeight: Double,
                unit: Double) {
        self.ground = ground
        self.waist = waist
        self.characterHeight = characterHeight
        self.unit = unit
    }
}

/// The floating readout's state and lights (hud.py): the gauge ring with its comet, response pulses, agent lights
/// orbiting the waist, embers at redline, the rolling speed and the new peak burst. The cards are placed by
/// `HudWings`; text is the renderer's: it draws the speed digits of `odometer`, the unit, the title (crossfading over
/// `titleProgress`) and the cards.
public struct CharacterHud: Sendable {
    public private(set) var odometer = Odometer()
    /// The title shown and the one it replaces while they crossfade (0 → 1).
    public private(set) var title: String?
    public private(set) var previousTitle: String?
    public private(set) var titleProgress = 1.0
    /// Seconds since the new-peak burst began, nil when none shows.
    public private(set) var peakAge: Double?
    public private(set) var peak: HudBody?
    /// Lights behind and in front of the character, rebuilt every frame (capacity reused).
    public private(set) var back: [HudDot] = []
    public private(set) var front: [HudDot] = []
    /// The speed's width spring (points), so the digits glide when their count changes.
    public private(set) var speedWidth = 0.0

    var rng: Rng
    var peakFollow: Follow
    var width = SecondOrder(2.5, 0.8, 0)
    var comet = 0.0
    var orbitPhase = 0.0
    var pulses: [(t: Double, k: Double)] = []
    var embers: [(x: Double, y: Double, vy: Double, age: Double, life: Double, seed: Double, front: Bool)] = []
    var speed = 0.0
    var heat = 0.0
    var intensity = 0.0
    /// Multiplies the ember rate (0 turns embers off: Soft and Off effects; 1.6 at Max).
    public var emberRate = 1.0
    var time = 0.0

    public init(seed: UInt64 = 5, unit: Double = 1) {
        rng = Rng(seed: seed)
        peakFollow = Follow(f: 2.0, z: 0.6, bob: (3.0 * unit, 2.9), phase: 0.7)
        back.reserveCapacity(512)
        front.reserveCapacity(512)
        pulses.reserveCapacity(16)
        embers.reserveCapacity(256)
    }

    /// A response finished: a ripple runs out from the ring.
    public mutating func pulse(strength: Double = 1) {
        pulses.append((0, strength))
    }

    /// A new peak: the gold pill pops above the speed.
    public mutating func celebratePeak() {
        peakAge = 0
        peakFollow.placed = false
    }

    /// Advances the readout one frame.
    /// - Parameters: `speed` the value to show, `heat` 0…1, `intensity` the director's, `title` the speed title text.
    public mutating func update(_ dt: Double, time: Double, speed: Double, heat: Double, intensity: Double,
                                title: String) {
        comet = CharacterMath.mod(comet + dt / (6.0 - 5.3 * heat), 1.0)
        orbitPhase += dt * (0.25 + 0.9 * heat)
        if title != self.title {
            previousTitle = self.title
            self.title = title
            titleProgress = 0
        }
        titleProgress = min(1.0, titleProgress + dt / 0.35)
        for i in pulses.indices { pulses[i].t += dt }
        pulses.removeAll { $0.t >= 0.9 }
        if let age = peakAge {
            peakAge = age + dt > 2.4 ? nil : age + dt
        }
        self.speed = speed
        self.heat = heat
        self.intensity = intensity
        self.time = time
        odometer.update(dt, speed)
    }

    /// Rebuilds the lights for this frame and moves the new-peak pill toward `peakAt` (above the speed).
    /// `digitAdvance` is the renderer's width of one digit of the speed (points), for the width spring; `agents` are
    /// the agent colors (0…255 RGB).
    public mutating func layout(_ dt: Double, _ l: HudLayout, agents: [(Double, Double, Double)], digitAdvance: Double,
                                peakAt: (x: Double, y: Double)) {
        let u = l.unit
        speedWidth = width.update(dt, digitAdvance * Double(odometer.digits.count))
        if let age = peakAge {
            let (px, py) = peakFollow.update(dt, time, peakAt.x, peakAt.y)
            let pop = age > 0 ? 1 + 0.25 * exp(-6 * age) * sin(14 * age) : 1
            let grow = min(1.0, 0.6 + age * 3)
            peak = HudBody(x: px, y: py, scale: pop * grow, alpha: min(1.0, age / 0.15) * min(1.0, (2.4 - age) / 0.4))
        } else {
            peak = nil
        }

        back.removeAll(keepingCapacity: true)
        front.removeAll(keepingCapacity: true)
        let g = l.ground
        updateEmbers(dt, g, u)
        let c = HeatColor.rgb(heat)
        back.append(dot(g.cx, g.cy - g.ry * 0.1, g.rx * 0.55, g.ry * 0.9, (0.02 * 255, 0.02 * 255, 0.04 * 255), 0.55, 1,
                        additive: false))
        back.append(dot(g.cx, g.cy, g.rx * 1.2, g.ry * 1.8, c, 0.12 + 0.3 * heat, 1))
        ring(g, u)
        pulseRows(g, u)
        satellites(l.waist, u, agents)
        emberRows(u)
        if let age = peakAge, let p = peak {
            for i in 0..<24 {
                let ang = 2 * Double.pi * Double(i) / 24
                let dist = (40 + 160 * min(1.0, age / 0.6)) * u
                front.append(dot(p.x + dist * cos(ang), p.y + dist * 0.5 * sin(ang), 2.2 * u, 2.2 * u,
                                 (255, 200, 80), max(0.0, 0.8 - age), 0.6))
            }
        }
    }

    func dot(_ x: Double, _ y: Double, _ rx: Double, _ ry: Double, _ c: (Double, Double, Double), _ a: Double,
             _ soft: Double, additive: Bool = true) -> HudDot {
        HudDot(x: Float(x), y: Float(y), rx: Float(rx), ry: Float(ry), r: Float(c.0 / 255), g: Float(c.1 / 255),
               b: Float(c.2 / 255), alpha: Float(a), softness: Float(soft), additive: additive)
    }

    mutating func put(_ d: HudDot, front isFront: Bool) {
        if isFront { front.append(d) } else { back.append(d) }
    }

    mutating func ring(_ g: (cx: Double, cy: Double, rx: Double, ry: Double), _ u: Double) {
        let n = 96
        let lit = heat * Double(n)
        for i in 0..<n {
            let a = Double.pi / 2 + 2 * Double.pi * Double(i) / Double(n)
            let s = sin(a)
            let x = g.cx + g.rx * cos(a), y = g.cy + g.ry * s
            let depth = 0.5 + 0.5 * (s + 1) / 2
            let size = (2.2 + 1.6 * (s + 1) / 2) * u
            let cm = CharacterMath.mod(Double(i) / Double(n) - comet + 0.5, 1.0) - 0.5
            let comet = exp(-((cm * 18) * (cm * 18)))
            if Double(i) < lit {
                let leading = i == Int(lit)
                let c = leading ? HeatColor.whiteHot : HeatColor.rgb(Double(i) / Double(n))
                let k = leading ? 1.0 : min(1.0, lit - Double(i))
                put(dot(x, y, size * 3.2, size * 1.7, c, (0.16 + 0.3 * comet) * depth * k, 1), front: s >= 0)
                put(dot(x, y, size, size * 0.62, c, (0.75 + 0.25 * comet) * depth * k, 0.35), front: s >= 0)
            } else {
                put(dot(x, y, size * 0.75, size * 0.47, (255, 255, 255), (0.16 + 0.3 * comet * (0.4 + heat)) * depth, 0.4),
                    front: s >= 0)
            }
        }
    }

    mutating func pulseRows(_ g: (cx: Double, cy: Double, rx: Double, ry: Double), _ u: Double) {
        let c = HeatColor.rgb(heat)
        for p in pulses {
            let k = p.t / 0.9
            let ease = 1 - pow(1 - k, 3)
            let grow = 1 + 0.42 * ease
            for i in 0..<96 {
                let a = 2 * Double.pi * Double(i) / 96
                let s = sin(a)
                put(dot(g.cx + g.rx * grow * cos(a), g.cy + g.ry * grow * s, 1.6 * u, 1.0 * u, c, 0.55 * (1 - k) * p.k, 0.5),
                    front: s >= 0)
            }
        }
    }

    mutating func satellites(_ w: (cx: Double, cy: Double, rx: Double, ry: Double), _ u: Double,
                             _ agents: [(Double, Double, Double)]) {
        for (k, color) in agents.enumerated() {
            let base = orbitPhase + 2 * Double.pi * Double(k) / Double(max(1, agents.count))
            for j in 0..<6 {
                let a = base - Double(j) * 0.07
                let s = sin(a)
                let x = w.cx + w.rx * cos(a)
                let y = w.cy + w.ry * s + -0.14 * w.rx * cos(a)
                let depth = 0.6 + 0.4 * (s + 1) / 2
                if j == 0 {
                    put(dot(x, y, 18 * u * depth, 18 * u * depth, color, 0.45 * depth, 1), front: s >= 0)
                    let core = (color.0 * 0.35 + 0.65 * 255, color.1 * 0.35 + 0.65 * 255, color.2 * 0.35 + 0.65 * 255)
                    put(dot(x, y, 4.5 * u * depth, 4.5 * u * depth, core, depth, 0.3), front: s >= 0)
                } else {
                    let r = (3.6 - 0.45 * Double(j)) * u * depth
                    put(dot(x, y, r, r, color, (0.55 - 0.08 * Double(j)) * depth, 0.6), front: s >= 0)
                }
            }
        }
    }

    mutating func updateEmbers(_ dt: Double, _ g: (cx: Double, cy: Double, rx: Double, ry: Double), _ u: Double) {
        let rate = 70 * max(0.0, (heat - 0.6) / 0.4) * intensity * emberRate
        let n = Int(rate * dt + rng.random())
        for _ in 0..<n {
            let a = rng.uniform(0, 2 * Double.pi)
            let vy = -rng.uniform(60, 160)
            let life = rng.uniform(0.7, 1.5)
            let seed = rng.uniform(0, 6.28)
            embers.append((g.cx + g.rx * cos(a), g.cy + g.ry * sin(a), vy, 0, life, seed, sin(a) > 0))
        }
        var kept = 0
        for i in embers.indices {
            var e = embers[i]
            e.age += dt
            if e.age < e.life {
                e.y += e.vy * u * dt
                e.x += 18 * u * sin(e.age * 6 + e.seed) * dt
                embers[kept] = e
                kept += 1
            }
        }
        embers.removeLast(embers.count - kept)
    }

    mutating func emberRows(_ u: Double) {
        for e in embers {
            let k = e.age / e.life
            put(dot(e.x, e.y, 1.8 * u, 1.8 * u, HeatColor.rgb(0.7 + 0.3 * (1 - k)), 0.8 * (1 - k), 0.6), front: e.front)
        }
    }
}
