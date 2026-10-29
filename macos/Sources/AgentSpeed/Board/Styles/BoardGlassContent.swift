import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// 流光玻璃 (DESIGN 2c Styles; `board-glass.html`): calm type on the glass, a heat-tinted aurora drifting behind the
/// hero, the 60 s sparkline, rows with glowing share underlines. With the live metrics (Live metrics, *Hero*) the
/// hero's right side is the pearl sub-gauge and the sparkline becomes a full-width strip under the hero.
@MainActor
final class BoardGlassContent: BoardContentBuilding {
    let size: BoardSize
    let palette: BoardPalette
    let l10n: Localizer
    let capsules: BoardCapsuleDrawing
    private(set) var rows: BoardListRows
    private var memory = BoardTickMemory()
    private lazy var gauge = BoardGlassGauge(size: size, palette: palette, l10n: l10n)

    init(size: BoardSize, dark: Bool, highContrast: Bool, l10n: Localizer, capsules: BoardCapsuleDrawing) {
        self.size = size
        palette = BoardPalette(style: .glass, dark: dark, highContrast: highContrast)
        self.l10n = l10n
        self.capsules = capsules
        rows = BoardListRows(style: .glass, size: size, palette: palette, capsules: capsules)
    }

    private var side: CGFloat { CGFloat(size.side) }
    private var pad: CGFloat { size == .small ? 14 : size == .medium ? 16 : 18 }
    private var heroHeight: CGFloat { size == .small ? 78 : size == .medium ? 86 : 94 }
    private var big: CGFloat { size == .small ? 34 : size == .medium ? 40 : 46 }

    var wakeAt: TimeInterval? { gauge.narrowsAt }
    var reduceMotion = false

    func climbsUntil(after now: TimeInterval) -> TimeInterval? { gauge.climbsUntil(after: now) }

    func layout(_ tick: BoardTick) -> BoardContentLayout {
        memory.begin(tick)
        let geometry = BoardContentBuilders.geometry(.glass, size, tick)
        if rows.geometry != geometry {
            rows = BoardListRows(style: .glass, size: size, palette: palette, capsules: capsules, geometry: geometry,
                                 caption: l10n("intel.glyph"))
        }
        let rowsTop = CGFloat(geometry.rankingTop)
        let models = BoardRowModels.make(tick, l10n: l10n, twoLine: geometry.twoLine)
        let lives = BoardRowModels.lives(tick)
        let column = BoardRowModels.capsuleColumn(models, compact: rows.compact, capsules: rows.capsules)
        var items = aurora(tick, geometry: geometry)
        let (hero, number) = self.hero(tick, geometry: geometry)
        items += hero
        var anchors: [String: (row: CGRect, rank: CGPoint)] = [:]
        var subRows: [String: CGRect] = [:]
        for (index, row) in tick.rows.enumerated() {
            let frame = rows.frame(row, rowsTop: rowsTop, pad: pad)
            switch row.kind {
            case .group(let group): anchors[tick.groups[group].id] = (frame, rows.rankCenter(frame))
            case .sub: subRows[row.key] = frame
            case .moreSubs, .moreGroups: break
            }
            items += rows.items(models[index], key: row.key, frame: frame, column: column,
                                accents: BoardRowAccents(row, live: lives[index], tick: tick, memory: memory, model: models[index],
                                                         reduceMotion: reduceMotion))
        }
        items.append(footer(tick))
        memory.end(tick, rows: zip(tick.rows, lives).map { ($0.key, $1) },
                   scores: Dictionary(zip(tick.rows, models).compactMap { row, model in model.intel.map { (row.key, $0) } },
                                      uniquingKeysWith: { a, _ in a }))
        let scoreArea = geometry.hasGauge && tick.metrics.readouts.intelligence ? gauge.scoreArea : nil
        return BoardContentLayout(items: items, groupRows: anchors, number: number, subRows: subRows, scoreArea: scoreArea)
    }

    /// Two soft lobes of heat light behind the hero (`.aurora`, drifting on 19 s and 23 s while agents work), and the
    /// rule above the rows or, with the 性能 column, the captions line in its place.
    private func aurora(_ tick: BoardTick, geometry: BoardGeometry) -> [BoardContentItem] {
        let palette = self.palette, side = self.side
        var items: [BoardContentItem] = []
        if !palette.highContrast {
            let heat = (tick.heat * 25).rounded() / 25
            let strength = 0.18 + heat * 0.62
            let active = tick.generating > 0
            // The lobes' closest-side gradients under a 34 pt blur: a linear fade reaching a blur further.
            let radius = CGSize(width: side * 0.49 + 34, height: side * 0.495 + 34)
            let lobes: [(CGPoint, RGBColor, BoardContentAmbient)] = [
                (CGPoint(x: side * 0.29, y: side * 0.205), HeatPalette.color(max(0.06, heat)).opacity(strength * 0.9),
                 .drift(dx: side * 0.176, dy: side * 0.079, scale: 1.15, period: 19)),
                (CGPoint(x: side * 0.78, y: side * 0.095), HeatPalette.color(min(1, heat + 0.18)).opacity(strength * 0.63),
                 .drift(dx: -side * 0.137, dy: side * 0.119, scale: 0.92, period: 23)),
            ]
            for (index, (center, color, drift)) in lobes.enumerated() {
                let frame = CGRect(x: center.x - radius.width, y: center.y - radius.height, width: 2 * radius.width, height: 2 * radius.height)
                // One soft white light, tinted to the heat by its layer: a new heat needs no redraw.
                var lobe = BoardContentItem(key: "aurora\(index)", frame: frame, motion: .none,
                                            revision: AnyHashable([index])) { context, box in
                    BoardDrawing.radial(center: CGPoint(x: box.width / 2, y: box.height / 2), radius: radius, color: .white, in: context)
                }
                lobe.tint = color.cgColor
                lobe.ambient = active ? drift : nil
                lobe.rasterScale = 0.5
                items.append(lobe)
            }
        }
        let rowsTop = CGFloat(geometry.rankingTop)
        if geometry.hasIntelligence {
            items.append(rows.captions(frame: CGRect(x: pad - 6, y: rowsTop, width: side - 2 * pad + 12, height: CGFloat(geometry.captionsHeight)),
                                       intel: l10n("intel.glyph"), unit: l10n("unit.tokS")))
        } else {
            let rule = CGRect(x: pad, y: rowsTop, width: side - 2 * pad, height: 1)
            items.append(BoardContentItem(key: "rule", frame: rule, motion: geometry.isLegacy ? .none : .row,
                                          revision: AnyHashable([palette.dark, palette.highContrast])) { context, box in
                BoardDrawing.fill(CGRect(origin: .zero, size: box), color: palette.line, in: context)
            })
        }
        return items
    }

    private func hero(_ tick: BoardTick, geometry: BoardGeometry) -> ([BoardContentItem], CGRect) {
        let palette = self.palette, big = self.big, heroHeight = self.heroHeight
        let heat = (tick.heat * 50).rounded() / 50
        let metaLine: CGFloat = 17
        let numTop = (heroHeight - (big * 1.08 + 7 + metaLine)) / 2
        let frame = CGRect(x: pad, y: pad - 2, width: side - 2 * pad, height: heroHeight)
        let look = BoardHeroNumber.Look(font: BoardFont(size: big, weight: .bold, tracking: -0.02, tabular: true), color: palette.ink,
                                        hot: palette.dark ? HeatPalette.whiteHot : palette.ink,
                                        hotGlow: palette.glows ? palette.heat(max(0.12, heat)) : nil, hotBlur: 14)
        let number = BoardHeroNumber(tick.valueText, value: tick.value, look: look, origin: CGPoint(x: frame.minX, y: frame.minY + numTop))
        let unit = BoardLabel(l10n("unit.tokS"), BoardFont(size: 13, weight: .medium), color: palette.ink2)
        // Beside the sub-gauge at S the meta line tightens: no tracking, 4 pt between the title and the context.
        let tight = geometry.hasGauge && size == .small
        let metaGap: CGFloat = tight ? 4 : 6
        let title = BoardLabel(tick.title, BoardFont(size: 12, weight: .semibold, tracking: tight ? 0 : 0.03),
                               color: palette.heatText(max(0.06, tick.heat)))
        let counts = BoardLabel("· " + l10n("agents.context", tick.generating, tick.tools), BoardFont(size: 12), color: palette.ink2)
        let sparkWidth: CGFloat = size == .small ? 76 : 104
        let history = tick.history.map { ($0 * 10).rounded() / 10 }
        let theme = AnyHashable([AnyHashable(palette.dark), AnyHashable(palette.highContrast), AnyHashable(l10n.language.rawValue)])
        let baseline = numTop + number.baseline
        let unitX = number.label.width + 6
        let now = Clock.now
        let gaugeWidth = geometry.hasGauge ? gauge.width(tick.metrics, now: now) : 0
        // Each piece is cached apart: the glow follows the digits, the unit their width, the meta line the title and
        // counts, the sparkline (or the strip) the history.
        var items: [BoardContentItem] = []
        if palette.glows, heat > 0 {
            let glow = palette.heat(heat).opacity(0.55), blur = heat * 14
            let rect = number.label.box(x: 0, baseline: baseline, margin: blur * 1.5)
                .union(unit.box(x: unitX, baseline: baseline, margin: blur * 1.5))
            var item = BoardContentItem.part("hero.glow", of: frame, rect: rect,
                                             revision: AnyHashable([AnyHashable(tick.valueText), AnyHashable(heat), theme])) { context in
                number.drawGlow(in: context, x: 0, top: numTop, color: glow, blurs: [blur])
                unit.drawGlowOnly(in: context, x: unitX, baseline: baseline, glow: glow, blur: blur)
            }
            item.rasterScale = BoardContentItem.softScale
            items.append(item)
        }
        items.append(.part("hero.unit", of: frame, rect: unit.box(x: unitX, baseline: baseline),
                           revision: AnyHashable([AnyHashable(unitX), theme])) { context in
            unit.draw(in: context, x: unitX, baseline: baseline)
        })
        let metaMid = numTop + big * 1.08 + 7 + metaLine / 2
        // The title and context share the left column with the value: the context truncates first, then the title.
        let room = frame.width - (geometry.hasGauge ? gaugeWidth : sparkWidth) - BoardGeometry.heroGap
        let metaRect = CGRect(x: 0, y: metaMid - metaLine / 2 - 3, width: room, height: metaLine + 6)
        let titleColor = palette.heatText(max(0.06, tick.heat))
        items.append(.part("hero.meta", of: frame, rect: metaRect,
                           revision: AnyHashable([AnyHashable(tick.title), AnyHashable(tick.generating), AnyHashable(tick.tools),
                                                  AnyHashable([titleColor.red, titleColor.green, titleColor.blue]), AnyHashable(room),
                                                  theme])) { context in
            let metaBaseline = title.baseline(centeredOn: metaMid)
            let shownTitle = title.truncated(to: room)
            shownTitle.draw(in: context, x: 0, baseline: metaBaseline)
            counts.truncated(to: max(0, room - shownTitle.width - metaGap)).draw(in: context, x: shownTitle.width + metaGap, baseline: metaBaseline)
        })
        let historyColor = palette.heat(max(0.06, heat))
        if geometry.hasStrip {
            let strip = CGRect(x: pad, y: CGFloat(geometry.stripTop), width: side - 2 * pad, height: CGFloat(geometry.stripHeight))
            items.append(.part("hero.strip", of: CGRect(x: 0, y: 0, width: side, height: side), rect: strip.insetBy(dx: -2, dy: -2),
                               revision: AnyHashable([AnyHashable(history), AnyHashable(heat), AnyHashable(strip.height), theme])) { context in
                BoardHeroHistory.strip(history, in: strip, color: historyColor, context: context)
            })
        } else {
            // The page's inline SVG sits atop a line box 3 pt taller than itself.
            let spark = CGRect(x: frame.width - sparkWidth, y: (heroHeight - 43) / 2, width: sparkWidth, height: 40)
            items.append(.part("hero.spark", of: frame, rect: spark.insetBy(dx: -4, dy: -4),
                               revision: AnyHashable([AnyHashable(history), AnyHashable(heat), theme])) { context in
                BoardHeroHistory.sparkline(history, in: spark, color: historyColor, context: context)
            })
        }
        let decals = tick.newPeak ? [number.sheen(), BoardHeroNumber.peakChip(style: .glass, l10n: l10n, glows: palette.glows)] : []
        items += number.items(memory: memory, decals: decals)
        if geometry.hasGauge {
            items += gauge.items(tick.metrics, in: BoardGaugeFrame(top: CGFloat(geometry.bandTop), height: CGFloat(geometry.gaugeArea),
                                                                    right: side - pad, memory: memory, now: now,
                                                                    reduceMotion: reduceMotion))
        }
        return (items.inHero(), number.rect)
    }

    private func footer(_ tick: BoardTick) -> BoardContentItem {
        var parts = [BoardFooterPart("boards.today", value: tick.todayText, l10n: l10n)]
        if let cost = tick.costText { parts.append(BoardFooterPart(value: cost, quiet: tick.costIsTag)) }
        if size != .small { parts.append(BoardFooterPart("boards.peak", value: tick.peakText, l10n: l10n)) }
        let palette = self.palette
        let frame = CGRect(x: pad, y: side - 12 - 18, width: side - 2 * pad, height: 18)
        let revision = AnyHashable([tick.todayText, tick.costText ?? "", tick.costIsTag ? "t" : "", tick.peakText,
                                    palette.dark ? "d" : "l", palette.highContrast ? "c" : "", l10n.language.rawValue])
        return BoardContentItem(key: "footer", frame: frame, motion: .none, revision: revision) { context, box in
            BoardFooterPart.draw(parts, label: BoardFont(size: 11, tabular: true), strong: BoardFont(size: 11, weight: .semibold, tabular: true),
                                 labelColor: palette.ink3, strongColor: palette.ink2, width: box.width, midY: box.height / 2, in: context)
        }
    }
}
