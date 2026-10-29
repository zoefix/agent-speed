import Foundation

/// The 64 latency buckets of the performance history (METRICS 6.11 *Latency buckets*): bucket `k` holds
/// `[100 · 2^(k/4), 100 · 2^((k+1)/4))` ms, with integer edges so every platform buckets alike.
public enum PerfLatency {
    public static let bucketCount = 64

    /// `E_0 … E_64 = ⌈100 · 2^(j/4)⌉`.
    public static let edges: [Int64] = [
        100, 119, 142, 169, 200, 238, 283, 337,
        400, 476, 566, 673, 800, 952, 1132, 1346,
        1600, 1903, 2263, 2691, 3200, 3806, 4526, 5382,
        6400, 7611, 9051, 10764, 12800, 15222, 18102, 21527,
        25600, 30444, 36204, 43054, 51200, 60888, 72408, 86108,
        102400, 121775, 144816, 172216, 204800, 243550, 289631, 344432,
        409600, 487100, 579262, 688863, 819200, 974199, 1158524, 1377725,
        1638400, 1948397, 2317048, 2755450, 3276800, 3896794, 4634096, 5510899,
        6553600,
    ]

    /// The number of edges `E_1 … E_63` the latency reaches.
    public static func bucket(_ ms: Int64) -> Int {
        var low = 1
        var high = bucketCount
        while low < high {
            let mid = (low + high) / 2
            if ms >= edges[mid] { low = mid + 1 } else { high = mid }
        }
        return low - 1
    }

    /// The geometric centre `100 · 2^((k + 0.5) / 4)` of bucket `k`.
    public static func centre(_ k: Int) -> Double {
        100 * Foundation.pow(2, Double(2 * k + 1) / 8)
    }

    /// `firstMs − startMs` of a response that has both, with `firstMs ≥ startMs`; nil otherwise.
    static func latency(startMs: Int64?, firstMs: Int64?) -> Int64? {
        guard let startMs, let firstMs, firstMs >= startMs else { return nil }
        let (value, overflow) = firstMs.subtractingReportingOverflow(startMs)
        return overflow ? .max : value
    }
}
