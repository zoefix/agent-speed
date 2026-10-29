import AgentSpeedCore
import Foundation

/// The sub-gauge's numbers per style and size (DESIGN 2c Live metrics: *Sub-gauge, glass / timing / neon*), points
/// from the sub-gauge's top. Font sizes are points; tracking is em.
public struct BoardGaugeMetrics: Equatable, Sendable {
    public let style: BoardStyle
    public let size: BoardSize

    public init(_ style: BoardStyle, _ size: BoardSize) {
        self.style = style
        self.size = size
    }

    private func per(_ small: Double, _ medium: Double, _ large: Double) -> Double {
        switch size {
        case .small: small
        case .medium: medium
        case .large: large
        }
    }

    // MARK: 性能 (glass dial, neon digits)

    /// Glass: the dial's `D` (its box is D × 0.742 D).
    public var dial: Double { per(54, 66, 74) }
    /// The dial box's height (D × 0.742, whole points as the prototype's box).
    public var dialHeight: Double { per(40, 49, 55) }
    public var arcRadius: Double { dial * 0.439 }
    public var arcStroke: Double { dial * 0.0455 }
    public var tipRadius: Double { dial * 0.035 }

    public var mainLabelSize: Double { per(7.5, 8.5, 8.5) }
    public var mainLabelTracking: Double {
        style == .neon ? per(0.18, 0.24, 0.24) : 0.1
    }
    public var mainLabelBaseline: Double {
        style == .neon ? per(6, 7, 7) : per(19, 22, 24)
    }
    /// Glass fits `8,888`, the widest compact score, 2 pt clear of the track's inner edge (DESIGN 2c).
    public var mainNumberSize: Double {
        style == .neon ? per(21, 24, 28) : per(13, 17, 19)
    }
    public var mainNumberBaseline: Double {
        style == .neon ? per(28.5, 32.5, 35.5) : per(35.5, 42.5, 46.5)
    }

    /// Neon: the 12-segment meter.
    public var meterWidth: Double { per(62, 74, 84) }
    public var meterTop: Double { per(36.5, 41, 45.5) }
    public static let meterHeight = 3.0
    public static let meterSegments = 12

    /// The part above the table (glass: the dial box; neon: digits and meter).
    public var mainHeight: Double {
        style == .neon ? meterTop + Self.meterHeight : dialHeight
    }

    // MARK: Table (glass, neon)

    public var tableNumberSize: Double {
        style == .neon ? per(9.5, 10.5, 11.5) : per(9.5, 11, 12)
    }
    public var tableLabelSize: Double { style == .neon ? per(7.5, 8.5, 8.5) : per(7.5, 8.5, 9) }
    public var tableUnitSize: Double { style == .neon ? per(7.5, 8, 8) : per(7.5, 8.5, 9) }
    public var tableLabelTracking: Double {
        style == .neon ? 0.06 : (size == .small ? 0.02 : 0.04)
    }
    public static let tableRow = 15.0
    /// Between table rows.
    public var tableRowGap: Double { style == .neon ? per(2, 3, 3) : 2 }
    /// Between the 性能 part and the table.
    public var tableGap: Double { style == .neon ? per(3, 6, 6) : 3 }
    /// A table row's baseline under its top.
    public var tableBaseline: Double { style == .neon ? per(8, 9, 10) : per(8, 10, 11) }
    /// After the label cell's widest label; between the number and the unit.
    public var labelGap: Double { size == .small ? 3 : 4 }
    public var unitGap: Double { size == .small ? 2 : 3 }

    /// `≥`, 0.85 × the number it precedes.
    public static let atLeastScale = 0.85

    // MARK: Timing data box

    public var boxRadius: Double { 5 }
    public var mainRow: Double { per(21, 24, 27) }
    public var row: Double { per(15, 17, 19) }
    public var tabLabelSize: Double { per(8, 9, 9) }
    public var tabPadding: Double { per(4, 6, 6) }
    public var tabTracking: Double { size == .small ? 0 : 0.06 }
    public var boxMainNumberSize: Double { per(15, 17, 19) }
    public var boxNumberSize: Double { per(10.5, 11.5, 12.5) }
    public var boxUnitSize: Double { per(7, 8, 8) }
    public var boxUnitGap: Double { per(2, 3, 3) }
    public var boxRightPadding: Double { per(5, 7, 7) }
    public var boxLeftPadding: Double { per(4, 6, 6) }
    /// 性能's baseline in its row; a small row's under its top when a separator leads it.
    public var boxMainBaseline: Double { per(15.5, 17.5, 20) }
    public var boxRowBaseline: Double { per(11, 12.5, 14) }

    // MARK: Stack

    /// The sub-gauge's vertical layout for the switched-on readouts, with 思考 shown or hidden at zero.
    public func stack(intelligence: Bool, thinking: Bool, cost: Bool) -> BoardGaugeStack {
        if style == .timing {
            var y = 0.0
            var stack = BoardGaugeStack()
            if intelligence {
                stack.main = 0
                stack.mainBaseline = boxMainBaseline
                y = mainRow
            }
            for key in [BoardGaugeStack.Row.thinking, .cost] where key == .thinking ? thinking : cost {
                // The first row has no separator: its number sits half a point higher.
                let baseline = y + boxRowBaseline - (y == 0 ? 0.5 : 0)
                stack.rows.append(.init(key: key, top: y, baseline: baseline, height: row))
                y += row
            }
            stack.height = y
            return stack
        }
        var stack = BoardGaugeStack()
        var y = 0.0
        if intelligence {
            stack.main = 0
            stack.mainBaseline = mainNumberBaseline
            y = mainHeight
        }
        var first = true
        for key in [BoardGaugeStack.Row.thinking, .cost] where key == .thinking ? thinking : cost {
            y += first ? (intelligence ? tableGap : 0) : tableRowGap
            stack.rows.append(.init(key: key, top: y, baseline: y + tableBaseline, height: Self.tableRow))
            y += Self.tableRow
            first = false
        }
        stack.height = y
        return stack
    }
}

/// The vertical layout of a sub-gauge (from its top): the 性能 part, the table rows and the height.
public struct BoardGaugeStack: Equatable, Sendable {
    public enum Row: Equatable, Sendable {
        case thinking, cost
    }

    public struct Line: Equatable, Sendable {
        public var key: Row
        public var top: Double
        public var baseline: Double
        public var height: Double
    }

    /// The 性能 part's top (glass: the dial box; neon: the label line; timing: its row); nil when 性能 is off.
    public var main: Double?
    /// 性能's number baseline.
    public var mainBaseline: Double?
    public var rows: [Line] = []
    public var height = 0.0

    public func row(_ key: Row) -> Line? { rows.first { $0.key == key } }
}

/// The sub-gauge's arc and meter on the log scale of the performance score (DESIGN 2c Live metrics; DESIGN 12 *Log
/// scale*): `f = clamp(log2(v / 250) / 4, 0, 1)`, so 1000 lights half the arc and 6 of neon's 12 segments.
public enum BoardIntelligenceScale {
    /// The reference level, at the top of the arc.
    public static let reference = 1000.0

    /// Glass: the share of the arc's sweep lit by the shown value.
    public static func arc(_ shown: Int?) -> Double {
        guard let shown else { return 0 }
        return PerfFormat.scale(Double(shown))
    }

    /// Neon: `round(f × 12)` lit segments, half a segment rounding away from zero.
    public static func segments(_ shown: Int?) -> Int {
        guard let shown else { return 0 }
        let lit = (PerfFormat.scale(Double(shown)) * Double(BoardGaugeMetrics.meterSegments)).rounded(.toNearestOrAwayFromZero)
        return min(BoardGaugeMetrics.meterSegments, max(0, Int(lit)))
    }
}
