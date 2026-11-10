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

    /// `a11y.orb.models`: one `a11y.orb.modelSpeed` per row the card shows (the model alone while it has no speed),
    /// joined by `list.separator`, then the card's `+N` line when it has one.
    @MainActor
    static func modelsSentence(_ rows: [OrbModelReading], limit: Int, _ l10n: Localizer) -> String? {
        guard !rows.isEmpty else { return nil }
        var parts = rows.prefix(limit).map { row in
            row.speed.map { l10n("a11y.orb.modelSpeed", row.model, String($0)) } ?? row.model
        }
        if rows.count > limit { parts.append(more(rows.count - limit, language: l10n.language)) }
        return l10n("a11y.orb.models", parts.joined(separator: l10n("list.separator")))
    }
}
