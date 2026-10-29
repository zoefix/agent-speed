import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// Numbers and words of the performance score shared by every surface (DESIGN 12, *Values and forms*, *Score card*,
/// *Accessibility*).
@MainActor
enum PerfScoreText {
    /// The score words: the number in full form, wrapped for a provisional score, then for resting, then for a set's top
    /// of N agents. Nil while none (`intel.none` replaces the words).
    static func words(_ reading: PerfSetReading, _ l10n: Localizer) -> String? {
        guard reading.form != .none, let shown = reading.shown else { return nil }
        return words(shown: shown, provisional: reading.isProvisional, form: reading.form, count: reading.count, l10n)
    }

    static func words(shown: Int, provisional: Bool, form: PerfValueForm, count: Int, _ l10n: Localizer) -> String {
        var words = PerfFormat.grouped(shown)
        if provisional { words = l10n("a11y.perf.provisional", words) }
        if form == .resting { words = l10n("a11y.perf.idle", words) }
        if count >= 2 { words = l10n("a11y.perf.top", words, count) }
        return words
    }

    /// The tier word of a shown score (the card only; it never colors anything).
    nonisolated static func tierKey(_ shown: Int) -> String {
        if shown >= 2000 { return "perf.tier.veryFast" }
        if shown >= 1300 { return "perf.tier.fast" }
        if shown >= 700 { return "perf.tier.standard" }
        return "perf.tier.slow"
    }

    /// `v / 1000` with two decimals, half away from zero (`1.29`, `0.86`, `12.84`).
    nonisolated static func ratio(_ shown: Int) -> String {
        let hundredths = (max(0, shown) + 5) / 10
        let whole = hundredths / 100
        let fraction = hundredths % 100
        return "\(whole)." + (fraction < 10 ? "0" : "") + "\(fraction)"
    }

    /// Rounded half away from zero.
    nonisolated static func rounded(_ value: Double) -> Int {
        guard value.isFinite else { return 0 }
        return value >= 0 ? Int(value + 0.5) : -Int(-value + 0.5)
    }

    // MARK: - Component values (DESIGN 12, *Score card*, Values)

    /// A component's measure as the card and the History table show it.
    static func value(_ id: PerfComponentId, _ measure: Double, _ l10n: Localizer) -> String {
        switch id {
        case .T, .R: SpeedFormat.speed(measure) + " " + l10n("unit.tokS")
        case .L, .U: duration(ms: measure, l10n)
        case .N: l10n("perf.unit.perMin", perMinute(measure))
        case .S, .H: percent(measure)
        }
    }

    /// Below 1 s whole milliseconds; one decimal second below 10 s and whole seconds up to 60 s; minutes from 60 s (one
    /// decimal below 10 min). Each rounding is taken once from milliseconds, and the unit follows the rounded value.
    static func duration(ms: Double, _ l10n: Localizer) -> String {
        let whole = rounded(ms)
        if whole < 1000 { return l10n("perf.unit.ms", String(whole)) }
        let tenths = rounded(ms / 100)
        if tenths < 100 { return l10n("perf.unit.s", decimal(tenths)) }
        let seconds = rounded(ms / 1000)
        if seconds < 60 { return l10n("perf.unit.s", String(seconds)) }
        let minuteTenths = rounded(ms / 6000)
        if minuteTenths < 100 { return l10n("perf.unit.min", decimal(minuteTenths)) }
        return l10n("perf.unit.min", String(rounded(ms / 60_000)))
    }

    /// One decimal below 10, whole from there.
    nonisolated static func perMinute(_ value: Double) -> String {
        let tenths = rounded(value * 10)
        return tenths < 100 ? decimal(tenths) : String(rounded(value))
    }

    /// A whole percent, half away from zero; a value below 100 % never shows `100%`.
    nonisolated static func percent(_ share: Double) -> String {
        var whole = rounded(share * 100)
        if share < 1, whole >= 100 { whole = 99 }
        return "\(whole)%"
    }

    /// `+8%`, `−12%` (U+2212), `±0%`.
    nonisolated static func signedPercent(_ percent: Int) -> String {
        if percent > 0 { return "+\(percent)%" }
        if percent < 0 { return "\u{2212}\(-percent)%" }
        return "\u{00B1}0%"
    }

    private nonisolated static func decimal(_ tenths: Int) -> String {
        "\(tenths / 10).\(tenths % 10)"
    }
}

extension PerfComponentId {
    /// `perf.comp.*`.
    var titleKey: String {
        switch self {
        case .T: "perf.comp.throughput"
        case .L: "perf.comp.latency"
        case .N: "perf.comp.pace"
        case .R: "perf.comp.reasoning"
        case .H: "perf.comp.cache"
        case .U: "perf.comp.toolTime"
        case .S: "perf.comp.toolSuccess"
        }
    }

    var meaningKey: String { titleKey + ".desc" }

    /// `history.perf.column.*`.
    var columnKey: String {
        switch self {
        case .T: "history.perf.column.throughput"
        case .L: "history.perf.column.latency"
        case .N: "history.perf.column.pace"
        case .R: "history.perf.column.reasoning"
        case .H: "history.perf.column.cache"
        case .U: "history.perf.column.toolTime"
        case .S: "history.perf.column.toolSuccess"
        }
    }

    /// The card's order: speed (throughput, latency, thinking), then efficiency (tool pace, tool time, tool success,
    /// cache hits).
    static let cardOrder: [PerfComponentId] = [.T, .L, .R, .N, .U, .S, .H]
    static let speedGroup: [PerfComponentId] = [.T, .L, .R]
    static let efficiencyGroup: [PerfComponentId] = [.N, .U, .S, .H]
}
