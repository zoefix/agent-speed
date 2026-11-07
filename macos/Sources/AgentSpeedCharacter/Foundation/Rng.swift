import Foundation

/// SplitMix64, the random generator every character player shares (spec/CHARACTERS.md, Random numbers), so the same
/// seed and the same speed history give the same motion on every platform.
public struct Rng: Sendable {
    public private(set) var state: UInt64

    public init(seed: UInt64) {
        state = seed
    }

    public mutating func nextUInt64() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }

    /// Uniform in [0, 1): the top 53 bits over 2^53.
    public mutating func random() -> Double {
        Double(nextUInt64() >> 11) / 9_007_199_254_740_992.0
    }

    public mutating func uniform(_ a: Double, _ b: Double) -> Double {
        a + (b - a) * random()
    }

    public mutating func index(_ n: Int) -> Int {
        min(Int(random() * Double(n)), n - 1)
    }

    public mutating func choice<T>(_ items: [T]) -> T {
        items[index(items.count)]
    }

    /// Fisher-Yates from the last item down.
    public mutating func shuffle<T>(_ items: inout [T]) {
        var i = items.count - 1
        while i > 0 {
            let j = index(i + 1)
            items.swapAt(i, j)
            i -= 1
        }
    }

    public mutating func sample<T>(_ items: [T], _ k: Int) -> [T] {
        var copy = items
        shuffle(&copy)
        return Array(copy.prefix(k))
    }
}
