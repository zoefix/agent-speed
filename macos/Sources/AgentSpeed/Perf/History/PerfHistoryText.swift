import AgentSpeedCore
import Foundation

/// The words of 性能表現 (DESIGN 12, *Usage History: performance*, *Accessibility*).
@MainActor
enum PerfHistoryText {
    static let absent = "\u{2013}"
    /// A day enters the trend, and a row gets a score, from this many timed requests (METRICS 6.11).
    static let minimum = PerfPooled.minimumResponses

    static func model(_ row: PerfHistoryRow, _ l10n: Localizer) -> String {
        row.model.isEmpty ? l10n("cost.noModelName") : row.model
    }

    static func source(_ row: PerfHistoryRow, _ l10n: Localizer) -> String { l10n(row.source.titleKey) }

    static func subject(_ row: PerfHistoryRow, _ l10n: Localizer) -> String {
        model(row, l10n) + " · " + source(row, l10n)
    }

    /// A count key (`%d`) filled with the grouped number, as every performance count is written.
    static func counted(_ key: String, _ count: Int, _ l10n: Localizer, _ more: Int...) -> String {
        let template = l10n(key).replacingOccurrences(of: "%d", with: "%@")
        let values = ([count] + more).map { PerfFormat.grouped($0) as NSString }
        return String(format: template, arguments: values)
    }

    static func sampleHint(_ n: Int, _ l10n: Localizer) -> String {
        counted("history.perf.sample.hint", n, l10n, minimum)
    }

    static func requests(_ n: Int, _ l10n: Localizer) -> String { counted("history.perf.requests", n, l10n) }

    // MARK: - Cells

    static func cell(_ row: PerfHistoryRow, _ column: PerfHistoryColumn, _ l10n: Localizer) -> String {
        switch column {
        case .model: model(row, l10n)
        case .score: row.shown.map(PerfFormat.full) ?? absent
        case .usual: row.usualShown.map(PerfFormat.full) ?? absent
        case .timed: PerfFormat.grouped(row.n)
        case .component(let id): row.measure(id).map { PerfScoreText.value(id, $0, l10n) } ?? absent
        case .parts: parts(row, l10n)
        }
    }

    /// A cell's tooltip: why a value is missing, or a component's sub-score.
    static func tooltip(_ row: PerfHistoryRow, _ column: PerfHistoryColumn, _ l10n: Localizer) -> String? {
        switch column {
        case .model: subject(row, l10n)
        case .score: row.shown == nil ? sampleHint(row.n, l10n) : nil
        case .usual: row.usual == nil ? usualNone(l10n) : nil
        case .timed: nil
        case .component(let id): componentHint(row, id, l10n)
        case .parts: nil
        }
    }

    static func usualNone(_ l10n: Localizer) -> String { counted("perf.usual.none.hint", minimum, l10n) }

    static func componentHint(_ row: PerfHistoryRow, _ id: PerfComponentId, _ l10n: Localizer) -> String? {
        guard let component = row.component(id) else { return nil }
        if let sub = component.subScore, component.isPresent {
            return l10n(id.titleKey) + " · " + PerfFormat.full(perfRoundHalfAway(sub))
        }
        return component.reason == .notRecorded
            ? l10n("perf.reason.notRecorded.hint", source(row, l10n))
            : l10n("perf.reason.fewSamples.hint", PerfFormula.minimumSamples)
    }

    /// One bar of the parts column: name, value and sub-score.
    static func partHint(_ row: PerfHistoryRow, _ id: PerfComponentId, _ l10n: Localizer) -> String {
        guard let component = row.component(id), component.isPresent, let measure = component.measure,
              let sub = component.subScore else {
            return l10n(id.titleKey) + " · " + (componentHint(row, id, l10n) ?? absent)
        }
        return [l10n(id.titleKey), PerfScoreText.value(id, measure, l10n), PerfFormat.full(perfRoundHalfAway(sub))]
            .joined(separator: " · ")
    }

    /// `a11y.history.perf.parts`: every component as `label sub-score`.
    static func parts(_ row: PerfHistoryRow, _ l10n: Localizer) -> String {
        let words = PerfComponentId.cardOrder.map { id -> String in
            let sub = row.component(id).flatMap { $0.isPresent ? $0.subScore : nil }
            return l10n(id.titleKey) + " " + (sub.map { PerfFormat.full(perfRoundHalfAway($0)) } ?? absent)
        }
        return l10n("a11y.history.perf.parts", words.joined(separator: l10n("list.separator")))
    }

    // MARK: - Narrow list

    /// Line 2: the source, the timed requests and the usual value.
    static func listDetail(_ row: PerfHistoryRow, _ l10n: Localizer) -> String {
        var parts = [source(row, l10n), requests(row.n, l10n)]
        if let usual = row.usualShown { parts.append(l10n("history.perf.column.usual") + " " + PerfFormat.full(usual)) }
        return parts.joined(separator: " · ")
    }

    /// Line 3: the present components as `label value` with the History column names.
    static func listComponents(_ row: PerfHistoryRow, _ l10n: Localizer) -> String {
        PerfComponentId.cardOrder.compactMap { id in
            row.measure(id).map { l10n(id.columnKey) + " " + PerfScoreText.value(id, $0, l10n) }
        }.joined(separator: " · ")
    }

    // MARK: - Trend

    /// `a11y.history.perf.chart` with the lowest, highest and latest daily scores, or `.empty`.
    static func chartLabel(_ row: PerfHistoryRow, _ l10n: Localizer) -> String {
        let scores = row.trend.compactMap(\.score).map(perfRoundHalfAway)
        guard let lowest = scores.min(), let highest = scores.max(), let latest = scores.last else {
            return l10n("a11y.history.perf.chart.empty", subject(row, l10n))
        }
        return l10n("a11y.history.perf.chart", subject(row, l10n), PerfFormat.full(lowest), PerfFormat.full(highest),
                    PerfFormat.full(latest))
    }

    // MARK: - Rows

    static func accessibleValue(_ row: PerfHistoryRow, columns: [PerfHistoryColumn], _ l10n: Localizer) -> String {
        columns.filter { $0 != .model }.map { column -> String in
            if column == .parts { return parts(row, l10n) }
            let text = cell(row, column, l10n)
            let spoken = text == absent ? (tooltip(row, column, l10n) ?? text) : text
            return l10n(column.fullKey) + " " + spoken
        }.joined(separator: l10n("list.separator"))
    }

    static func accessibleLabel(_ row: PerfHistoryRow, _ l10n: Localizer) -> String {
        var label = subject(row, l10n)
        if row.shown == nil { label += l10n("list.separator") + l10n("history.perf.sample.low") }
        return label
    }

    /// The copied rows: the full column names, then one line per row, tab separated.
    static func copyText(_ rows: [PerfHistoryRow], _ l10n: Localizer) -> String {
        let columns = PerfHistoryColumn.copied
        let header = columns.map { l10n($0.fullKey) }.joined(separator: "\t")
        let lines = rows.map { row in
            columns.map { $0 == .model ? subject(row, l10n) : cell(row, $0, l10n) }.joined(separator: "\t")
        }
        return ([header] + lines).joined(separator: "\n")
    }
}
