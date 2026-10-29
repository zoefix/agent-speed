import AgentSpeedCore
import CoreGraphics

/// A column of the 性能表現 table (DESIGN 12, *Table*): wide shows the seven components, medium folds them into parts.
enum PerfHistoryColumn: Hashable, Sendable {
    case model, score, usual, timed, component(PerfComponentId), parts

    static func visible(_ width: PerfHistoryWidth) -> [PerfHistoryColumn] {
        let head: [PerfHistoryColumn] = [.model, .score, .usual, .timed]
        return width == .wide ? head + PerfComponentId.cardOrder.map { .component($0) } : head + [.parts]
    }

    /// Every value column, in the wide order, for copying a row.
    static let copied: [PerfHistoryColumn] = visible(.wide)

    var isNumeric: Bool { self != .model }
    var isSortable: Bool { self != .parts }

    var headerKey: String {
        switch self {
        case .model: "history.perf.column.model"
        case .score: "history.perf.column.score"
        case .usual: "history.perf.column.usual"
        case .timed: "history.perf.column.timed"
        case .component(let id): id.columnKey
        case .parts: "history.perf.column.parts"
        }
    }

    /// The header's full name, spoken and shown as its tooltip.
    var fullKey: String {
        switch self {
        case .timed: "history.perf.column.timed.full"
        case .component(let id): id.titleKey
        default: headerKey
        }
    }

    /// A further line of the header's tooltip.
    var hintKey: String? {
        switch self {
        case .usual: "history.perf.usual.hint"
        case .component(let id): id.meaningKey
        default: nil
        }
    }

    var storageValue: String {
        switch self {
        case .model: "model"
        case .score: "score"
        case .usual: "usual"
        case .timed: "timed"
        case .component(let id): id.rawValue
        case .parts: "parts"
        }
    }

    init?(storageValue: String) {
        switch storageValue {
        case "model": self = .model
        case "score": self = .score
        case "usual": self = .usual
        case "timed": self = .timed
        default:
            guard let id = PerfComponentId(rawValue: storageValue) else { return nil }
            self = .component(id)
        }
    }

    /// The base width of a number column; the model takes the rest.
    var baseWidth: CGFloat {
        switch self {
        case .model: 0
        case .score: 88
        case .usual: 46
        case .timed: 56
        case .parts: 64
        case .component(let id):
            switch id {
            case .T: 60
            case .L: 52
            case .R: 58
            case .N: 62
            case .U: 58
            case .S: 52
            case .H: 40
            }
        }
    }
}

/// The table's sort; the window remembers it (DESIGN 12, *Table*).
struct PerfHistorySort: Equatable, Sendable {
    var column: PerfHistoryColumn = .score
    var descending = true

    static let standard = PerfHistorySort()

    /// The same column flips; another starts descending, the model ascending.
    func toggled(_ column: PerfHistoryColumn) -> PerfHistorySort {
        guard column.isSortable else { return self }
        if column == self.column { return PerfHistorySort(column: column, descending: !descending) }
        return PerfHistorySort(column: column, descending: column != .model)
    }

    /// Rows without the column's value go last; ties fall back to the default order: score descending (rows without a
    /// score by timed requests descending), then the model id, then the source.
    func sorted(_ rows: [PerfHistoryRow]) -> [PerfHistoryRow] {
        rows.sorted { lhs, rhs in
            if column == .model {
                if lhs.model != rhs.model {
                    let ascending = lhs.model.utf8.lexicographicallyPrecedes(rhs.model.utf8)
                    return descending ? !ascending : ascending
                }
                return Self.standardPrecedes(lhs, rhs)
            }
            let (a, b) = (Self.key(lhs, column), Self.key(rhs, column))
            switch (a, b) {
            case let (a?, b?) where a != b: return descending ? a > b : a < b
            case (.some, nil): return true
            case (nil, .some): return false
            default: return Self.standardPrecedes(lhs, rhs)
            }
        }
    }

    static func standardPrecedes(_ lhs: PerfHistoryRow, _ rhs: PerfHistoryRow) -> Bool {
        switch (lhs.pooled.score, rhs.pooled.score) {
        case let (a?, b?) where a != b: return a > b
        case (.some, nil): return true
        case (nil, .some): return false
        default: break
        }
        if lhs.pooled.score == nil, lhs.n != rhs.n { return lhs.n > rhs.n }
        if lhs.model != rhs.model { return lhs.model.utf8.lexicographicallyPrecedes(rhs.model.utf8) }
        return lhs.source.order < rhs.source.order
    }

    private static func key(_ row: PerfHistoryRow, _ column: PerfHistoryColumn) -> Double? {
        switch column {
        case .score: row.pooled.score
        case .usual: row.usual
        case .timed: Double(row.n)
        case .component(let id): row.measure(id)
        case .model, .parts: nil
        }
    }

    var storageValue: String { column.storageValue + (descending ? ":desc" : ":asc") }

    init(column: PerfHistoryColumn = .score, descending: Bool = true) {
        self.column = column
        self.descending = descending
    }

    init(storageValue: String?) {
        let parts = (storageValue ?? "").split(separator: ":").map(String.init)
        guard parts.count == 2, let column = PerfHistoryColumn(storageValue: parts[0]), column.isSortable,
              parts[1] == "asc" || parts[1] == "desc" else {
            self = .standard
            return
        }
        self.init(column: column, descending: parts[1] == "desc")
    }
}

/// The columns' widths over a row's content width: numbers in fixed columns, the model in the rest. When the model
/// would get less than `minModel`, the gaps tighten and then the number columns shrink (at most to 85 %), so nothing
/// scrolls sideways.
enum PerfHistoryColumnLayout {
    static let minModel: CGFloat = 150
    static let gap: CGFloat = 8
    static let minGap: CGFloat = 4

    static func layout(_ columns: [PerfHistoryColumn], available: CGFloat) -> (widths: [CGFloat], gap: CGFloat) {
        let numbers = columns.filter { $0 != .model }
        let fixed = numbers.reduce(0) { $0 + $1.baseWidth }
        let gaps = CGFloat(max(columns.count - 1, 0))
        var gap = Self.gap
        var scale: CGFloat = 1
        var model = available - fixed - gap * gaps
        if model < minModel, gaps > 0 {
            gap = max(minGap, (available - fixed - minModel) / gaps)
            model = available - fixed - gap * gaps
        }
        if model < minModel, fixed > 0 {
            scale = max(0.85, (available - gap * gaps - minModel) / fixed)
            model = available - fixed * scale - gap * gaps
        }
        let widths = columns.map { $0 == .model ? max(model, 0) : $0.baseWidth * scale }
        return (widths, gap)
    }
}
