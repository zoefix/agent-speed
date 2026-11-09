import AgentSpeedCharacter
import Foundation

/// Lightning on the `character` speed (DESIGN 2a.6): when digits of the readout change, an electric crackle runs
/// around each changed digit, and when more than the last digit changes an arc also jumps over the changed digits.
/// Bolts flicker (a new shape 30 times a second), fade over their life and make the changed digits flash. Strength
/// follows the speed's heat. Paths are built in the speed body's local points (digit cells centered on y = 0) into a
/// reused buffer, so a warm frame allocates nothing.
struct CharacterLightning {
    struct Strike {
        /// Changed digits, counted from the last one (0 = units): `low...high`.
        var low: Int
        var high: Int
        var start: Double
        var life: Double
        var power: Double
        var seed: UInt64
        var bridge: Bool
    }

    enum Strength {
        case off, flashOnly, soft, standard, max
    }

    private(set) var strikes: [Strike] = []
    private(set) var time = 0.0
    /// Path points of the last `bolt` call (local points).
    private(set) var path: [SIMD2<Double>] = []
    private var lastText: [Character] = []
    private var count: UInt64 = 0

    init() {
        strikes.reserveCapacity(8)
        path.reserveCapacity(16)
        lastText.reserveCapacity(8)
    }

    var isActive: Bool { !strikes.isEmpty }

    /// Advances time and starts strikes for the digits of `digits` (the odometer's current characters, most
    /// significant first) that differ from the last call.
    mutating func update(_ dt: Double, digits: [Odometer.Digit], heat: Double, strength: Strength) {
        time += dt
        strikes.removeAll { time - $0.start >= $0.life }
        var low = Int.max, high = -1
        let n = digits.count
        for i in 0..<n {
            let fromRight = n - 1 - i
            let now = digits[i].current
            let before = fromRight < lastText.count ? lastText[lastText.count - 1 - fromRight] : nil
            if let now, before != now, before != nil || !lastText.isEmpty {
                low = min(low, fromRight)
                high = max(high, fromRight)
            }
        }
        lastText.removeAll(keepingCapacity: true)
        for digit in digits { if let c = digit.current { lastText.append(c) } }
        guard high >= 0, strength != .off else { return }
        count &+= 1
        let power = 0.35 + 0.65 * min(max(heat, 0), 1)
        let bridge = high > low && (strength == .standard || strength == .max)
        let life = (high > 0 ? 0.3 : 0.2) * (strength == .soft ? 0.7 : 1)
        if strikes.count >= 8 { strikes.removeFirst() }
        strikes.append(Strike(low: low, high: high, start: time, life: life, power: power,
                              seed: 0x5EED_0000 &+ count &* 0x9E37_79B9, bridge: bridge))
    }

    /// 0...1: how much the digit `fromRight` flashes now.
    func flash(fromRight: Int) -> Double {
        var best = 0.0
        for strike in strikes where (strike.low...strike.high).contains(fromRight) {
            best = max(best, envelope(strike) * strike.power)
        }
        return best
    }

    /// The strike's alpha now: an instant flash, a flicker per 1/30 s step and a fade over its life.
    func envelope(_ strike: Strike) -> Double {
        let age = time - strike.start
        guard age >= 0, age < strike.life else { return 0 }
        var rng = Rng(seed: strike.seed ^ (UInt64(age * 30) &* 0xBF58_476D_1CE4_E5B9))
        let flicker = 0.55 + 0.45 * rng.random()
        return flicker * pow(1 - age / strike.life, 1.4)
    }

    /// How many bolts `strike` draws: one crackle per changed digit (at most three), plus the arc over them.
    func boltCount(_ strike: Strike, strength: Strength) -> Int {
        guard strength != .off, strength != .flashOnly else { return 0 }
        let crackles = min(strike.high - strike.low + 1, 3) + (strength == .max && strike.power > 0.7 ? 1 : 0)
        return crackles + (strike.bridge ? 1 : 0)
    }

    /// Builds bolt `index` of `strike` into `path`, for a readout of `digitCount` digits `advance` wide with glyphs
    /// `cap` tall. Returns false when there is no such bolt.
    mutating func bolt(_ strike: Strike, index: Int, strength: Strength, digitCount: Int, advance: Double,
                       cap: Double) -> Bool {
        path.removeAll(keepingCapacity: true)
        guard index < boltCount(strike, strength: strength), digitCount > 0 else { return false }
        let age = time - strike.start
        var rng = Rng(seed: strike.seed &+ UInt64(index + 1) &* 0x94D0_49BB_1331_11EB
            ^ (UInt64(max(0, age) * 30) &* 0xD6E8_FEB8_6659_FD93))
        func center(_ fromRight: Int) -> Double {
            let i = min(max(digitCount - 1 - fromRight, 0), digitCount - 1)
            return -Double(digitCount) * advance / 2 + advance * (Double(i) + 0.5)
        }
        let crackles = boltCount(strike, strength: strength) - (strike.bridge ? 1 : 0)
        if index < crackles {
            // A crackle hugging one changed digit: an arc of its ellipse with a jagged radius.
            let span = strike.high - strike.low + 1
            let fromRight = strike.low + (index % span)
            let cx = center(fromRight)
            let rx = advance * 0.6, ry = cap * 0.8
            let start = rng.uniform(0, 2 * .pi)
            let sweep = rng.uniform(1.2, 2.2) * (rng.random() < 0.5 ? -1 : 1)
            let points = 9
            for k in 0..<points {
                let f = Double(k) / Double(points - 1)
                let a = start + sweep * f
                let jag = k == 0 || k == points - 1 ? 0 : rng.uniform(-0.2, 0.2)
                let r = 1 + jag
                path.append(SIMD2(cx + cos(a) * rx * r, sin(a) * ry * r))
            }
            return true
        }
        // The arc over the changed digits, from just left of the highest to just right of the lowest.
        let left = center(strike.high) - advance * 0.5
        let right = center(strike.low) + advance * 0.5
        let lift = cap * rng.uniform(0.95, 1.2)
        let points = 12
        for k in 0..<points {
            let f = Double(k) / Double(points - 1)
            let x = left + (right - left) * f
            let y = -cap * 0.2 - 4 * f * (1 - f) * lift
            let jag = k == 0 || k == points - 1 ? 0 : rng.uniform(-0.22, 0.22) * cap
            path.append(SIMD2(x + rng.uniform(-0.08, 0.08) * advance, y + jag))
        }
        return true
    }
}
