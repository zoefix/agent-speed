import AgentSpeedOrbModel
import AppKit

/// Timing's sub-gauge, 資料盒 (DESIGN 2c Live metrics): a broadcast data box — label tabs on the left, every value on
/// one right edge — right-aligned at the side inset and centred in the hero band. Each row is a cached piece of the box
/// (its slab, tab, separator and its share of the outline), so 思考 hiding at zero closes the box like a drawer: the
/// row fades in place while the rows around it glide together. A value too wide for its row widens the box to the left
/// at once; it narrows 6 s after the last value that needed it. 性能 counts in full form in its row; resting and `–` are
/// drawn in `ink-3`, a provisional score with the dotted underline (DESIGN 12).
@MainActor
final class BoardTimingBox {
    let size: BoardSize
    let palette: BoardPalette
    let l10n: Localizer
    let metrics: BoardGaugeMetrics
    private var boxWidth = BoardCellWidth()
    private var widthKey: [String] = []
    private var score = BoardHeroScore()
    /// The 性能 row, where the hero's score card opens (board points); nil without 性能.
    private(set) var scoreArea: CGRect?

    /// When a running count-up lands (the board lays itself out on every frame until then).
    func climbsUntil(after now: TimeInterval) -> TimeInterval? { score.climbsUntil(after: now) }

    init(size: BoardSize, palette: BoardPalette, l10n: Localizer) {
        self.size = size
        self.palette = palette
        self.l10n = l10n
        metrics = BoardGaugeMetrics(.timing, size)
    }

    private struct Row {
        var key: String
        var label: String
        var number: String
        var unit: String
        var atLeast: Bool
        var quiet: Bool
        var tag: Bool
        var main: Bool
        var previous: String
    }

    private var tabFont: BoardFont {
        BoardFont(size: CGFloat(metrics.tabLabelSize), weight: .bold, tracking: CGFloat(metrics.tabTracking))
    }

    private func numberFont(main: Bool) -> BoardFont {
        BoardFont(family: .mono, size: CGFloat(main ? metrics.boxMainNumberSize : metrics.boxNumberSize), weight: .heavy,
                  tracking: main ? -0.04 : -0.03)
    }

    private var unitFont: BoardFont { BoardFont(family: .mono, size: CGFloat(metrics.boxUnitSize), weight: .bold) }
    private func atLeastFont(main: Bool) -> BoardFont {
        BoardFont(size: CGFloat(main ? metrics.boxMainNumberSize : metrics.boxNumberSize) * CGFloat(BoardGaugeMetrics.atLeastScale))
    }

    private func tagFont(main: Bool) -> BoardFont {
        BoardFont(size: CGFloat(main ? metrics.boxMainNumberSize : metrics.boxNumberSize) * 0.8, weight: .semibold)
    }

    private func rows(_ tick: BoardMetricsTick) -> [Row] {
        let readings = BoardGaugeReadings(tick, l10n: l10n, uppercase: true)
        var rows: [Row] = []
        if readings.intelligenceOn {
            let intelligence = tick.intelligence
            rows.append(Row(key: "intel", label: l10n("intel.glyph").uppercased(with: l10n.locale),
                            number: intelligence.shown.map(String.init) ?? "–", unit: "", atLeast: false,
                            quiet: intelligence.form != .live, tag: false, main: true, previous: "intel"))
        }
        for (reading, key) in [(readings.thinking, "think"), (readings.cost, "cost")] {
            guard let reading else { continue }
            rows.append(Row(key: key, label: reading.label, number: reading.number, unit: reading.unit, atLeast: reading.atLeast,
                            quiet: reading.quiet, tag: reading.tag, main: false, previous: key))
        }
        return rows
    }

    /// The tab column: the widest switched-on label plus its padding (思考 hiding keeps it).
    private func tabWidth(_ tick: BoardMetricsTick) -> CGFloat {
        let locale = l10n.locale
        var labels: [String] = []
        if tick.readouts.intelligence { labels.append(l10n("intel.glyph").uppercased(with: locale)) }
        if tick.readouts.thinking { labels.append(l10n("intel.thinking.glyph").uppercased(with: locale)) }
        if tick.readouts.cost { labels.append(l10n("cost.glyph").uppercased(with: locale)) }
        let widest = labels.map { BoardLabel($0, tabFont, color: palette.ink2).width }.max() ?? 0
        return (widest + 2 * CGFloat(metrics.tabPadding)).rounded(.up)
    }

    private func valueWidth(_ row: Row) -> CGFloat {
        let number = BoardLabel(row.number, row.tag ? tagFont(main: row.main) : numberFont(main: row.main), color: palette.ink).width
        let unit = row.unit.isEmpty ? 0 : CGFloat(metrics.boxUnitGap) + BoardLabel(row.unit, unitFont, color: palette.ink3).width
        let atLeast = row.atLeast ? BoardLabel("≥", atLeastFont(main: row.main), color: palette.ink3).width + 1 + CGFloat(metrics.boxUnitGap) : 0
        return atLeast + number + unit
    }

    private func fit(_ tick: BoardMetricsTick, now: TimeInterval) -> CGFloat {
        let key = [String(describing: tick.readouts), l10n.language.rawValue, tick.costUnit]
        if key != widthKey {
            widthKey = key
            boxWidth.reset(base: BoardGeometry(.timing, size).gaugeWidth)
        }
        let tab = tabWidth(tick)
        let need = rows(tick).map { tab + CGFloat(metrics.boxLeftPadding) + valueWidth($0) + CGFloat(metrics.boxRightPadding) }.max() ?? 0
        return CGFloat(boxWidth.fit(Double(need.rounded(.up)), at: now))
    }

    var narrowsAt: TimeInterval? { boxWidth.narrowsAt }

    func width(_ tick: BoardMetricsTick, now: TimeInterval) -> CGFloat { fit(tick, now: now) }

    func items(_ tick: BoardMetricsTick, in frame: BoardGaugeFrame) -> [BoardContentItem] {
        let width = fit(tick, now: frame.now)
        let tab = tabWidth(tick)
        let rows = self.rows(tick)
        let stack = metrics.stack(intelligence: tick.readouts.intelligence, thinking: tick.showsThinking, cost: tick.readouts.cost)
        let top = frame.top + (frame.height - CGFloat(stack.height)) / 2
        let left = frame.right - width
        let memory = frame.memory
        var items: [BoardContentItem] = []
        scoreArea = nil
        for (index, row) in rows.enumerated() {
            let rowTop: CGFloat, height: CGFloat, baseline: CGFloat
            if row.main {
                rowTop = top
                height = CGFloat(metrics.mainRow)
                baseline = top + CGFloat(metrics.boxMainBaseline)
            } else {
                guard let line = stack.row(row.key == "think" ? .thinking : .cost) else { continue }
                rowTop = top + CGFloat(line.top)
                height = CGFloat(line.height)
                baseline = top + CGFloat(line.baseline)
            }
            let first = index == 0, last = index == rows.count - 1
            items.append(slab(row, frame: CGRect(x: left, y: rowTop, width: width, height: height), tab: tab, first: first,
                              last: last, baseline: baseline - rowTop))
            if row.main {
                let reading = tick.intelligence
                let (shown, motion) = score.update(reading, previous: memory.previousHeroScore, fresh: memory.fresh,
                                                   now: frame.now, reduceMotion: frame.reduceMotion)
                scoreArea = CGRect(x: left, y: rowTop, width: width, height: height)
                let number = BoardScoreNumber(key: "box.intel.n", shown: shown, form: .full, font: numberFont(main: true),
                                              color: reading.form == .live ? palette.ink : palette.quiet,
                                              anchor: .right(frame.right - CGFloat(metrics.boxRightPadding)), baseline: baseline,
                                              provisional: reading.isProvisional, highContrast: palette.highContrast)
                items += number.items(fade: motion.numberFade)
                continue
            }
            let number = BoardRollingText(key: "box.\(row.key).n", text: row.number,
                                          font: row.tag ? tagFont(main: row.main) : numberFont(main: row.main),
                                          color: row.quiet ? palette.quiet : palette.ink,
                                          right: frame.right - CGFloat(metrics.boxRightPadding) - unitSpace(row), baseline: baseline)
            items += number.items(previous: memory.previousMetric(row.previous), fresh: memory.fresh,
                                  motion: .glide(fade: BoardRollingText.crossfade))
        }
        return items
    }

    private func unitSpace(_ row: Row) -> CGFloat {
        row.unit.isEmpty ? 0 : CGFloat(metrics.boxUnitGap) + BoardLabel(row.unit, unitFont, color: palette.ink3).width
    }

    /// One row of the box: its slab and tab with the label, its separator (rows after the first) and its share of the
    /// 1 pt outline, the unit and `≥` on the number's baseline. Only the box's ends are rounded.
    private func slab(_ row: Row, frame: CGRect, tab: CGFloat, first: Bool, last: Bool, baseline: CGFloat) -> BoardContentItem {
        let palette = self.palette, metrics = self.metrics
        let label = BoardLabel(row.label, tabFont, color: row.main ? palette.blockInk : palette.ink2)
        let unit = BoardLabel(row.unit, unitFont, color: palette.ink3)
        let numberLabel = BoardLabel(row.number, row.tag ? tagFont(main: row.main) : numberFont(main: row.main), color: palette.ink)
        let atLeast = BoardLabel("≥", atLeastFont(main: row.main), color: palette.ink3)
        let radius = CGFloat(metrics.boxRadius)
        let separator: CGFloat = first ? 0 : 1
        let rightPad = CGFloat(metrics.boxRightPadding), unitGap = CGFloat(metrics.boxUnitGap)
        let revision = AnyHashable([AnyHashable(row.label), AnyHashable(row.unit), AnyHashable(row.atLeast),
                                    AnyHashable(row.atLeast ? row.number : ""), AnyHashable(frame.size.width),
                                    AnyHashable(frame.size.height), AnyHashable(tab), AnyHashable(first), AnyHashable(last),
                                    AnyHashable(baseline), AnyHashable(palette.dark), AnyHashable(palette.highContrast)])
        var item = BoardContentItem(key: "box.\(row.key)", frame: frame, motion: .glide(fade: 0.25), revision: revision) { context, size in
            // The box's shape seen from this row: rounded only where the box ends.
            let shape = CGRect(x: 0, y: first ? 0 : -2 * radius, width: size.width,
                               height: size.height + (first ? 0 : 2 * radius) + (last ? 0 : 2 * radius))
            let path = CGPath(roundedRect: shape, cornerWidth: radius, cornerHeight: radius, transform: nil)
            context.saveGState()
            context.addRect(CGRect(origin: .zero, size: size))
            context.clip()
            context.addPath(path)
            context.clip()
            BoardDrawing.fill(shape, color: palette.slab2, in: context)
            BoardDrawing.fill(CGRect(x: 0, y: shape.minY, width: tab, height: shape.height), color: palette.block, in: context)
            if !first { BoardDrawing.fill(CGRect(x: 0, y: 0, width: size.width, height: 1), color: palette.line2, in: context) }
            context.restoreGState()
            context.saveGState()
            context.addRect(CGRect(origin: .zero, size: size))
            context.clip()
            BoardDrawing.stroke(shape, radius: radius, width: 1, color: palette.highContrast ? palette.ink3 : palette.line2, in: context)
            context.restoreGState()
            let mid = separator + (size.height - separator) / 2
            label.draw(in: context, x: label.centredX(tab / 2, tracking: label.style.tracking), baseline: label.baseline(centeredOn: mid))
            var x = size.width - rightPad
            if !row.unit.isEmpty {
                x -= unit.width
                unit.draw(in: context, x: x, baseline: baseline)
                x -= unitGap
            }
            if row.atLeast {
                x -= numberLabel.width + unitGap + 1 + atLeast.width
                atLeast.draw(in: context, x: x, baseline: baseline)
            }
        }
        item.group = .hero
        return item
    }
}
