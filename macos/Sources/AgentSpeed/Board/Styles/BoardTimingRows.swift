import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Timing tower rows (`board-timing.html` `.row` grid: 24 pt position block, 3 pt team stripe in the source color,
/// name, capsule column, 38 pt interval, 46 pt value, 7 pt gaps, 4 pt padding; S drops the interval). With the live
/// metrics (DESIGN 2c *Ranking rows*): a 34 pt interval and the 性能 cell before a 42 pt value (S 40), the captions
/// line and the capsule's word dropped below L. At M and L each group row has a second line (the detail), the position
/// block and stripe spanning both, and its figures (性能 and the value) 2 pt larger, centred on the row; their columns
/// widen to fit them.
@MainActor
struct BoardTimingRows {
    let size: BoardSize
    let palette: BoardPalette
    let capsules: BoardCapsuleDrawing
    let geometry: BoardGeometry
    let intelCell: CGFloat
    /// The value column: at least as wide as the widest value in a two-line group row's font.
    private let valueColumn: CGFloat

    init(size: BoardSize, palette: BoardPalette, capsules: BoardCapsuleDrawing, geometry: BoardGeometry? = nil, caption: String = "") {
        self.size = size
        self.palette = palette
        self.capsules = capsules
        let geometry = geometry ?? BoardGeometry(.timing, size)
        self.geometry = geometry
        let large = geometry.twoLine
        if geometry.hasIntelligence {
            let digits = BoardLabel(PerfFormat.measureText, Self.intelFont(sub: false, live: true, large: large), color: palette.ink).width
            let label = BoardLabel(caption, Self.captionFont, color: palette.ink3).width
            intelCell = max(CGFloat(geometry.intelligenceColumn), digits.rounded(.up), label.rounded(.up))
        } else {
            intelCell = 0
        }
        let base = geometry.hasIntelligence ? CGFloat(geometry.valueColumn) : (size == .small ? 44 : 46)
        valueColumn = large ? max(base, BoardFigures.widest(Self.valueFont(sub: false, idle: false, large: true), color: palette.ink))
            : base
    }

    /// The capsule drops its word at S, and below L while the 性能 column shows (meter and bolts stay).
    var compact: Bool { size == .small || (intelCell > 0 && size != .large) }

    func frame(_ row: BoardPlannedRow, rowsTop: CGFloat, pad: CGFloat) -> CGRect {
        CGRect(x: pad - 4, y: rowsTop + CGFloat(row.y), width: CGFloat(size.side) - 2 * pad + 8, height: CGFloat(row.height))
    }

    func rankCenter(_ frame: CGRect) -> CGPoint { CGPoint(x: frame.minX + 16, y: frame.midY) }

    // MARK: Columns

    private var legacyInterval: CGFloat { size == .small ? 0 : 38 }
    private var valueWidth: CGFloat { valueColumn }
    private var intervalWidth: CGFloat { intelCell > 0 ? CGFloat(geometry.intervalColumn) : legacyInterval }

    private func valueX(_ width: CGFloat) -> CGFloat { width - 4 - valueWidth }
    private func intelRight(_ width: CGFloat) -> CGFloat { valueX(width) - 7 }
    private func intervalX(_ width: CGFloat) -> CGFloat {
        intelCell > 0 ? intelRight(width) - intelCell - 7 - intervalWidth : valueX(width) - 7 - intervalWidth
    }

    private func capsuleX(_ width: CGFloat, column: CGFloat) -> CGFloat {
        // Without the 性能 column the original layout stands (its S keeps the interval's gap).
        guard intelCell > 0 else { return intervalX(width) - 7 - column }
        let right = intervalWidth > 0 ? intervalX(width) : intelRight(width) - intelCell
        return right - 7 - column
    }

    private func firstBaseline(_ model: BoardRowModel) -> CGFloat? {
        model.twoLine && model.rank != nil ? CGFloat(BoardGeometry.firstLineBaseline) : nil
    }

    /// A two-line group row's figures: larger, on one baseline centred on the row (nil: a one-line row's own).
    private func figures(_ model: BoardRowModel) -> BoardFigures? {
        guard model.twoLine, model.rank != nil else { return nil }
        let intel = model.intel.map { Self.intelFont(sub: false, live: $0.live, large: true) }
        return BoardFigures(centre: CGFloat(geometry.figureCentre),
                            fonts: [Self.valueFont(sub: false, idle: model.idle, large: true)] + (intel.map { [$0] } ?? []))
    }

    /// Where line 2 ends: the first figure column's left edge — the interval, else the 性能 cell, else the value.
    func figuresLeft(_ width: CGFloat) -> CGFloat {
        if intervalWidth > 0 { return intervalX(width) }
        return intelCell > 0 ? intelRight(width) - intelCell : valueX(width)
    }

    // MARK: Items

    /// The row's body, position number, value, interval and 性能 as items of their own (see `BoardListRows.items`).
    func items(_ model: BoardRowModel, key: String, frame: CGRect, column: CGFloat, accents: BoardRowAccents) -> [BoardContentItem] {
        let rows = self, look = AnyHashable([AnyHashable(palette.dark), AnyHashable(palette.highContrast)])
        var body = BoardContentItem(key: key, frame: frame, revision: AnyHashable([AnyHashable(model.body), AnyHashable(column),
                                                                                 AnyHashable(intelCell), look])) { context, box in
            rows.draw(model, column: column, in: context, size: box)
        }
        guard model.kind != .more else { return [body] }
        let sub = model.kind == .sub
        if let event = accents.event {
            let color = model.source.map { palette.source($0) } ?? .white
            body.accents.append(.decal(BoardRowDecals.sweep(width: frame.width, height: frame.height, color: color,
                                                           leader: event.kind == .newLeader, radius: 0)))
        }
        var items = [body]
        let line1 = firstBaseline(model)
        if intervalWidth > 0, !model.interval.isEmpty, !sub {
            items.append(interval(model, key: key, frame: frame, centre: figures(model)?.centre, look: look))
        }

        if let rank = model.rank {
            let tall: CGFloat = line1 != nil ? 26 : 22
            let block = CGRect(x: frame.minX + 4, y: frame.midY - tall / 2, width: 24, height: tall)
            var item = BoardContentItem(key: key + "#rank", frame: block, revision: AnyHashable([AnyHashable(rank), AnyHashable(tall), look])) { context, box in
                rows.drawPositionNumber(rank, leader: rank == 1, in: context, size: box)
            }
            item.clips = true
            if let old = accents.previousRank { item.accents.append(.roll(rising: rank < old)) }
            if accents.event != nil {
                item.accents.append(.decal(BoardDecal(frame: CGRect(origin: .zero, size: block.size), motion: .fade(seconds: 0.9)) { context, box in
                    BoardDrawing.fill(CGRect(origin: .zero, size: box), radius: 5, color: BoardPalette.green, in: context)
                    let label = BoardLabel("\(rank)", BoardFont(family: .mono, size: 12, weight: .heavy), color: RGBColor(hex: 0x04130A))
                    label.draw(in: context, x: (box.width - label.width) / 2, baseline: label.baseline(centeredOn: box.height / 2))
                }))
            }
            items.append(item)
        }

        let figures = self.figures(model)
        let figureLine = figures?.baseline
        let valueFrame = CGRect(x: frame.maxX - 70, y: frame.minY, width: 70, height: frame.height)
        var value = BoardContentItem(key: key + "#value", frame: valueFrame,
                                     revision: AnyHashable([AnyHashable(model.value), AnyHashable(model.idle), AnyHashable(figureLine),
                                                            look])) { context, box in
            rows.drawValue(model, sub: sub, baseline: figureLine, hot: false, in: context, size: box)
        }
        value.hot = BoardContentDrawing { context, box in
            rows.drawValue(model, sub: sub, baseline: figureLine, hot: true, in: context, size: box)
        }
        switch accents.change {
        case .rise: value.accents.append(.warm)
        case .fall: value.accents.append(.dim)
        case .steady: break
        }
        if let event = accents.event, event.from > event.to {
            value.accents.append(.decal(BoardRowDecals.chip(style: .timing, climbed: event.from - event.to, rowRight: 70,
                                                            midY: figures.map { $0.centre } ?? frame.height / 2, palette: palette)))
        }
        items.append(value)
        if let intel = model.intel {
            items.append(intelItem(intel, model: model, sub: sub, key: key, frame: frame, baseline: figureLine, accents: accents,
                                   look: look))
        }
        return items
    }

    /// `large`: a two-line group row's, 2 pt larger.
    static func intelFont(sub: Bool, live: Bool, large: Bool = false) -> BoardFont {
        BoardFont(family: .mono, size: (sub ? 10 : 11) + (large ? CGFloat(BoardGeometry.figureGrowth) : 0),
                  weight: live ? .bold : .semibold, tracking: -0.02)
    }

    static let captionFont = BoardFont(family: .mono, size: 8.5, weight: .bold, tracking: 0.1)

    private func intelItem(_ score: BoardRowScore, model: BoardRowModel, sub: Bool, key: String, frame: CGRect, baseline: CGFloat?,
                           accents: BoardRowAccents, look: AnyHashable) -> BoardContentItem {
        let palette = self.palette
        let right = intelRight(frame.width)
        let live = score.live
        let large = baseline != nil
        let font = Self.intelFont(sub: sub, live: live, large: large)
        let valueFont = Self.valueFont(sub: sub, idle: false, large: large)
        let text = score.text
        let cell = CGRect(x: right - intelCell - 4, y: 0, width: intelCell + 8, height: frame.height)
        var item = BoardContentItem.part(key + "#iq", of: frame, rect: cell, motion: .row,
                                         revision: AnyHashable([AnyHashable(score), AnyHashable(sub), AnyHashable(baseline), look])) { context in
            let color = live ? palette.ink2 : palette.quiet
            let label = BoardLabel(text, font, color: color)
            let line = baseline ?? BoardLabel(text, valueFont, color: palette.ink).baseline(centeredOn: frame.height / 2)
            label.draw(in: context, x: right - label.width, baseline: line)
            if score.provisional {
                BoardScoreNumber.drawUnderline(in: context, x: right - label.width, baseline: line, width: label.width,
                                               textSize: font.size, color: color, highContrast: palette.highContrast)
            }
        }
        if let fade = accents.intelFade { item.accents.append(.crossfade(fade)) }
        return item
    }

    /// The captions line: 間隔 over the intervals (not at S), 性能 over the cells, `TOK/S` over the values, a hairline
    /// from the position column to the capsule column's end.
    func captions(frame: CGRect, gap: String, intel: String, unit: String) -> BoardContentItem {
        let palette = self.palette, font = Self.captionFont
        let width = frame.width
        let gapLabel = BoardLabel(gap, font, color: palette.ink3)
        let intelLabel = BoardLabel(intel, font, color: palette.ink3)
        let unitLabel = BoardLabel(unit, font, color: palette.ink3)
        let intelRight = self.intelRight(width), valueRight = width - 4
        let intervalWidth = self.intervalWidth, intervalX = self.intervalX(width)
        let lineEnd = (intervalWidth > 0 ? intervalX : intelRight - intelCell) - 7 - 2
        return BoardContentItem(key: "captions", frame: frame, motion: .row,
                                revision: AnyHashable([AnyHashable(gap), AnyHashable(intel), AnyHashable(unit), AnyHashable(intelCell),
                                                       AnyHashable(palette.dark), AnyHashable(palette.highContrast)])) { context, box in
            let mid = box.height / 2
            BoardDrawing.fill(CGRect(x: 4, y: (mid - 0.5).rounded(), width: max(0, lineEnd - 4), height: 1), color: palette.line, in: context)
            if intervalWidth > 0 {
                gapLabel.draw(in: context, x: intervalX + intervalWidth - gapLabel.width, baseline: gapLabel.baseline(centeredOn: mid))
            }
            intelLabel.draw(in: context, x: intelRight - intelLabel.width, baseline: intelLabel.baseline(centeredOn: mid))
            unitLabel.draw(in: context, x: valueRight - unitLabel.width, baseline: unitLabel.baseline(centeredOn: mid))
        }
    }

    private func draw(_ model: BoardRowModel, column: CGFloat, in context: CGContext, size box: CGSize) {
        let midY = box.height / 2
        let nameX: CGFloat = 4 + 24 + 7 + 3 + 7
        let mono = BoardFont.Family.mono
        if model.kind == .more {
            let label = BoardLabel(model.name, BoardFont(family: mono, size: 10, weight: .semibold, tracking: 0.03),
                                   color: palette.ink3)
            let x = nameX + 10
            label.truncated(to: box.width - 4 - x).draw(in: context, x: x, baseline: label.baseline(centeredOn: midY))
            return
        }
        let sub = model.kind == .sub
        let line1 = firstBaseline(model)
        if !sub { BoardDrawing.fill(CGRect(x: 0, y: box.height - 1, width: box.width, height: 1), color: palette.line, in: context) }
        if model.rank != nil { drawPositionBlock(leader: model.isLeader, midY: midY, tall: line1 != nil ? 26 : 22, in: context) }
        if let source = model.source { drawStripe(palette.source(source), sub: sub, midY: midY, tall: line1 != nil ? 24 : 18, in: context) }

        let capsuleX = self.capsuleX(box.width, column: column)
        let start = nameX + (sub ? 10 : 0)
        let nameWidth = max(0, capsuleX - 7 - start)
        if sub {
            BoardDrawing.fill(CGRect(x: nameX, y: (midY - 0.5).rounded(), width: 6, height: 1), color: palette.ink3.opacity(palette.ink3.alpha * 0.6),
                              in: context)
        }
        let name = BoardLabel(model.name, sub ? BoardFont(size: 11, weight: .semibold) : BoardFont(size: 12.5, weight: .bold, tracking: 0.005),
                              color: sub ? palette.ink2 : palette.ink)
        let detail = BoardLabel(model.detail, BoardFont(family: mono, size: 10, weight: .semibold, tracking: 0.02), color: palette.ink3)
        if let line1 {
            // Line 2 is the detail's alone, up to the centred figures.
            name.truncated(to: nameWidth).draw(in: context, x: start, baseline: line1)
            if !detail.isEmpty {
                detail.truncated(to: max(0, figuresLeft(box.width) - start))
                    .draw(in: context, x: start, baseline: CGFloat(geometry.secondLineBaseline))
            }
        } else {
            let split = BoardContentRules.nameSplit(available: Double(nameWidth), name: Double(name.width))
            let baseline = name.baseline(centeredOn: midY)
            name.truncated(to: CGFloat(split.name)).draw(in: context, x: start, baseline: baseline)
            if !detail.isEmpty, split.detail > 4 {
                detail.truncated(to: CGFloat(split.detail)).draw(in: context, x: start + CGFloat(split.name) + 6, baseline: baseline)
            }
        }

        if column > 0, let modes = model.modes {
            let capsuleMid = line1 != nil ? 12 : midY
            capsules.drawCapsule(for: modes, compact: compact, dark: palette.dark, highContrast: palette.highContrast, width: column,
                                 in: context, at: CGPoint(x: capsuleX, y: capsuleMid - EffortCapsule.height / 2))
        }
    }

    /// The interval column as an item of its own (it changes on most ticks; the body stays cached). In a two-line
    /// group row it is a figure: at its own size, its cap-height centre on the row's centre line with the others'.
    private func interval(_ model: BoardRowModel, key: String, frame: CGRect, centre: CGFloat?, look: AnyHashable) -> BoardContentItem {
        let box = frame.size, intervalWidth = self.intervalWidth, palette = self.palette
        let intervalX = self.intervalX(box.width)
        let rect = CGRect(x: intervalX - 24, y: 0, width: intervalWidth + 28, height: box.height)
        let leader = model.isLeader
        let font = leader ? BoardFont(family: .mono, size: 9.5, weight: .heavy, tracking: 0.08)
            : BoardFont(family: .mono, size: 10.5, weight: .semibold)
        let baseline = centre.map { $0 + font.font.capHeight / 2 }
        return .part(key + "#gap", of: frame, rect: rect, motion: .row,
                     revision: AnyHashable([AnyHashable(model.interval), AnyHashable(model.isLeader), AnyHashable(baseline), look])) { context in
            let gold = palette.dark ? BoardPalette.gold : RGBColor(hex: 0x9A6B00)
            let label = BoardLabel(model.interval, font, color: leader ? gold : palette.ink3)
            label.draw(in: context, x: intervalX + intervalWidth - label.width, baseline: baseline ?? label.baseline(centeredOn: box.height / 2))
        }
    }

    /// `large`: a two-line group row's, 2 pt larger.
    static func valueFont(sub: Bool, idle: Bool, large: Bool = false) -> BoardFont {
        BoardFont(family: .mono, size: (sub ? 11 : 13) + (large ? CGFloat(BoardGeometry.figureGrowth) : 0),
                  weight: idle ? .semibold : sub ? .bold : .heavy, tracking: -0.02)
    }

    /// The value; rising, it flashes green (`tvu`) with a glow.
    private func drawValue(_ model: BoardRowModel, sub: Bool, baseline line: CGFloat? = nil, hot: Bool, in context: CGContext, size box: CGSize) {
        let font = Self.valueFont(sub: sub, idle: model.idle, large: line != nil)
        let color = hot ? BoardPalette.green : model.idle ? palette.ink3 : sub ? palette.ink2 : palette.ink
        let value = BoardLabel(model.value, font, color: color)
        let baseline = (line ?? value.baseline(centeredOn: box.height / 2)) - (hot ? 2 : 0)
        value.draw(in: context, x: box.width - 4 - value.width, baseline: baseline,
                   glow: hot && palette.glows ? BoardPalette.green.opacity(0.7) : nil, blur: 8)
    }

    private func drawPositionBlock(leader: Bool, midY: CGFloat, tall: CGFloat = 22, in context: CGContext) {
        let block = CGRect(x: 4, y: midY - tall / 2, width: 24, height: tall)
        guard leader else {
            BoardDrawing.fill(block, radius: 5, color: palette.block, in: context)
            if palette.highContrast { BoardDrawing.stroke(block, radius: 5, width: 1, color: palette.ink3, in: context) }
            return
        }
        if palette.highContrast { return BoardDrawing.fill(block, radius: 5, color: BoardPalette.gold, in: context) }
        BoardDrawing.glowing(BoardPalette.gold.opacity(0.35), blur: 12, in: context) {
            BoardDrawing.fill(block, radius: 5, color: BoardPalette.gold, in: $0)
        }
        BoardDrawing.gradient(block, radius: 5, stops: [(RGBColor(hex: 0xFFF1A8), 0), (BoardPalette.gold, 0.45), (RGBColor(hex: 0xD49B00), 1)],
                              from: CGPoint(x: block.minX + 8, y: block.minY), to: CGPoint(x: block.maxX - 8, y: block.maxY), in: context)
    }

    private func drawPositionNumber(_ rank: Int, leader: Bool, in context: CGContext, size box: CGSize) {
        let label = BoardLabel("\(rank)", BoardFont(family: .mono, size: 12, weight: .heavy),
                               color: leader ? RGBColor(hex: 0x241A00) : palette.blockInk)
        label.draw(in: context, x: (box.width - label.width) / 2, baseline: label.baseline(centeredOn: box.height / 2))
    }

    private func drawStripe(_ color: RGBColor, sub: Bool, midY: CGFloat, tall: CGFloat = 18, in context: CGContext) {
        let x: CGFloat = 4 + 24 + 7
        let stripe = sub ? CGRect(x: x + 1, y: midY - 5, width: 2, height: 10) : CGRect(x: x, y: midY - tall / 2, width: 3, height: tall)
        let tint = color.opacity(sub ? 0.7 : 1)
        BoardDrawing.glowing(palette.glows && !sub ? color.opacity(0.6) : nil, blur: 6, in: context) {
            BoardDrawing.fill(stripe, radius: 2, color: tint, in: $0)
        }
    }
}
