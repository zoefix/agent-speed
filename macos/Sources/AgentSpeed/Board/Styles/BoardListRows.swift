import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Glass and neon ranking rows (the prototypes' `.row` grid: 16 pt rank, 10 pt badge, name, capsule column, 46 pt
/// value, 8 pt gaps, 6 pt padding), with the share underline from the name to the value. With the live metrics
/// (DESIGN 2c *Ranking rows*): the 性能 cell before a 44 pt value and the captions line over the ranking. At M and L
/// each group row has a second line, the detail under the name, and its figures (性能 and the value) 2 pt larger,
/// centred on the row; their columns widen to fit them.
@MainActor
struct BoardListRows {
    let style: BoardStyle
    let size: BoardSize
    let palette: BoardPalette
    let capsules: BoardCapsuleDrawing
    let geometry: BoardGeometry
    /// The 性能 cell's width: at least as wide as `8,888` in its font (a two-line group row's) and as the caption.
    let intelCell: CGFloat
    /// The value column: at least as wide as the widest value in a two-line group row's font.
    let valueWidth: CGFloat

    init(style: BoardStyle, size: BoardSize, palette: BoardPalette, capsules: BoardCapsuleDrawing,
         geometry: BoardGeometry? = nil, caption: String = "") {
        self.style = style
        self.size = size
        self.palette = palette
        self.capsules = capsules
        let geometry = geometry ?? BoardGeometry(style, size)
        self.geometry = geometry
        let large = geometry.twoLine
        if geometry.hasIntelligence {
            let digits = BoardLabel(PerfFormat.measureText, Self.intelFont(style: style, sub: false, live: true, large: large), color: palette.ink).width
            let label = BoardLabel(caption, Self.captionFont(style), color: palette.ink3).width
            intelCell = max(CGFloat(geometry.intelligenceColumn), digits.rounded(.up), label.rounded(.up))
        } else {
            intelCell = 0
        }
        let font = Self.valueFont(style: style, sub: false, idle: false, large: large)
        let widest = large ? BoardFigures.widest(font, color: palette.ink) : 0
        valueWidth = max(CGFloat(geometry.valueColumn), widest)
    }

    private var neon: Bool { style == .neon }
    var compact: Bool { size == .small }

    /// The row's frame inside the board: the rows area spans the board less `pad − 6` on each side.
    func frame(_ row: BoardPlannedRow, rowsTop: CGFloat, pad: CGFloat) -> CGRect {
        let height = row.height - (isGroup(row) ? CGFloat(geometry.groupGap) : 0)
        return CGRect(x: pad - 6, y: rowsTop + CGFloat(row.y), width: CGFloat(size.side) - 2 * pad + 12, height: height)
    }

    /// The center of the rank, for the effects (board points).
    func rankCenter(_ frame: CGRect) -> CGPoint {
        CGPoint(x: frame.minX + 14, y: geometry.twoLine && frame.height > 34 ? frame.minY + 12 : frame.midY)
    }

    // MARK: Columns

    private func valueRight(_ width: CGFloat) -> CGFloat { width - 6 }
    private func intelRight(_ width: CGFloat) -> CGFloat { valueRight(width) - valueWidth - 8 }
    private func capsuleX(_ width: CGFloat, column: CGFloat) -> CGFloat {
        (intelCell > 0 ? intelRight(width) - intelCell : valueRight(width) - valueWidth) - 8 - column
    }

    /// Line 1's baseline in a two-line group row, else nil (the row's lines centre).
    private func firstBaseline(_ model: BoardRowModel) -> CGFloat? {
        model.twoLine && model.rank != nil ? CGFloat(BoardGeometry.firstLineBaseline) : nil
    }

    /// A two-line group row's figures: larger, on one baseline centred on the row (nil: a one-line row's own).
    private func figures(_ model: BoardRowModel) -> BoardFigures? {
        guard model.twoLine, model.rank != nil else { return nil }
        let intel = model.intel.map { Self.intelFont(style: style, sub: false, live: $0.live, large: true) }
        return BoardFigures(centre: CGFloat(geometry.figureCentre), fonts: [Self.valueFont(style: style, sub: false, idle: model.idle,
                                                                                            large: true)] + (intel.map { [$0] } ?? []))
    }

    /// Where line 2 ends: the first figure column's left edge (the figures, centred on the row, hold their columns).
    func figuresLeft(_ width: CGFloat) -> CGFloat {
        intelCell > 0 ? intelRight(width) - intelCell : valueRight(width) - valueWidth
    }

    // MARK: Items

    /// The row's items: its body, its rank (groups), its value and its 性能, each cached apart so a new value or rank
    /// redraws only itself and can roll or flash; all spring together.
    func items(_ model: BoardRowModel, key: String, frame: CGRect, column: CGFloat, accents: BoardRowAccents) -> [BoardContentItem] {
        let rows = self, look = AnyHashable([AnyHashable(palette.dark), AnyHashable(palette.highContrast)])
        var body = BoardContentItem(key: key, frame: frame, revision: AnyHashable([AnyHashable(model.body), AnyHashable(column),
                                                                                 AnyHashable(intelCell), look])) { context, box in
            rows.draw(model, column: column, in: context, size: box)
        }
        guard model.kind != .more else { return [body] }
        let sub = model.kind == .sub
        let source = model.source.map { palette.source($0) } ?? .white
        if let event = accents.event {
            body.accents.append(.decal(BoardRowDecals.sweep(width: frame.width, height: frame.height, color: source,
                                                           leader: event.kind == .newLeader, radius: neon ? 9 : 10)))
        }
        var items = [body]
        if model.share > 0 {
            items.append(underline(model, sub: sub, key: key, frame: frame, look: look))
        }

        let line1 = firstBaseline(model)
        if let rank = model.rank {
            let top = line1.map { frame.minY + $0 - 12 } ?? frame.midY - 8
            var item = BoardContentItem(key: key + "#rank", frame: CGRect(x: frame.minX + 6, y: top, width: 16, height: 16),
                                        revision: AnyHashable([AnyHashable(rank), AnyHashable(line1 != nil), look])) { context, _ in
                rows.drawRank(rank, leader: rank == 1, midY: 8, baseline: line1.map { _ in 12 }, x: 0, in: context)
            }
            item.clips = true
            if let old = accents.previousRank { item.accents.append(.roll(rising: rank < old)) }
            items.append(item)
        }

        let figures = self.figures(model)
        let figureLine = figures?.baseline
        let valueFrame = CGRect(x: frame.maxX - 76, y: frame.minY, width: 76, height: frame.height)
        var value = BoardContentItem(key: key + "#value", frame: valueFrame,
                                     revision: AnyHashable([AnyHashable(model.value), AnyHashable(model.idle), AnyHashable(model.heat),
                                                            AnyHashable(figureLine), look])) { context, box in
            rows.drawValue(model, sub: sub, right: box.width - 6, midY: box.height / 2, baseline: figureLine, hot: false, in: context)
        }
        value.hot = BoardContentDrawing { context, box in
            rows.drawValue(model, sub: sub, right: box.width - 6, midY: box.height / 2, baseline: figureLine, hot: true, in: context)
        }
        switch accents.change {
        case .rise: value.accents.append(.warm)
        case .fall: value.accents.append(.dim)
        case .steady: break
        }
        if let event = accents.event, event.from > event.to {
            value.accents.append(.decal(BoardRowDecals.chip(style: style, climbed: event.from - event.to, rowRight: 76,
                                                            midY: figures.map { $0.centre } ?? frame.height / 2, palette: palette)))
        }
        items.append(value)
        if let intel = model.intel {
            items.append(intelItem(intel, model: model, sub: sub, key: key, frame: frame, baseline: figureLine, accents: accents,
                                   look: look))
        }
        return items
    }

    /// The 性能 cell: right-aligned on the value's baseline; live one step quieter than the value, resting and `–`
    /// quieter still, the proof mark under an unproven score (drawn in the cell's cached image). Its text changes in
    /// place; a fall, live ↔ resting and the proof mark crossfade.
    private func intelItem(_ score: BoardRowScore, model: BoardRowModel, sub: Bool, key: String, frame: CGRect, baseline: CGFloat?,
                           accents: BoardRowAccents, look: AnyHashable) -> BoardContentItem {
        let palette = self.palette, neon = self.neon
        let right = intelRight(frame.width)
        let cell = CGRect(x: right - intelCell - 4, y: 0, width: intelCell + 8, height: frame.height)
        let live = score.live
        let large = baseline != nil
        let font = Self.intelFont(style: style, sub: sub, live: live, large: large)
        let valueFont = Self.valueFont(style: style, sub: sub, idle: false, large: large)
        let text = score.text
        var item = BoardContentItem.part(key + "#iq", of: frame, rect: cell.insetBy(dx: neon ? -8 : 0, dy: 0), motion: .row,
                                         revision: AnyHashable([AnyHashable(score), AnyHashable(sub), AnyHashable(baseline), look])) { context in
            let color = live ? (neon ? palette.led.opacity(0.78) : palette.ink2) : palette.quiet
            let label = BoardLabel(text, font, color: color)
            let line = baseline ?? BoardLabel(text, valueFont, color: palette.ink).baseline(centeredOn: frame.height / 2)
            let glow = neon && live && palette.dark && palette.glows ? BoardPalette.ledGlow : nil
            label.draw(in: context, x: right - label.width, baseline: line, glow: glow, blur: 6)
            if score.provisional {
                BoardScoreNumber.drawUnderline(in: context, x: right - label.width, baseline: line, width: label.width,
                                               textSize: font.size, color: color, highContrast: palette.highContrast)
            }
        }
        if let fade = accents.intelFade { item.accents.append(.crossfade(fade)) }
        return item
    }

    /// The captions line at the top of the ranking: 性能 over the cells, `tok/s` over the values, and a hairline from
    /// the rank column to the capsule column's end (it replaces glass's top rule).
    func captions(frame: CGRect, intel: String, unit: String) -> BoardContentItem {
        let palette = self.palette
        let font = Self.captionFont(style)
        let intelLabel = BoardLabel(intel, font, color: palette.ink3)
        let unitLabel = BoardLabel(unit, font, color: palette.ink3)
        let width = frame.width
        let intelRight = self.intelRight(width), valueRight = self.valueRight(width)
        let lineEnd = intelRight - intelCell - 8 - 2
        var item = BoardContentItem(key: "captions", frame: frame, motion: .row,
                                    revision: AnyHashable([AnyHashable(intel), AnyHashable(unit), AnyHashable(intelCell),
                                                           AnyHashable(palette.dark), AnyHashable(palette.highContrast)])) { context, box in
            let mid = box.height / 2
            BoardDrawing.fill(CGRect(x: 6, y: (mid - 0.5).rounded(), width: max(0, lineEnd - 6), height: 1), color: palette.line, in: context)
            intelLabel.draw(in: context, x: intelRight - intelLabel.width, baseline: intelLabel.baseline(centeredOn: mid))
            unitLabel.draw(in: context, x: valueRight - unitLabel.width, baseline: unitLabel.baseline(centeredOn: mid))
        }
        item.clips = false
        return item
    }

    static func captionFont(_ style: BoardStyle) -> BoardFont {
        style == .neon ? BoardFont(family: .mono, size: 8.5, weight: .semibold, tracking: 0.12)
            : BoardFont(size: 9, weight: .semibold, tracking: 0.06)
    }

    /// `large`: a two-line group row's, 2 pt larger.
    static func intelFont(style: BoardStyle, sub: Bool, live: Bool, large: Bool = false) -> BoardFont {
        let size: CGFloat = (style == .neon ? (sub ? 10 : 11) : (sub ? 10.5 : 11)) + (large ? CGFloat(BoardGeometry.figureGrowth) : 0)
        return style == .neon ? BoardFont(family: .mono, size: size, weight: .semibold, tracking: -0.02)
            : BoardFont(size: size, weight: live ? .semibold : .medium, tabular: true)
    }

    /// The share underline as an item of its own along the row's bottom, drawn at its full width and stretched to the
    /// share by its layer: a new share eases across without a redraw.
    private func underline(_ model: BoardRowModel, sub: Bool, key: String, frame: CGRect, look: AnyHashable) -> BoardContentItem {
        let rows = self, box = frame.size
        let reach = (neon ? 8 : 6) * 1.5 + 2
        let left: CGFloat = sub ? 46 : 40
        let rect = CGRect(x: left - reach, y: box.height - 3 - reach, width: box.width - left + reach, height: reach + 3)
        var item = BoardContentItem.part(key + "#line", of: frame, rect: rect, motion: .row,
                                         revision: AnyHashable([AnyHashable(sub), look])) { context in
            var full = model
            full.share = 200
            rows.drawUnderline(full, sub: sub, ink: .white, in: context, size: box)
        }
        // The bar and its glow are one heat color at varying alpha: drawn once in white, tinted by its layer.
        item.tint = palette.heat(max(0.12, model.heatFraction)).cgColor
        item.stretch = model.shareFraction
        // The bar grows from its start, inside the part's frame (whole points, so the start sits a little in).
        item.stretchAnchor = frame.minX + left - item.frame.minX
        return item
    }

    // MARK: Drawing

    private func draw(_ model: BoardRowModel, column: CGFloat, in context: CGContext, size box: CGSize) {
        if model.kind == .more { return drawMore(model, in: context, size: box) }
        let sub = model.kind == .sub
        let midY = box.height / 2
        let line1 = firstBaseline(model)
        if model.isLeader { drawLeaderPlate(in: context, size: box) }

        if let source = model.source {
            // Two lines: the badge centres on line 1's band (its center 11 under the top).
            let badgeTop = line1 != nil ? 6 : midY - 5
            BoardDrawing.badge(model.phase, color: palette.source(source), idle: palette.ink3, look: neon ? .glowDot : .dots,
                               glows: palette.glows, at: CGPoint(x: 30, y: badgeTop), in: context)
        }

        let nameX: CGFloat = 48 + (sub ? 12 : 0)
        let capsuleX = self.capsuleX(box.width, column: column)
        let nameWidth = max(0, capsuleX - 8 - nameX)
        if sub { BoardDrawing.connector(x: 50, midY: midY, color: palette.line, in: context) }

        let nameFont = sub ? BoardFont(size: 11.5, weight: .medium) : BoardFont(size: 12.5, weight: .semibold)
        let detailFont = neon ? BoardFont(family: .mono, size: 10, weight: .medium, tracking: 0.02)
            : BoardFont(size: line1 != nil ? 10.5 : 11)
        let name = BoardLabel(model.name, nameFont, color: sub ? palette.ink2 : palette.ink)
        let detail = BoardLabel(model.detail, detailFont, color: palette.ink3)
        if let line1 {
            // The detail moves under the name and has line 2 to itself up to the value's edge, truncating at its tail;
            // the capsule sits in line 1's 16 pt band.
            name.truncated(to: nameWidth).draw(in: context, x: nameX, baseline: line1)
            if !detail.isEmpty {
                detail.truncated(to: max(0, figuresLeft(box.width) - nameX))
                    .draw(in: context, x: nameX, baseline: CGFloat(geometry.secondLineBaseline))
            }
        } else {
            let split = BoardContentRules.nameSplit(available: Double(nameWidth), name: Double(name.width))
            let baseline = name.baseline(centeredOn: midY)
            name.truncated(to: CGFloat(split.name)).draw(in: context, x: nameX, baseline: baseline)
            if !detail.isEmpty, split.detail > 4 {
                detail.truncated(to: CGFloat(split.detail)).draw(in: context, x: nameX + CGFloat(split.name) + 6, baseline: baseline)
            }
        }

        if column > 0, let capsule = model.capsule {
            let capsuleMid = line1 != nil ? 12 : midY
            capsules.drawCapsule(capsule, compact: compact, dark: palette.dark, highContrast: palette.highContrast,
                                 tint: EffortCapsuleTint(ink: palette.ink3.color, line: palette.line.color), width: column,
                                 in: context, at: CGPoint(x: capsuleX, y: capsuleMid - EffortCapsule.height / 2))
        }
    }

    private func drawLeaderPlate(in context: CGContext, size box: CGSize) {
        let rect = CGRect(origin: .zero, size: box)
        if palette.highContrast {
            BoardDrawing.fill(rect, radius: neon ? 9 : 10, color: BoardPalette.gold.opacity(palette.dark ? 0.16 : 0.2), in: context)
            BoardDrawing.stroke(rect, radius: neon ? 9 : 10, width: 1, color: BoardPalette.gold, in: context)
            return
        }
        if neon {
            BoardDrawing.gradient(rect, radius: 9, stops: [(BoardPalette.gold.opacity(0.12), 0),
                                                            (RGBColor(hex: 0xE879F9, alpha: 0.07), 0.45),
                                                            (RGBColor(hex: 0xE879F9, alpha: 0), 0.75)],
                                  from: .zero, to: CGPoint(x: box.width, y: box.width * 0.18), in: context)
        } else {
            let gold = palette.dark ? BoardPalette.gold : RGBColor(hex: 0xE9A800)
            let alpha = palette.dark ? 0.11 : 0.12
            BoardDrawing.gradient(rect, radius: 10, stops: [(gold.opacity(alpha), 0), (gold.opacity(0.03), 0.55), (gold.opacity(0), 1)],
                                  from: .zero, to: CGPoint(x: box.width, y: 0), in: context)
        }
    }

    private func drawRank(_ rank: Int, leader: Bool, midY: CGFloat, baseline line: CGFloat? = nil, x left: CGFloat, in context: CGContext) {
        let font = neon ? BoardFont(family: .mono, size: 12, weight: .bold) : BoardFont(size: 12, weight: .semibold, tabular: true)
        let label = BoardLabel("\(rank)", font, color: palette.ink3)
        let x = left + (16 - label.width) / 2
        let baseline = line ?? label.baseline(centeredOn: midY)
        guard leader else { return label.draw(in: context, x: x, baseline: baseline) }
        if palette.highContrast {
            return BoardLabel("\(rank)", font, color: palette.dark ? RGBColor(hex: 0xFFE58A) : RGBColor(hex: 0x8A5A00))
                .draw(in: context, x: x, baseline: baseline)
        }
        if neon {
            let color = palette.dark ? RGBColor(hex: 0xFFE58A) : RGBColor(hex: 0xB07800)
            BoardLabel("\(rank)", font, color: color).draw(in: context, x: x, baseline: baseline,
                                                         glow: palette.dark ? BoardPalette.gold.opacity(0.9) : nil, blur: 8)
        } else {
            let colors = palette.dark ? [RGBColor(hex: 0xFFE58A), RGBColor(hex: 0xE9A800)] : [RGBColor(hex: 0xE0A000), RGBColor(hex: 0x9A6500)]
            let top = line.map { $0 - 12 } ?? midY - 8
            label.drawGradient(in: context, x: x, baseline: baseline, colors: colors, start: CGPoint(x: 0, y: top),
                               end: CGPoint(x: 0, y: top + 16))
        }
    }

    /// The underline in the row's heat color, or in `ink` (white: the coverage its tinted layer fills).
    private func drawUnderline(_ model: BoardRowModel, sub: Bool, ink: RGBColor? = nil, in context: CGContext, size box: CGSize) {
        let left: CGFloat = sub ? 46 : 40
        let track = box.width - 6 - left
        let width = track * CGFloat(model.shareFraction)
        guard width > 0.5 else { return }
        let height: CGFloat = sub ? 1.5 : 2
        let rect = CGRect(x: left, y: box.height - 1 - height, width: width, height: height)
        let heat = (ink ?? palette.heat(max(0.12, model.heatFraction))).opacity(sub ? 0.7 : 1)
        if palette.highContrast { return BoardDrawing.fill(rect, radius: 1, color: heat.opacity(1), in: context) }
        let start = neon ? heat.opacity(0) : heat.opacity(heat.alpha * 0.35)
        BoardDrawing.glowing(heat.opacity(neon ? heat.alpha : heat.alpha * 0.7), blur: neon ? 8 : 6, in: context) {
            BoardDrawing.gradient(rect, radius: height, stops: [(start, 0), (heat, 1)], from: CGPoint(x: rect.minX, y: 0),
                                  to: CGPoint(x: rect.maxX, y: 0), in: $0)
        }
    }

    private func valueFont(sub: Bool, idle: Bool) -> BoardFont {
        Self.valueFont(style: style, sub: sub, idle: idle)
    }

    /// `large`: a two-line group row's, 2 pt larger.
    static func valueFont(style: BoardStyle, sub: Bool, idle: Bool, large: Bool = false) -> BoardFont {
        let neon = style == .neon
        let size: CGFloat = (sub ? (neon ? 11 : 11.5) : 13) + (large ? CGFloat(BoardGeometry.figureGrowth) : 0)
        let weight: NSFont.Weight = idle ? .medium : sub && !neon ? .semibold : neon ? .bold : .semibold
        return neon ? BoardFont(family: .mono, size: size, weight: weight, tracking: -0.02)
            : BoardFont(size: size, weight: weight, tabular: true)
    }

    private func drawValue(_ model: BoardRowModel, sub: Bool, right: CGFloat, midY: CGFloat, baseline line: CGFloat? = nil, hot: Bool,
                           in context: CGContext) {
        let font = Self.valueFont(style: style, sub: sub, idle: model.idle, large: line != nil)
        let color = model.idle ? palette.ink3
            : model.heatFraction > 0.45 ? palette.heatText(model.heatFraction)
            : sub ? palette.ink2 : palette.ink
        guard hot else {
            let label = BoardLabel(model.value, font, color: color)
            return label.draw(in: context, x: right - label.width, baseline: line ?? label.baseline(centeredOn: midY))
        }
        // Rising: white-hot with the row's heat around it (`valUp` / neon `vu`).
        let label = BoardLabel(model.value, font, color: palette.dark ? (neon ? .white : HeatPalette.whiteHot) : palette.ink)
        let glow = palette.glows ? palette.heat(max(0.12, model.heatFraction)) : nil
        label.draw(in: context, x: right - label.width, baseline: (line ?? label.baseline(centeredOn: midY)) - 2, glow: glow,
                   blur: neon ? 12 : 10)
    }

    private func drawMore(_ model: BoardRowModel, in context: CGContext, size box: CGSize) {
        let midY = box.height / 2
        let colors = model.sources.map { palette.source($0) }
        let diameter: CGFloat = neon ? 5 : 6
        let ring = neon ? nil : Optional(BoardHostView.slabColor(style, dark: palette.dark).opacity(1))
        let origin = CGPoint(x: 60 - (neon ? 1 : 2), y: midY - diameter / 2 + (neon ? 0 : 0.5))
        let stack = BoardDrawing.stack(colors, diameter: diameter, overlap: neon ? 1 : 2, ring: ring, glows: palette.glows,
                                       at: origin, in: context)
        let font = neon ? BoardFont(family: .mono, size: 10, weight: .medium, tracking: 0.03) : BoardFont(size: 11)
        let label = BoardLabel(model.name, font, color: palette.ink3)
        let x = origin.x + stack + 6
        label.truncated(to: box.width - 6 - x).draw(in: context, x: x, baseline: label.baseline(centeredOn: midY))
    }

    private func isGroup(_ row: BoardPlannedRow) -> Bool {
        if case .group = row.kind { return true }
        return false
    }
}
