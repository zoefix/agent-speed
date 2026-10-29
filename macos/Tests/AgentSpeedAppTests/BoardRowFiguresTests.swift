import AgentSpeedCore
import AgentSpeedOrbModel
import CoreGraphics
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 2c *The second line*: in a two-line group row (M and L) the 性能 cell and the value leave line 1, share one
/// baseline with the cap-height centre of their digits on the row's centre line (19 under the top at M, 20 at L), and
/// are 2 pt larger; S rows and sub rows keep their sizes. Checked on the rendered pixels.
@Suite("Board row figures")
@MainActor
struct BoardRowFiguresTests {
    static let l10n = Localizer(preference: .zhHant)

    static func layout(_ style: BoardStyle, _ size: BoardSize) -> (tick: BoardTick, layout: BoardContentLayout) {
        let feed = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: style, size: size, dark: false, l10n: l10n,
                                metrics: .init())
        for _ in 0..<40 { feed.step() }
        return (feed.tick, feed.layout)
    }

    /// The vertical extent of what `item` paints (points from its top), measured at 4×. `leading`: only the first share of
    /// the ink's columns, so a comma's tail after the first digit (`1,215`) is left out.
    static func inkExtent(_ item: BoardContentItem, leading: CGFloat = 1) throws -> (top: CGFloat, bottom: CGFloat) {
        let scale: CGFloat = 4
        let image = try #require(BoardRaster.image(size: item.frame.size, scale: scale, draw: item.draw))
        let data = try #require(image.dataProvider?.data as Data?)
        func alpha(_ x: Int, _ y: Int) -> UInt8 { data[y * image.bytesPerRow + x * 4 + 3] }
        let inked = (0..<image.width).filter { x in (0..<image.height).contains { alpha(x, $0) > 0 } }
        let first = try #require(inked.first), last = try #require(inked.last)
        let columns = first...max(first, first + Int(CGFloat(last - first) * leading))
        var alphas: [UInt8] = []
        for y in 0..<image.height {
            var row: UInt8 = 0
            for x in columns { row = max(row, alpha(x, y)) }
            alphas.append(row)
        }
        let threshold = (alphas.max() ?? 0) / 2
        let top = try #require(alphas.firstIndex { $0 > threshold })
        let bottom = try #require(alphas.lastIndex { $0 > threshold })
        return (CGFloat(top) / scale, CGFloat(bottom + 1) / scale)
    }

    @Test func twoLineFiguresCentreOnTheRow() throws {
        for style in BoardStyle.allCases {
            for size in [BoardSize.medium, .large] {
                let (tick, layout) = Self.layout(style, size)
                let geometry = BoardGeometry(style, size, readouts: tick.metrics.readouts)
                #expect(geometry.figureCentre == (size == .medium ? 19 : 20))
                let group = tick.groups[0]
                let row = try #require(layout.groupRows[group.id]?.row)
                for suffix in ["#value", "#iq"] {
                    let item = try #require(layout.items.first { $0.key == group.id + suffix }, "\(style) \(size) \(suffix)")
                    let ink = try Self.inkExtent(item, leading: suffix == "#iq" ? 0.15 : 1)
                    let centre = item.frame.minY + (ink.top + ink.bottom) / 2 - row.minY
                    #expect(abs(centre - CGFloat(geometry.figureCentre)) <= 0.75,
                            "\(style) \(size) \(suffix): the digits' centre is \(centre)")
                }
            }
        }
    }

    /// Timing's interval is a figure too: at its own size, centred on the row like the 性能 cell and the value (the
    /// leader's `boards.leader` and a gap alike, in Chinese and English).
    @Test func timingsIntervalCentresWithTheFigures() throws {
        for language in [LanguagePreference.zhHant, .en] {
            let l10n = Localizer(preference: language)
            for size in [BoardSize.medium, .large] {
                let feed = BoardSimFeed(simulation: BoardSimulation(scenario: "team"), style: .timing, size: size, dark: false,
                                        l10n: l10n, metrics: .init())
                for _ in 0..<40 { feed.step() }
                let (tick, layout) = (feed.tick, feed.layout)
                let geometry = BoardGeometry(.timing, size, readouts: tick.metrics.readouts)
                for group in tick.groups.prefix(3) {
                    guard let row = layout.groupRows[group.id]?.row else { continue }
                    let item = try #require(layout.items.first { $0.key == group.id + "#gap" }, "\(size) \(group.id)")
                    let ink = try Self.inkExtent(item)
                    let centre = item.frame.minY + (ink.top + ink.bottom) / 2 - row.minY
                    #expect(abs(centre - CGFloat(geometry.figureCentre)) <= 0.75,
                            "\(language) \(size) \(group.id): the interval's centre is \(centre)")
                }
            }
        }
    }

    /// Line 2 runs up to the first figure column's left edge: the 性能 cell (glass, neon), timing's interval.
    @Test func lineTwoEndsAtTheFirstFigureColumn() {
        let width: CGFloat = 320
        for style in [BoardStyle.glass, .neon] {
            let palette = BoardPalette(style: style, dark: true, highContrast: false)
            let rows = BoardListRows(style: style, size: .medium, palette: palette, capsules: BoardEffortCapsules.shared,
                                     geometry: BoardGeometry(style, .medium, readouts: .all), caption: "性能")
            #expect(rows.figuresLeft(width) == width - 6 - rows.valueWidth - 8 - rows.intelCell, "\(style)")
        }
        let palette = BoardPalette(style: .timing, dark: true, highContrast: false)
        let geometry = BoardGeometry(.timing, .medium, readouts: .all)
        let timing = BoardTimingRows(size: .medium, palette: palette, capsules: BoardEffortCapsules.shared, geometry: geometry,
                                     caption: "性能")
        #expect(timing.figuresLeft(width) < width - 4 - CGFloat(geometry.valueColumn) - 7 - timing.intelCell,
                "timing stops at the interval, before the 性能 cell")
    }

    @Test func figuresGrowTwoPointsOnlyInTwoLineGroupRows() {
        #expect(BoardListRows.intelFont(style: .glass, sub: false, live: true, large: true).size == 13)
        #expect(BoardListRows.intelFont(style: .neon, sub: false, live: true, large: true).size == 13)
        #expect(BoardTimingRows.intelFont(sub: false, live: true, large: true).size == 13)
        #expect(BoardListRows.valueFont(style: .glass, sub: false, idle: false, large: true).size == 15)
        #expect(BoardListRows.valueFont(style: .neon, sub: false, idle: false, large: true).size == 15)
        #expect(BoardTimingRows.valueFont(sub: false, idle: false, large: true).size == 15)
        // Sub rows and S keep their sizes.
        #expect(BoardListRows.intelFont(style: .glass, sub: true, live: true).size == 10.5)
        #expect(BoardTimingRows.valueFont(sub: true, idle: false).size == 11)
    }

    @Test func theColumnsFitTheLargerFigures() {
        for style in [BoardStyle.glass, .neon] {
            let palette = BoardPalette(style: style, dark: false, highContrast: false)
            let geometry = BoardGeometry(style, .medium, readouts: .all)
            let rows = BoardListRows(style: style, size: .medium, palette: palette, capsules: BoardEffortCapsules.shared,
                                     geometry: geometry, caption: "性能")
            let digits = BoardLabel("888", BoardListRows.intelFont(style: style, sub: false, live: true, large: true), color: palette.ink)
            #expect(rows.intelCell >= digits.width.rounded(.up))
            let value = BoardListRows.valueFont(style: style, sub: false, idle: false, large: true)
            #expect(rows.valueWidth >= BoardFigures.widest(value, color: palette.ink) && rows.valueWidth >= 44)
            // One-line boards keep the table's columns.
            let small = BoardListRows(style: style, size: .small, palette: palette, capsules: BoardEffortCapsules.shared,
                                      geometry: BoardGeometry(style, .small, readouts: .all), caption: "性能")
            #expect(small.valueWidth == 44)
        }
    }

    @Test func oneLineRowsKeepTheirPlaces() throws {
        // S: a group row's value is centred on its own line as before, at the one-line size.
        let (tick, layout) = Self.layout(.glass, .small)
        let group = tick.groups[0]
        let row = try #require(layout.groupRows[group.id]?.row)
        let item = try #require(layout.items.first { $0.key == group.id + "#value" })
        let ink = try Self.inkExtent(item)
        let centre = item.frame.minY + (ink.top + ink.bottom) / 2 - row.minY
        #expect(abs(centre - row.height / 2) <= 1.5, "S stays one line: \(centre) in a \(row.height) pt row")
        #expect(ink.bottom - ink.top < 11, "S keeps the 13 pt value (digits about 9.2 pt tall)")
    }
}
