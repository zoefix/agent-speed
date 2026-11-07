import AgentSpeedCharacter
import Foundation

/// Petals carried by the wind from boost up and thrown out in puffs at take-offs and pose swaps
/// (characters/reference/effects.py). Lengths are in reference units (`unit` points each); the pool is reused.
struct CharacterPetals {
    struct Petal {
        var x, y, vx, vy, rotation, spin, size, age, life, phase: Double
        var front: Bool
    }

    private(set) var items: [Petal] = []
    private var rng: Rng
    private var carry = 0.0
    static let limit = 160

    init(seed: UInt64 = 11) {
        rng = Rng(seed: seed)
        items.reserveCapacity(Self.limit)
    }

    mutating func clear() {
        items.removeAll(keepingCapacity: true)
        carry = 0
    }

    /// Advances the drift; `area` is where wind petals start (x0, y0, x1, y1), `rate` multiplies the emission.
    mutating func update(_ dt: Double, intensity: Double, area: (Double, Double, Double, Double), unit: Double,
                         rate: Double) {
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
            items.append(Petal(x: x, y: y, vx: vx, vy: vy, rotation: rotation, spin: spin, size: size, age: 0, life: life,
                               phase: phase, front: front))
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

    /// A puff of petals thrown out from (x, y).
    mutating func burst(x: Double, y: Double, unit: Double, count: Int = 14) {
        for _ in 0..<count where items.count < Self.limit {
            let a = rng.uniform(0, 2 * Double.pi)
            let v = rng.uniform(0.4, 1.0) * 260 * unit
            let rotation = rng.uniform(0, 360), spin = rng.uniform(-300, 300)
            let size = rng.uniform(14, 26) * unit, life = rng.uniform(0.9, 1.6)
            let front = rng.random() < 0.6, phase = rng.uniform(0, 6.28)
            items.append(Petal(x: x, y: y, vx: cos(a) * v, vy: sin(a) * v * 0.6 - 60 * unit, rotation: rotation, spin: spin,
                               size: size, age: 0, life: life, phase: phase, front: front))
        }
    }

    /// Sprites of the petals on one side of the character, sampling `uv` (the flower in the atlas).
    func sprites(front: Bool, uv: SIMD4<Float>, alpha: Double) -> LazyMapSequence<LazyFilterSequence<[Petal]>, CharacterSprite> {
        items.lazy.filter { $0.front == front }.map { p in
            let k = p.age / p.life
            let a = min(1, k / 0.15) * min(1, (1 - k) / 0.3) * alpha
            let squash = 0.55 + 0.45 * abs(cos(p.rotation * .pi / 180 * 1.7))
            return CharacterSprite(geometry: SIMD4(Float(p.x), Float(p.y), Float(p.size / 2), Float(p.size * squash / 2)),
                                   uv: uv, extra: SIMD4(Float(p.rotation * .pi / 180), Float(a), 0, 0))
        }
    }
}
