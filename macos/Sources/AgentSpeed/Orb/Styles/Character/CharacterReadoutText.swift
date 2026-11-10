import Foundation

/// The words of the 首字 row and the models card (DESIGN 2a.6, Readouts), as drawn and as read by accessibility.
enum CharacterReadoutText {
    /// The speed of a model without a timed response yet.
    static let noSpeed = "–"

    /// A latency as the 首字 row shows it: the number (value token), its unit (secondary token), and both as read.
    struct Latency: Equatable {
        var number: String
        var unit: String
        var spoken: String
    }

    /// Below 1 s whole milliseconds rounded to the nearest 10 (`850 ms`), from 1 s one decimal (`1.2 s`), from 10 s
    /// whole seconds (`14 s`). Each rounding is taken once from the milliseconds, half away from zero, and the unit
    /// follows the rounded value.
    nonisolated static func latency(ms: Double, language: AppLanguage) -> Latency {
        let tens = PerfScoreText.rounded(ms / 10)
        if tens < 100 { return latency(String(tens * 10), unitKey: "perf.unit.ms", language) }
        let tenths = PerfScoreText.rounded(ms / 100)
        if tenths < 100 { return latency("\(tenths / 10).\(tenths % 10)", unitKey: "perf.unit.s", language) }
        return latency(String(PerfScoreText.rounded(ms / 1000)), unitKey: "perf.unit.s", language)
    }

    private nonisolated static func latency(_ number: String, unitKey: String, _ language: AppLanguage) -> Latency {
        let format = Localizer.string(unitKey, language: language)
        let unit = format.replacingOccurrences(of: "%@", with: "").trimmingCharacters(in: .whitespaces)
        return Latency(number: number, unit: unit, spoken: format.replacingOccurrences(of: "%@", with: number))
    }

    /// The models card's last line for the models it has no room for (`+3 個模型`).
    nonisolated static func more(_ count: Int, language: AppLanguage) -> String {
        Localizer.string("character.models.more", language: language).replacingOccurrences(of: "%@", with: String(count))
    }

    /// How many model rows the card shows: 4, or 2 below S = 96 (DESIGN 2a.6, LOD).
    nonisolated static func rowLimit(small: Bool) -> Int { small ? 2 : 4 }

    /// A model's display name: its id without a trailing date, `-YYYYMMDD` or `-YYYY-MM-DD`
    /// (`claude-sonnet-4-5-20250929` → `claude-sonnet-4-5`); an id that is only a date stays as it is.
    nonisolated static func displayName(_ id: String) -> String {
        let bytes = Array(id.utf8)
        let n = bytes.count
        func digits(_ range: Range<Int>) -> Bool { range.allSatisfy { bytes[$0] >= 0x30 && bytes[$0] <= 0x39 } }
        func dash(_ k: Int) -> Bool { bytes[k] == 0x2D }
        if n > 9, dash(n - 9), digits(n - 8..<n) {
            return String(decoding: bytes[..<(n - 9)], as: UTF8.self)
        }
        if n > 11, dash(n - 11), digits(n - 10..<n - 6), dash(n - 6), digits(n - 5..<n - 3), dash(n - 3), digits(n - 2..<n) {
            return String(decoding: bytes[..<(n - 11)], as: UTF8.self)
        }
        return id
    }

    /// `a11y.orb.models`: one `a11y.orb.modelSpeed` per running model, drawn or not, by its full id (the model alone
    /// while it has no speed), joined by `list.separator`.
    @MainActor
    static func modelsSentence(_ rows: [OrbModelReading], _ l10n: Localizer) -> String? {
        guard !rows.isEmpty else { return nil }
        let parts = rows.map { row in row.speed.map { l10n("a11y.orb.modelSpeed", row.model, String($0)) } ?? row.model }
        return l10n("a11y.orb.models", parts.joined(separator: l10n("list.separator")))
    }
}
