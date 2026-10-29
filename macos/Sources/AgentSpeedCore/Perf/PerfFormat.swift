import Foundation

/// The two number forms of the performance score (DESIGN 12, *Number formats*), from the rounded integer, with a comma
/// every three digits in every language.
public enum PerfFormat {
    /// Up to 99,999 grouped; from 100,000 the compact form.
    public static func full(_ value: Int) -> String {
        value >= 100_000 ? compact(value) : grouped(value)
    }

    /// Below 10,000 the full form; from 10,000 three significant digits with `k` or `M`, rounded half away from zero,
    /// the suffix following the rounded value (`99,960` → `100k`, `999,500` → `1.00M`).
    public static func compact(_ value: Int) -> String {
        guard value >= 10_000 else { return grouped(value) }
        let tenths = rounded(value, by: 100)
        if tenths < 1000 { return decimal(tenths, places: 1) + "k" }
        let thousands = rounded(value, by: 1000)
        if thousands < 1000 { return String(thousands) + "k" }
        let hundredths = rounded(value, by: 10_000)
        if hundredths < 1000 { return decimal(hundredths, places: 2) + "M" }
        let millionTenths = rounded(value, by: 100_000)
        if millionTenths < 1000 { return decimal(millionTenths, places: 1) + "M" }
        return String(rounded(value, by: 1_000_000)) + "M"
    }

    /// The widest compact text below 1,000,000, which every compact cell is measured with.
    public static let measureText = "8,888"

    /// A comma every three digits.
    public static func grouped(_ value: Int) -> String {
        let digits = String(value.magnitude)
        var out = ""
        for (index, character) in digits.enumerated() {
            if index > 0, (digits.count - index) % 3 == 0 { out.append(",") }
            out.append(character)
        }
        return value < 0 ? "-" + out : out
    }

    /// `value / divisor` rounded half away from zero, for `value ≥ 0`.
    private static func rounded(_ value: Int, by divisor: Int) -> Int {
        (value + divisor / 2) / divisor
    }

    private static func decimal(_ scaled: Int, places: Int) -> String {
        var text = String(scaled)
        while text.count <= places { text = "0" + text }
        text.insert(".", at: text.index(text.endIndex, offsetBy: -places))
        return text
    }

    // MARK: - Log scale (DESIGN 12, *Log scale*)

    /// `f = clamp(log2(v / 250) / 4, 0, 1)`: 250 empty, 1000 half, 4000 full.
    public static func scale(_ value: Double) -> Double {
        guard value > 0, value.isFinite else { return 0 }
        return min(max(Foundation.log2(value / 250) / 4, 0), 1)
    }
}
