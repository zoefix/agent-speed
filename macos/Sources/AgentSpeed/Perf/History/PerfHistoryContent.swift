import AgentSpeedCore
import CoreGraphics
import Foundation

/// A row of 性能表現: one source and raw model.
struct PerfHistoryKey: Hashable, Sendable {
    var source: SourceKind
    var model: String
}

/// One `(source, model)` of 性能表現 (DESIGN 12, *Usage History: performance*): the values pooled over the range, the
/// 14-day baseline and the daily trend.
struct PerfHistoryRow: Equatable, Identifiable, Sendable {
    var source: SourceKind
    var model: String
    var pooled: PerfPooled
    /// The baseline behind 比平常 (METRICS 6.11); nil below 20 timed requests in the 14 days before today.
    var usual: Double?
    var trend: [PerfHistory.Point]

    var id: PerfHistoryKey { PerfHistoryKey(source: source, model: model) }
    var n: Int { pooled.n }
    var shown: Int? { pooled.shown }
    var usualShown: Int? { usual.map(perfRoundHalfAway) }

    func component(_ id: PerfComponentId) -> PerfComponent? { pooled.component(id) }

    /// The pooled measure of a present component.
    func measure(_ id: PerfComponentId) -> Double? {
        guard let component = component(id), component.isPresent else { return nil }
        return component.measure
    }
}

/// 性能表現 over one range and source picker choice, rebuilt with the window's content, never per frame.
struct PerfHistoryContent: Equatable, Sendable {
    /// Every `(source, model)` with a timed request in the range, in the store's order.
    var rows: [PerfHistoryRow]
    /// The trend's days: the range, or the 14 days ending on a single-day range.
    var trendDays: DayRange
    /// The single day of a one-day range, tinted in the trend.
    var singleDay: DayKey?

    static let singleDayTrend = 14

    static func build(_ records: [PerfDayRecord], range: DayRange, source: SourceKind?, today: DayKey) -> PerfHistoryContent {
        let single = range.isSingleDay ? range.start : nil
        let trendDays = single.map { DayRange($0.adding(days: 1 - singleDayTrend), $0) } ?? range
        let rows = PerfHistory.range(records, first: range.start.description, last: range.end.description, source: source)
            .filter { $0.pooled.n > 0 }
            .map { row in
                let trend = single == nil ? row.trend
                    : PerfHistory.trend(records, source: row.source, model: row.model,
                                        first: trendDays.start.description, last: trendDays.end.description)
                return PerfHistoryRow(source: row.source, model: row.model, pooled: row.pooled,
                                      usual: PerfHistory.baseline(records, source: row.source, model: row.model,
                                                                  today: today.description),
                                      trend: trend)
            }
        return PerfHistoryContent(rows: rows, trendDays: trendDays, singleDay: single)
    }

    /// The chart's row: the chosen one while it is in the range, else the row with the most timed requests (ties in the
    /// table's order).
    static func selected(_ key: PerfHistoryKey?, in sorted: [PerfHistoryRow]) -> PerfHistoryRow? {
        if let key, let row = sorted.first(where: { $0.id == key }) { return row }
        var best: PerfHistoryRow?
        for row in sorted where row.n > (best?.n ?? -1) { best = row }
        return best
    }
}

/// The section's layout by its content width (DESIGN 12, *Widths*).
enum PerfHistoryWidth: Equatable, Sendable {
    case wide, medium, narrow

    static let wideFrom: CGFloat = 860
    static let mediumFrom: CGFloat = 600

    init(width: CGFloat) {
        self = width >= Self.wideFrom ? .wide : width >= Self.mediumFrom ? .medium : .narrow
    }

    var chartHeight: CGFloat {
        switch self {
        case .wide: 150
        case .medium: 132
        case .narrow: 120
        }
    }
}

/// What the section shows (DESIGN 12, *States*).
enum PerfHistoryMode: Equatable, Sendable {
    case analyzing, empty, rows

    init(rows: Int, analyzing: Bool) {
        self = rows > 0 ? .rows : analyzing ? .analyzing : .empty
    }
}
