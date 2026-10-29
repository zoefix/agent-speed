import Foundation

/// Where a board's parts sit for its style, size and live metrics switches (DESIGN 2c Layout per size; Live metrics
/// *Hero* and *Ranking rows*). Points from the board's top-left corner, y down. With every switch off the board keeps
/// the geometry it had before the live metrics, except that group rows at M and L keep their second line.
public struct BoardGeometry: Equatable, Sendable {
    public let style: BoardStyle
    public let size: BoardSize
    public let readouts: BoardReadouts

    public init(_ style: BoardStyle, _ size: BoardSize, readouts: BoardReadouts = .none) {
        self.style = style
        self.size = size
        self.readouts = readouts
    }

    public var side: Double { size.side }

    private func per(_ small: Double, _ medium: Double, _ large: Double) -> Double {
        switch size {
        case .small: small
        case .medium: medium
        case .large: large
        }
    }

    /// The hero's sub-gauge shows (any of 性能, 思考, 費用).
    public var hasGauge: Bool { readouts.hero }
    /// The rows' 性能 column and its captions line.
    public var hasIntelligence: Bool { readouts.intelligence }
    /// Group rows draw their second line (the model): at M and L whatever the switches, never at S.
    public var twoLine: Bool { size != .small }
    /// No readout shows: the hero and the row columns as before the live metrics.
    public var isLegacy: Bool { !hasGauge && !hasIntelligence }

    // MARK: Hero

    /// The side inset (the prototypes' `--pad`).
    public var inset: Double {
        style == .timing ? per(12, 14, 16) : per(14, 16, 18)
    }

    public var bandTop: Double {
        style == .timing ? inset + 2 : inset - 2
    }

    /// The hero band's height; neon's grows by 10 / 8 / 8 with the sub-gauge.
    public var bandHeight: Double {
        switch style {
        case .glass: per(78, 86, 94)
        case .timing: per(72, 80, 88)
        case .neon: hasGauge ? per(92, 100, 108) : per(82, 92, 100)
        }
    }

    /// The height the sub-gauge is centred in: the band (neon: less its bottom 17, the LED bar).
    public var gaugeArea: Double { style == .neon ? bandHeight - 17 : bandHeight }

    /// The sub-gauge's nominal width (timing: the data box's).
    public var gaugeWidth: Double {
        style == .timing ? per(110, 134, 152) : per(92, 112, 128)
    }

    /// The gap between the hero's two columns.
    public static let heroGap = 8.0

    /// Glass and timing draw their history as a full-width strip under the hero while the sub-gauge shows.
    public var hasStrip: Bool { hasGauge && style != .neon }

    public var stripTop: Double {
        style == .timing ? bandTop + bandHeight + 8 : bandTop + bandHeight
    }

    public var stripHeight: Double {
        guard hasStrip else { return 0 }
        return style == .timing ? per(10, 12, 12) : per(14, 16, 18)
    }

    // MARK: Ranking

    /// The footer's share of the board below the ranking.
    private var footerHeight: Double {
        switch style {
        case .glass: 38
        case .timing: 32
        case .neon: 36
        }
    }

    /// The top of the ranking (the rows' coordinates start here).
    public var rankingTop: Double {
        if hasGauge {
            switch style {
            case .glass: return stripTop + stripHeight
            case .timing: return stripTop + stripHeight + 5
            case .neon: return bandTop + bandHeight + 4
            }
        }
        switch style {
        case .glass: return inset + bandHeight + 4
        case .timing: return inset + per(70, 78, 86) + 8
        case .neon: return inset + bandHeight + 2
        }
    }

    public var rankingHeight: Double { side - rankingTop - footerHeight }

    /// The captions line at the top of the ranking (性能, tok/s; timing also 間隔), 0 without the 性能 column.
    public var captionsHeight: Double {
        guard hasIntelligence else { return 0 }
        return style == .timing ? 14 : 12
    }

    /// The first row's top in the ranking.
    public var firstRow: Double {
        if hasIntelligence { return captionsHeight + 2 }
        return style == .timing ? 0 : 6
    }

    /// The vertical room for rows.
    public var room: Double {
        guard !isLegacy else {
            switch style {
            // Neon's prototype used the glass box.
            case .glass, .neon: return side - (inset + per(78, 86, 94) + 4) - 38 - 8
            case .timing: return side - (inset + per(70, 78, 86) + 8) - 32 - 6
            }
        }
        return rankingHeight - firstRow
    }

    /// A group row's height (two lines: 8 more).
    public var groupRow: Double { per(30, 30, 32) + (twoLine ? 8 : 0) }

    /// Glass and neon leave 2 pt between single-line group rows.
    public var groupGap: Double { style != .timing && !twoLine ? 2 : 0 }

    public var subRow: Double {
        style == .timing ? per(21, 21, 22) : per(22, 22, 23)
    }

    public var moreRow: Double { style == .timing ? 19 : 20 }

    // MARK: Row columns

    /// The 性能 cell's width (at least as wide as `8,888` and the caption; the app widens it when they are wider):
    /// glass 33, timing and neon 34, and 39 / 40 on the two-line group rows of M and L.
    public var intelligenceColumn: Double {
        guard hasIntelligence else { return 0 }
        let mono = style != .glass
        return twoLine ? (mono ? 40 : 39) : (mono ? 34 : 33)
    }

    /// The value column.
    public var valueColumn: Double {
        switch style {
        case .glass, .neon: hasIntelligence ? 44 : 46
        case .timing: size == .small ? (hasIntelligence ? 40 : 44) : (hasIntelligence ? 42 : 46)
        }
    }

    /// Timing's interval column (none at S).
    public var intervalColumn: Double {
        guard style == .timing, size != .small else { return 0 }
        return hasIntelligence ? 34 : 38
    }

    /// Two-line rows: line 1's baseline under the row's top, line 2's.
    public static let firstLineBaseline = 16.0
    public var secondLineBaseline: Double { style == .glass ? 32 : 31 }

    /// Two-line group rows: the row's figures (the 性能 cell and the value) leave line 1 and share one baseline with the
    /// cap-height centre of their digits on the row's centre line, 19 under the top at M and 20 at L (DESIGN 2c *The
    /// second line*).
    public var figureCentre: Double { groupRow / 2 }
    /// …and they are this much larger than in a one-line row.
    public static let figureGrowth = 2.0
}

extension BoardLayoutMetrics {
    /// The planner's metrics for a geometry.
    public init(_ geometry: BoardGeometry) {
        let limits = Self.limits(geometry.size)
        self.init(groupHeight: geometry.groupRow + geometry.groupGap, subHeight: geometry.subRow,
                  moreHeight: geometry.moreRow, room: geometry.room, top: geometry.firstRow,
                  groupLimit: limits.groups, subLimits: limits.subs)
    }
}
