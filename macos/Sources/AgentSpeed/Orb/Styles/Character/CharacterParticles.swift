import AgentSpeedCharacter
import Foundation
import simd

/// A character's particles (spec/CHARACTERS.md, Particles; characters/reference/effects.py): Claude's petals carried by
/// the wind from boost up, or the kind her manifest names drifting around her at a rate that follows her intensity.
/// Every kind is thrown out in puffs at take-offs and pose swaps. Lengths are in reference units (`unit` points each);
/// the pool is reused.
struct CharacterParticles {
    enum Look { case petal, ring, star, wisp, chip, feather, dot, glyph }
    enum Origin { case body, top, head, feet }

    struct Kind {
        let look: Look
        /// Per second: base + gain × max(0, intensity − floor)^power; an inverse kind emits base × (1 − intensity)².
        let rate: (base: Double, gain: Double, floor: Double, power: Double)
        var inverse = false
        let origin: Origin
        let vx: (Double, Double), vy: (Double, Double)
        /// Extra vx per unit of wind; the wind grows with intensity.
        let wind: Double
        let wobble: (amplitude: Double, frequency: Double)
        let size: (Double, Double)
        /// Size change over the life, as a fraction of the starting size.
        let grow: Double
        let life: (Double, Double)
        let spin: (Double, Double)
        var glyphs: [String] = []
        var glow = false

        var additive: Bool { glow || look == .star || look == .wisp }
    }

    struct Particle {
        var x, y, vx, vy, rotation, spin, size, age, life, phase: Double
        var front: Bool
        var color: SIMD3<Float>
        var glyph = 0
        var burst = false
    }

    static let kinds: [String: Kind] = [
        "bubbles": Kind(look: .ring, rate: (3, 16, 0, 1), origin: .body, vx: (-15, 15), vy: (-90, -40), wind: 20,
                        wobble: (14, 4), size: (6, 14), grow: 0.4, life: (1.6, 2.8), spin: (0, 0)),
        "sparkles": Kind(look: .star, rate: (4, 26, 0, 1.5), origin: .body, vx: (-12, 12), vy: (-12, 12), wind: 0,
                         wobble: (0, 0), size: (8, 16), grow: 0, life: (0.8, 1.6), spin: (-40, 40)),
        "stars": Kind(look: .star, rate: (3, 18, 0, 1.4), origin: .body, vx: (-10, 10), vy: (-30, -10), wind: 10,
                      wobble: (6, 2), size: (10, 18), grow: 0, life: (1.4, 2.4), spin: (-90, 90)),
        "notes": Kind(look: .glyph, rate: (3, 14, 0, 1), origin: .body, vx: (-10, 10), vy: (-60, -30), wind: 30,
                      wobble: (20, 3), size: (18, 28), grow: 0, life: (1.8, 2.8), spin: (0, 0), glyphs: ["♪", "♫"]),
        "embers": Kind(look: .dot, rate: (0, 30, 0.2, 1.3), origin: .feet, vx: (-20, 20), vy: (-160, -80), wind: 25,
                       wobble: (18, 6), size: (2, 4), grow: -0.5, life: (0.7, 1.5), spin: (0, 0), glow: true),
        "foxfire": Kind(look: .wisp, rate: (2, 10, 0, 1), origin: .body, vx: (-8, 8), vy: (-45, -20), wind: 8,
                        wobble: (16, 2.5), size: (7, 12), grow: -0.3, life: (1.4, 2.4), spin: (0, 0)),
        "confetti": Kind(look: .chip, rate: (0, 40, 0.4, 1.5), origin: .top, vx: (-40, 40), vy: (60, 140), wind: 60,
                         wobble: (25, 5), size: (5, 9), grow: 0, life: (1.6, 2.6), spin: (-400, 400)),
        "zzz": Kind(look: .glyph, rate: (1.2, 0, 0, 1), inverse: true, origin: .head, vx: (10, 22), vy: (-30, -18),
                    wind: 0, wobble: (6, 1.5), size: (14, 18), grow: 0.7, life: (2.2, 3), spin: (-10, 10),
                    glyphs: ["Z", "z"]),
        "pages": Kind(look: .chip, rate: (0, 20, 0.2, 1.4), origin: .body, vx: (120, 220), vy: (-40, 30), wind: 0,
                      wobble: (18, 5), size: (10, 16), grow: 0, life: (1.3, 2.4), spin: (-260, 260)),
        "blossoms": Kind(look: .chip, rate: (2, 24, 0, 1.5), origin: .body, vx: (100, 200), vy: (-30, 30), wind: 0,
                         wobble: (18, 5), size: (8, 13), grow: 0, life: (1.4, 2.6), spin: (-200, 200)),
        "feathers": Kind(look: .feather, rate: (3, 18, 0, 1.4), origin: .top, vx: (-20, 20), vy: (25, 50), wind: 40,
                         wobble: (30, 1.8), size: (14, 24), grow: 0, life: (2, 3.2), spin: (-60, 60)),
        "snow": Kind(look: .dot, rate: (6, 20, 0, 1), origin: .top, vx: (-10, 10), vy: (30, 60), wind: 30,
                     wobble: (12, 2), size: (3, 6), grow: 0, life: (2.5, 4), spin: (0, 0)),
    ]

    /// Colors of a kind for a character of `accent`: tints of her accent, or the kind's own colors.
    static func palette(_ name: String, accent: SIMD3<Float>) -> [SIMD3<Float>] {
        let light = 0.45 * accent + SIMD3(repeating: 0.55)
        switch name {
        case "bubbles": return [SIMD3(0.75, 0.9, 1)]
        case "sparkles": return [light, SIMD3(1, 0.97, 0.85)]
        case "stars": return [light, SIMD3(1, 0.9, 0.55)]
        case "notes", "zzz": return [light]
        case "embers": return [SIMD3(1, 0.55, 0.2), SIMD3(1, 0.8, 0.35)]
        case "foxfire": return [SIMD3(0.45, 0.72, 1)]
        case "confetti": return [accent, SIMD3(1, 0.48, 0.72), SIMD3(1, 0.83, 0.35), SIMD3(0.48, 0.88, 0.76)]
        case "pages": return [SIMD3(0.98, 0.97, 0.93)]
        case "blossoms": return [SIMD3(1, 0.86, 0.92), SIMD3(0.86, 0.9, 1), SIMD3(1, 1, 1)]
        case "feathers": return [accent, light]
        case "snow": return [SIMD3(1, 1, 1)]
        default: return [accent]
        }
    }

    /// `#RRGGBB` as 0...1 RGB; Claude's orange when it does not parse.
    static func rgb(_ hex: String) -> SIMD3<Float> {
        let digits = hex.hasPrefix("#") ? hex.dropFirst() : Substring(hex)
        guard digits.count == 6, let v = UInt32(digits, radix: 16) else { return SIMD3(0.85, 0.47, 0.34) }
        return SIMD3(Float((v >> 16) & 0xFF), Float((v >> 8) & 0xFF), Float(v & 0xFF)) / 255
    }

    private(set) var items: [Particle] = []
    /// The kind's name, nil for petals.
    private(set) var name: String?
    private(set) var kind: Kind?
    private var colors: [SIMD3<Float>] = [SIMD3(0.85, 0.47, 0.34)]
    private var packID: String?
    private var rng: Rng
    private var carry = 0.0
    static let limit = 160

    init(seed: UInt64 = 11) {
        rng = Rng(seed: seed)
        items.reserveCapacity(Self.limit)
    }

    var isPetals: Bool { kind == nil }

    /// Bursts and wind-blown petals move fast and need a fluid frame rate; the slow ambient drift does not.
    var isLively: Bool { isPetals ? !items.isEmpty : items.contains { $0.burst } }

    /// Takes the kind of the pack's manifest (`motion.particles`, petals by default); a pack without the petal sprite
    /// gets sparkles instead of petals.
    mutating func use(_ pack: CharacterPack, hasSprite: Bool) {
        guard pack.id != packID else { return }
        select(pack.manifest.motion?.particles ?? "petals", accent: pack.manifest.accent, hasSprite: hasSprite)
        packID = pack.id
    }

    /// Switches to the kind named `wanted` (unknown names are petals) and clears the pool.
    mutating func select(_ wanted: String, accent: String, hasSprite: Bool) {
        clear()
        var wanted = Self.kinds[wanted] == nil ? "petals" : wanted
        if wanted == "petals", !hasSprite { wanted = "sparkles" }
        kind = Self.kinds[wanted]
        name = kind == nil ? nil : wanted
        colors = Self.palette(wanted, accent: Self.rgb(accent))
        packID = nil
    }

    mutating func clear() {
        items.removeAll(keepingCapacity: true)
        carry = 0
    }

    /// Advances the drift. `area` is the stage the particles start in (x0, y0, x1, y1); `rate` multiplies the emission
    /// and is 0 while particles are off.
    mutating func update(_ dt: Double, intensity: Double, area: (Double, Double, Double, Double), unit: Double,
                         rate: Double) {
        guard let kind else {
            updatePetals(dt, intensity: intensity, area: area, unit: unit, rate: rate)
            return
        }
        let r = kind.rate
        let perSecond = kind.inverse ? r.base * pow(1 - min(max(intensity, 0), 1), 2)
            : r.base + r.gain * pow(max(0, intensity - r.floor), r.power)
        carry += perSecond * rate * dt
        while carry >= 1 {
            carry -= 1
            let p = spawn(kind, intensity: intensity, area: area, unit: unit)
            if items.count < Self.limit { items.append(p) }
        }
        let amplitude = kind.wobble.amplitude * unit, frequency = kind.wobble.frequency
        var kept = 0
        for i in items.indices {
            var p = items[i]
            p.age += dt
            guard p.age < p.life else { continue }
            p.x += (p.vx + amplitude * sin(p.age * frequency + p.phase)) * dt
            p.y += p.vy * dt
            p.rotation += p.spin * dt
            items[kept] = p
            kept += 1
        }
        items.removeLast(items.count - kept)
    }

    private mutating func spawn(_ kind: Kind, intensity: Double, area: (Double, Double, Double, Double), unit: Double,
                                at point: (Double, Double)? = nil) -> Particle {
        let (x0, y0, x1, y1) = area
        let w = x1 - x0, h = y1 - y0
        let x, y: Double
        if let point {
            x = point.0
            y = point.1
        } else {
            switch kind.origin {
            case .top:
                x = rng.uniform(x0, x1)
                y = y0 - 0.05 * h + rng.uniform(0, 0.25 * h)
            case .head:
                x = rng.uniform(x0 + 0.35 * w, x0 + 0.65 * w)
                y = rng.uniform(y0, y0 + 0.2 * h)
            case .feet:
                x = rng.uniform(x0 + 0.25 * w, x0 + 0.75 * w)
                y = y1 - rng.uniform(0, 0.1 * h)
            case .body:
                x = rng.uniform(x0 + 0.1 * w, x0 + 0.9 * w)
                y = rng.uniform(y0 + 0.1 * h, y0 + 0.8 * h)
            }
        }
        let vx = (rng.uniform(kind.vx.0, kind.vx.1) + kind.wind * (0.6 + intensity)) * unit
        let vy = rng.uniform(kind.vy.0, kind.vy.1) * unit
        let size = rng.uniform(kind.size.0, kind.size.1) * unit
        let life = rng.uniform(kind.life.0, kind.life.1)
        let spin = rng.uniform(kind.spin.0, kind.spin.1)
        let rotation = rng.uniform(0, 360), phase = rng.uniform(0, 6.28)
        let front = rng.random() < 0.55
        let color = colors[rng.index(colors.count)]
        let glyph = kind.glyphs.isEmpty ? 0 : rng.index(kind.glyphs.count)
        return Particle(x: x, y: y, vx: vx, vy: vy, rotation: rotation, spin: spin, size: size, age: 0, life: life,
                        phase: phase, front: front, color: color, glyph: glyph)
    }

    /// A puff thrown out from (x, y).
    mutating func burst(x: Double, y: Double, unit: Double, count: Int = 14) {
        guard let kind else {
            burstPetals(x: x, y: y, unit: unit, count: count)
            return
        }
        for _ in 0..<count where items.count < Self.limit {
            var p = spawn(kind, intensity: 0.6, area: (x, y, x, y), unit: unit, at: (x, y))
            let a = rng.uniform(0, 2 * Double.pi)
            let v = rng.uniform(0.3, 0.8) * 260 * unit
            p.vx = cos(a) * v
            p.vy = sin(a) * v * 0.6 - 40 * unit
            p.life *= 0.7
            p.burst = true
            items.append(p)
        }
    }

    func alpha(_ p: Particle) -> Double {
        let k = p.age / p.life
        var a = min(1, k / 0.15) * min(1, (1 - k) / 0.3)
        if kind?.look == .star {
            let s = sin(p.age * 9 + p.phase)
            a *= 0.55 + 0.45 * s * s
        }
        return a
    }

    func size(_ p: Particle) -> Double {
        p.size * (1 + (kind?.grow ?? 0) * p.age / p.life)
    }

    /// The dots of the particles on one side of the character, at `alpha` overall.
    func dots(front: Bool, alpha opacity: Double, into out: inout [CharacterDot]) {
        guard let kind, kind.look != .glyph else { return }
        for p in items where p.front == front {
            let a = Float(alpha(p) * opacity), s = Float(size(p)), x = Float(p.x), y = Float(p.y)
            let c = p.color
            func dot(_ x: Float, _ y: Float, _ rx: Float, _ ry: Float, _ rgb: SIMD3<Float>, _ alpha: Float,
                     _ softness: Float) {
                out.append(CharacterDot(x: x, y: y, rx: rx, ry: ry, color: SIMD4(rgb, alpha), softness: softness))
            }
            switch kind.look {
            case .ring:
                dot(x, y, s, s, c, 0.22 * a, 0.25)
                dot(x - 0.35 * s, y - 0.35 * s, 0.25 * s, 0.25 * s, SIMD3(1, 1, 1), 0.8 * a, 0.5)
            case .star:
                dot(x, y, s, 0.16 * s, c, a, 0.6)
                dot(x, y, 0.16 * s, s, c, a, 0.6)
                dot(x, y, 0.6 * s, 0.6 * s, c, 0.35 * a, 1)
            case .wisp:
                dot(x, y, s, 1.7 * s, c, 0.45 * a, 1)
                dot(x, y + 0.2 * s, 0.4 * s, 0.6 * s, SIMD3(0.9, 0.97, 1), 0.8 * a, 0.6)
            case .chip:
                let flip = Float(abs(cos(p.rotation * .pi / 180)))
                dot(x, y, 0.5 * s, 0.5 * s * (0.15 + 0.85 * flip), c, a, 0.15)
            case .feather:
                dot(x, y, 0.3 * s, s, c, 0.85 * a, 0.5)
            case .dot:
                dot(x, y, s, s, c, a, 0.7)
            case .petal, .glyph:
                break
            }
        }
    }

    /// The glyph particles (notes, zzz) on one side: the glyph, its center, size in points, angle (radians) and alpha.
    func glyphs(front: Bool) -> [(glyph: String, x: Double, y: Double, size: Double, angle: Double, alpha: Double,
                                  color: SIMD3<Float>)] {
        guard let kind, kind.look == .glyph else { return [] }
        return items.filter { $0.front == front }.map {
            (kind.glyphs[$0.glyph], $0.x, $0.y, size($0), $0.rotation * .pi / 180, alpha($0), $0.color)
        }
    }

    /// The largest size a glyph of the kind reaches, in points, so one texture serves every particle.
    func glyphCeiling(unit: Double) -> Double {
        guard let kind else { return 0 }
        return kind.size.1 * max(1, 1 + kind.grow) * unit
    }

    // MARK: Petals (Claude)

    private mutating func updatePetals(_ dt: Double, intensity: Double, area: (Double, Double, Double, Double),
                                       unit: Double, rate: Double) {
        carry += 34 * pow(max(0, intensity - 0.15), 1.6) * rate * dt
        while carry >= 1 {
            carry -= 1
            guard items.count < Self.limit else { continue }
            let (x0, y0, x1, y1) = area
            let w = x1 - x0, h = y1 - y0
            let fromInside = rng.random() < 0.6
            let x = fromInside ? rng.uniform(x0, x1) : x0 - 0.15 * w
            let y = rng.uniform(y0 + 0.05 * h, y0 + 0.75 * h)
            let vx = rng.uniform(120, 260) * (0.6 + intensity) * unit
            let vy = rng.uniform(-40, 30) * unit
            let rotation = rng.uniform(0, 360), spin = rng.uniform(-260, 260)
            let size = rng.uniform(16, 30) * unit, life = rng.uniform(1.3, 2.4)
            let front = rng.random() < 0.55, phase = rng.uniform(0, 6.28)
            items.append(Particle(x: x, y: y, vx: vx, vy: vy, rotation: rotation, spin: spin, size: size, age: 0,
                                  life: life, phase: phase, front: front, color: colors[0]))
        }
        var kept = 0
        for i in items.indices {
            var p = items[i]
            p.age += dt
            guard p.age < p.life else { continue }
            p.x += p.vx * dt
            p.y += (p.vy + 18 * unit * sin(p.age * 5 + p.phase)) * dt
            p.rotation += p.spin * dt
            items[kept] = p
            kept += 1
        }
        items.removeLast(items.count - kept)
    }

    private mutating func burstPetals(x: Double, y: Double, unit: Double, count: Int) {
        for _ in 0..<count where items.count < Self.limit {
            let a = rng.uniform(0, 2 * Double.pi)
            let v = rng.uniform(0.4, 1.0) * 260 * unit
            let rotation = rng.uniform(0, 360), spin = rng.uniform(-300, 300)
            let size = rng.uniform(14, 26) * unit, life = rng.uniform(0.9, 1.6)
            let front = rng.random() < 0.6, phase = rng.uniform(0, 6.28)
            items.append(Particle(x: x, y: y, vx: cos(a) * v, vy: sin(a) * v * 0.6 - 60 * unit, rotation: rotation,
                                  spin: spin, size: size, age: 0, life: life, phase: phase, front: front,
                                  color: colors[0], burst: true))
        }
    }

    /// Sprites of the petals on one side of the character, sampling `uv` (the flower in the atlas).
    func sprites(front: Bool, uv: SIMD4<Float>, alpha: Double) -> LazyMapSequence<LazyFilterSequence<[Particle]>, CharacterSprite> {
        items.lazy.filter { $0.front == front }.map { p in
            let k = p.age / p.life
            let a = min(1, k / 0.15) * min(1, (1 - k) / 0.3) * alpha
            let squash = 0.55 + 0.45 * abs(cos(p.rotation * .pi / 180 * 1.7))
            return CharacterSprite(geometry: SIMD4(Float(p.x), Float(p.y), Float(p.size / 2), Float(p.size * squash / 2)),
                                   uv: uv, extra: SIMD4(Float(p.rotation * .pi / 180), Float(a), 0, 0))
        }
    }
}
