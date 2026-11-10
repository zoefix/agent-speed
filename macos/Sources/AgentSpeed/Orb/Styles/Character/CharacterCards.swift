import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Sizes of the readout cards for a disc of `D` points (DESIGN 2a.6, Readouts): row text `t`, headline numbers `p`
/// (性能, 費用), the plate's padding and the space between rows. Floors keep them legible on small orbs.
struct CharacterCardMetrics {
    let t: CGFloat
    let p: CGFloat
    let padX: CGFloat
    let padY: CGFloat
    let rowGap: CGFloat

    init(disc d: CGFloat) {
        t = max(8, 0.027 * d)
        p = 1.25 * t
        padX = 0.8 * t
        padY = 0.55 * t
        rowGap = 0.36 * t
    }

    /// A row's height, and its baseline below the row's top: CJK labels and digits share a visual center 0.36 em
    /// above the baseline.
    func rowHeight(_ size: CGFloat) -> CGFloat { 0.92 * size }
    func baseline(_ size: CGFloat) -> CGFloat { 0.82 * size }

    func number(_ size: CGFloat) -> OrbTextStyle {
        OrbTextStyle(size: size, weight: .semibold, monospacedDigits: true, padding: 1)
    }

    /// Glyphs (性能, 費用, the arrows) and units at 0.82 × their row (DESIGN 10).
    func glyph(_ size: CGFloat) -> OrbTextStyle {
        OrbTextStyle(size: OrbLinePlacement.unitScale * size, weight: .semibold, padding: 1)
    }

    func unit(_ size: CGFloat) -> OrbTextStyle {
        OrbTextStyle(size: OrbLinePlacement.unitScale * size, weight: .medium, padding: 1)
    }

    func label(_ size: CGFloat) -> OrbTextStyle { OrbTextStyle(size: size, weight: .medium, padding: 1) }
    func chip(_ size: CGFloat) -> OrbTextStyle { OrbTextStyle(size: size, weight: .semibold, padding: 1) }

    var glyphGap: CGFloat { 0.24 * t }
    var cellGap: CGFloat { OrbLinePlacement.gap * t }
}

/// The colors of the cards: frosted glass plates with a hairline rim, a soft shadow and a sheen along the top; text in
/// the value and secondary tokens; her accent on the edge facing her. Increase Contrast and Reduce Transparency make
/// the plates denser.
struct CharacterCardInk {
    let fill: RGBColor
    let rim: RGBColor
    let shadow: RGBColor
    let sheen: RGBColor
    let number: RGBColor
    let secondary: RGBColor
    let accent: RGBColor

    init(_ frame: OrbFrame, accent: RGBColor?) {
        let dark = frame.dark
        let contrast = frame.content.increaseContrast
        let solid = frame.content.reduceTransparency
        let glass = dark ? RGBColor(red: 18 / 255, green: 20 / 255, blue: 29 / 255) : RGBColor(red: 1, green: 1, blue: 1)
        fill = glass.opacity(solid ? 0.97 : contrast ? 0.92 : dark ? 0.64 : 0.8)
        rim = dark ? RGBColor.white.opacity(contrast ? 0.4 : 0.15) : RGBColor.black.opacity(contrast ? 0.32 : 0.085)
        shadow = RGBColor.black.opacity(dark ? 0.3 : 0.11)
        sheen = RGBColor.white.opacity(dark ? 0.1 : 0.75)
        let ink = dark ? RGBColor.white : RGBColor(hex: 0x0B0D12)
        number = ink.opacity(contrast ? 0.98 : 0.9)
        secondary = (dark ? RGBColor.white : RGBColor.black).opacity(contrast ? 0.74 : 0.55)
        self.accent = accent ?? RGBColor(red: 0.85, green: 0.47, blue: 0.34)
    }
}

/// Measures and draws the readout cards (DESIGN 2a.6, Readouts). Every card is a plate centered on its place, its
/// rows laid out from the top left; `size` and `draw` share each row's arithmetic, so a plate always fits its text.
/// Numbers are drawn character by character from the shared glyph atlas, so a changing value never rasterizes text.
@MainActor
struct CharacterCards {
    let frame: OrbFrame
    let character: OrbCharacterContent?
    let m: CharacterCardMetrics
    let backing: CGFloat
    /// The 首字 row's fade and the models card's rows, carried from frame to frame by the renderer.
    let dynamics: CharacterCardDynamics
    let cache = OrbTextCache.shared

    init(frame: OrbFrame, character: OrbCharacterContent?, backing: CGFloat,
         dynamics: CharacterCardDynamics = CharacterCardDynamics()) {
        self.frame = frame
        self.character = character
        m = CharacterCardMetrics(disc: frame.s)
        self.backing = backing
        self.dynamics = dynamics
    }

    var language: AppLanguage { frame.content.language }
    var readings: OrbCharacterReadings? { character?.readings }
    var readouts: CharacterReadouts { character?.readouts ?? .default }
    var reduceMotion: Bool { frame.effects.reduceMotion }

    // MARK: Sizes

    /// The card's size in points (padding included); zero when it has nothing to draw.
    func size(_ card: HudCard, model: String?) -> (w: Double, h: Double) {
        let size: CGSize = switch card {
        case .speed: .zero
        case .model: model.map(chipSize) ?? .zero
        case .performance: performanceSize()
        case .network: networkSize()
        case .cost: costSize()
        case .agents: agentsSize()
        case .models: modelsSize()
        case .quota: quotaSize()
        }
        return (Double(size.width), Double(size.height))
    }

    func plate(_ content: CGSize) -> CGSize {
        CGSize(width: content.width + 2 * m.padX, height: content.height + 2 * m.padY)
    }

    // MARK: Performance

    private struct PerformanceRows {
        var score: OrbScoreMotion.Frame?
        var scoreWidth: CGFloat = 0
        var thinking: String?
        var thinkingWidth: CGFloat = 0
        /// The thinking row's opacity and its share of its room (it fades, then the card closes up).
        var thinkingAlpha = 1.0
        var thinkingShare: CGFloat = 1
        /// 首字: the value it shows (or showed, while it fades out), its eased presence as opacity and share of its room.
        var latency: CharacterReadoutText.Latency?
        var latencyWidth: CGFloat = 0
        var latencyShare: CGFloat = 0
    }

    private func performanceRows() -> PerformanceRows? {
        guard let reading = readings?.performance, case .intelligence(_, let thinking) = reading.content else { return nil }
        var rows = PerformanceRows()
        let glyphs = OrbLineGlyphs(language)
        if let latency = dynamics.latency, dynamics.latencyPresence > 0 {
            rows.latency = latency
            rows.latencyShare = CGFloat(Easing.smoothstep(0, 1, dynamics.latencyPresence))
            rows.latencyWidth = advance(Localizer.string("character.latency.glyph", language: language), m.glyph(m.t))
                + m.glyphGap + digits(latency.number, m.number(m.t)) + m.cellGap + advance(latency.unit, m.unit(m.t))
        }
        if readouts.intelligence {
            let score = reading.scoreMotion.frame(at: frame.time, reduceMotion: reduceMotion)
            rows.score = score
            rows.scoreWidth = advance(glyphs.intelligence, m.glyph(m.p)) + m.glyphGap * 1.25
                + max(digits(score.text, m.number(m.p)), score.outgoing.map { digits($0, m.number(m.p)) } ?? 0)
        }
        if readouts.thinking {
            rows.thinking = thinking
            rows.thinkingWidth = advance(glyphs.thinking, m.glyph(m.t)) + m.glyphGap + digits(thinking, m.number(m.t))
                + m.cellGap + advance(OrbLineMetrics.thinkingUnit, m.unit(m.t))
            // Alone it never hides (DESIGN 10, Thinking hidden at zero).
            if rows.score != nil {
                rows.thinkingAlpha = reading.thinking.alpha(at: frame.time, reduceMotion: reduceMotion)
                let single = reading.thinking.single(at: frame.time, reduceMotion: reduceMotion)
                rows.thinkingShare = 1 - CGFloat(Easing.smoothstep(0, 1, single))
            }
        }
        return rows.score == nil && rows.thinking == nil && rows.latency == nil ? nil : rows
    }

    private func performanceSize() -> CGSize {
        guard let rows = performanceRows() else { return .zero }
        var height: CGFloat = 0
        if rows.score != nil { height += m.rowHeight(m.p) }
        if rows.thinking != nil {
            height += rows.thinkingShare * ((rows.score != nil ? m.rowGap : 0) + m.rowHeight(m.t))
        }
        if rows.latency != nil {
            height += rows.latencyShare * ((rows.score != nil || rows.thinking != nil ? m.rowGap : 0) + m.rowHeight(m.t))
        }
        let width = max(rows.scoreWidth, rows.thinkingShare * rows.thinkingWidth, rows.latencyShare * rows.latencyWidth)
        return plate(CGSize(width: width, height: height))
    }

    // MARK: Network

    private func networkSize() -> CGSize {
        guard let reading = readings?.network, case .traffic = reading.content else { return .zero }
        let cells = OrbLineMetrics.cells(size: m.t, bits: readings?.bits ?? false)
        return plate(CGSize(width: cells.lineWidth(size: m.t), height: 2 * m.rowHeight(m.t) + m.rowGap))
    }

    // MARK: Cost

    private func costSize() -> CGSize {
        guard let money = readings?.cost else { return .zero }
        return plate(CGSize(width: costWidth(money), height: m.rowHeight(m.p)))
    }

    private func costWidth(_ money: LiveMoney) -> CGFloat {
        var width = advance(character?.costGlyph ?? "", m.glyph(m.p)) + m.glyphGap * 1.25 + digits(money.number, m.number(m.p))
        if money.atLeast { width += advance("≥", m.unit(m.p)) + 0.08 * m.p }
        if !money.unknown { width += m.cellGap + advance(money.unit, m.unit(m.t)) }
        return width
    }

    // MARK: Agents

    private func agentsSize() -> CGSize {
        let lines = [character?.todayLine, character?.agentsLine]
        var width: CGFloat = 0, rows = 0
        for case let line? in lines {
            width = max(width, advance(line, m.label(m.t)))
            rows += 1
        }
        guard rows > 0 else { return .zero }
        return plate(CGSize(width: width, height: CGFloat(rows) * m.rowHeight(m.t) + CGFloat(rows - 1) * m.rowGap))
    }

    // MARK: Model chip

    private func chipSize(_ name: String) -> CGSize {
        let style = m.chip(m.t)
        let dot = 0.62 * m.t
        return CGSize(width: advance(name, style) + dot + 0.42 * m.t + 2 * 0.85 * m.t, height: m.rowHeight(m.t) + 2 * 0.5 * m.t)
    }

    // MARK: Drawing

    /// Draws a card at its place: the plate, then its rows.
    /// `right`: the card is at her right, so its accent edge is its left one.
    func draw(_ card: HudCard, at place: HudWings.Place, size: (w: Double, h: Double), model: String?, right: Bool,
              into scene: inout OrbScene) {
        let alpha = place.alpha * frame.alpha
        guard alpha > 0.01, size.w > 0, size.h > 0 else { return }
        let pen = CharacterBodyPainter(frame: frame, center: CGPoint(x: place.x, y: place.y), scale: CGFloat(place.scale),
                                       spin: place.spin, alpha: alpha, metrics: CharacterReadoutMetrics(disc: frame.s),
                                       contrast: frame.content.increaseContrast)
        let ink = CharacterCardInk(frame, accent: character?.accent)
        let box = CGSize(width: size.w, height: size.h)
        let origin = CGPoint(x: -box.width / 2 + m.padX, y: -box.height / 2 + m.padY)
        switch card {
        case .speed:
            return
        case .model:
            guard let model else { return }
            drawPlate(pen, box: box, ink: ink, corner: box.height / 2, spine: nil, into: &scene)
            drawChip(model, pen, box: box, ink: ink, into: &scene)
        case .performance:
            guard let rows = performanceRows() else { return }
            drawPlate(pen, box: box, ink: ink, spine: right ? -1 : 1, into: &scene)
            drawPerformance(rows, pen, origin: origin, ink: ink, into: &scene)
        case .network:
            guard let reading = readings?.network, case .traffic(let rx, let tx) = reading.content else { return }
            drawPlate(pen, box: box, ink: ink, spine: right ? -1 : 1, into: &scene)
            let cells = OrbLineMetrics.cells(size: m.t, bits: readings?.bits ?? false)
            drawTraffic(rx, down: true, pen, left: origin.x, top: origin.y, cells: cells, ink: ink, into: &scene)
            drawTraffic(tx, down: false, pen, left: origin.x, top: origin.y + m.rowHeight(m.t) + m.rowGap, cells: cells,
                        ink: ink, into: &scene)
        case .cost:
            guard let money = readings?.cost else { return }
            drawPlate(pen, box: box, ink: ink, spine: right ? -1 : 1, into: &scene)
            drawCost(money, pen, origin: origin, ink: ink, into: &scene)
        case .models:
            guard modelsLayout() != nil else { return }
            drawPlate(pen, box: box, ink: ink, spine: right ? -1 : 1, into: &scene)
            drawModels(pen, origin: origin, box: box, ink: ink, into: &scene)
        case .quota:
            drawPlate(pen, box: box, ink: ink, spine: right ? -1 : 1, into: &scene)
            drawQuota(pen, origin: origin, box: box, ink: ink, into: &scene)
        case .agents:
            drawPlate(pen, box: box, ink: ink, spine: right ? -1 : 1, into: &scene)
            var top = origin.y
            for (line, color) in [(character?.todayLine, ink.number), (character?.agentsLine, ink.secondary)] {
                guard let line, let texture = cache.texture(line, style: m.label(m.t), language: language, scale: scale(pen))
                else { continue }
                scene.add(pen.text(texture, centerX: origin.x + texture.advance / 2, baseline: top + m.baseline(m.t),
                                   color: color.opacity(pen.alpha)))
                top += m.rowHeight(m.t) + m.rowGap
            }
        }
    }

    /// The plate: a soft shadow, the hairline rim, the frosted fill, a sheen along the top, and her accent on the edge
    /// facing her (`spine` 1 the right edge, -1 the left one, nil none).
    func drawPlate(_ pen: CharacterBodyPainter, box: CGSize, ink: CharacterCardInk, corner: CGFloat? = nil,
                   spine: CGFloat?, into scene: inout OrbScene) {
        let s = pen.scale
        let a = pen.alpha
        let half = CGSize(width: box.width / 2 * s, height: box.height / 2 * s)
        let r = min(corner ?? 0.72 * m.t, box.height / 2) * s
        scene.add(.roundedRect(center: pen.point(0, 0.2 * m.t), halfSize: half, rotation: pen.spin, corner: r,
                               color: ink.shadow.opacity(a), core: 0, glow: 0.9 * m.t * s, glowAlpha: 1), blend: .over)
        scene.add(.roundedRect(center: pen.center, halfSize: CGSize(width: half.width + 0.6, height: half.height + 0.6),
                               rotation: pen.spin, corner: r + 0.6, color: ink.rim.opacity(a)), blend: .over)
        scene.add(.roundedRect(center: pen.center, halfSize: half, rotation: pen.spin, corner: r, color: ink.fill.opacity(a)),
                  blend: .over)
        let inset = r / s + 0.15 * m.t
        if box.width / 2 > inset {
            let y = -box.height / 2 + 0.75
            scene.add(.capsule(from: pen.point(-box.width / 2 + inset, y), to: pen.point(box.width / 2 - inset, y),
                               width: 0.9 * s, color: ink.sheen.opacity(a)), blend: .over)
        }
        guard let spine else { return }
        let x = spine * (box.width / 2 - 0.3 * m.t)
        let h = 0.3 * box.height
        scene.add(.capsule(from: pen.point(x, -h), to: pen.point(x, h), width: max(1.2, 0.15 * m.t) * s,
                           color: ink.accent.opacity(0.9 * a)), blend: .over)
    }

    private func drawPerformance(_ rows: PerformanceRows, _ pen: CharacterBodyPainter, origin: CGPoint,
                                 ink: CharacterCardInk, into scene: inout OrbScene) {
        let glyphs = OrbLineGlyphs(language)
        var top = origin.y
        if let score = rows.score {
            let baseline = top + m.baseline(m.p)
            var x = origin.x
            x += put(glyphs.intelligence, m.glyph(m.p), pen, x: x, baseline: baseline, color: ink.secondary, into: &scene)
            x += m.glyphGap * 1.25
            let color = ink.number.mix(ink.secondary, score.pale)
            if let outgoing = score.outgoing {
                putDigits(outgoing, m.number(m.p), pen, x: x, baseline: baseline,
                          color: color.opacity(score.outgoingAlpha), into: &scene)
            }
            let width = putDigits(score.text, m.number(m.p), pen, x: x, baseline: baseline,
                                  color: color.opacity(score.alpha), into: &scene)
            drawProvisionalMark(score, pen, x: x, width: width, baseline: baseline, color: color, into: &scene)
            top += m.rowHeight(m.p) + m.rowGap
        }
        if let thinking = rows.thinking {
            var rowTop = top
            if rows.score != nil { rowTop -= (1 - rows.thinkingShare) * (m.rowHeight(m.t) + m.rowGap) }
            if rows.thinkingAlpha > 0.01 {
                drawRow(glyphs.thinking, number: thinking, unit: OrbLineMetrics.thinkingUnit, pen, x: origin.x,
                        baseline: rowTop + m.baseline(m.t), alpha: rows.thinkingAlpha, ink: ink, into: &scene)
            }
            top += rows.thinkingShare * (m.rowHeight(m.t) + m.rowGap)
        }
        // 首字 fades where it stands as the card eases to its height.
        guard let latency = rows.latency, rows.latencyShare > 0.01 else { return }
        drawRow(Localizer.string("character.latency.glyph", language: language), number: latency.number, unit: latency.unit,
                pen, x: origin.x, baseline: top + m.baseline(m.t), alpha: Double(rows.latencyShare), ink: ink, into: &scene)
    }

    /// A row at `t`: its glyph, the number in the value token and the unit in the secondary one.
    private func drawRow(_ glyph: String, number: String, unit: String, _ pen: CharacterBodyPainter, x left: CGFloat,
                         baseline: CGFloat, alpha a: Double, ink: CharacterCardInk, into scene: inout OrbScene) {
        var x = left
        x += put(glyph, m.glyph(m.t), pen, x: x, baseline: baseline, color: ink.secondary.opacity(a), into: &scene)
        x += m.glyphGap
        x += putDigits(number, m.number(m.t), pen, x: x, baseline: baseline, color: ink.number.opacity(a), into: &scene)
        x += m.cellGap
        put(unit, m.unit(m.t), pen, x: x, baseline: baseline, color: ink.secondary.opacity(a), into: &scene)
    }

    /// The dotted mark under a provisional score (DESIGN 12, *Provisional mark*); it takes no width.
    private func drawProvisionalMark(_ score: OrbScoreMotion.Frame, _ pen: CharacterBodyPainter, x: CGFloat, width: CGFloat,
                                     baseline: CGFloat, color: RGBColor, into scene: inout OrbScene) {
        guard score.provisional > 0.01, score.text != OrbScoreMotion.absent else { return }
        let contrast = frame.content.increaseContrast
        let scale = scale(pen)
        guard let mark = OrbLineProvisionalMark.shared.texture(width: width, textSize: m.p, scale: scale,
                                                               increasedContrast: contrast) else { return }
        let top = OrbLineProvisionalMark.top(baseline: baseline, textSize: m.p, scale: scale, increasedContrast: contrast)
        scene.add(pen.text(mark, centerX: x + mark.advance / 2, baseline: top,
                           color: color.opacity(score.provisional * pen.alpha)))
    }

    /// One traffic row: the arrow, the number right-aligned in the `8.88` cell, the unit left-aligned in its cell.
    private func drawTraffic(_ parts: TrafficFormat.Parts, down: Bool, _ pen: CharacterBodyPainter, left: CGFloat,
                             top: CGFloat, cells: OrbLineMetrics.Cells, ink: CharacterCardInk, into scene: inout OrbScene) {
        let baseline = top + m.baseline(m.t)
        if let arrow = cache.arrowTexture(down: down, size: m.t, scale: scale(pen)) {
            scene.add(pen.text(arrow, centerX: left + arrow.advance / 2, baseline: baseline,
                               color: ink.secondary.opacity(pen.alpha)))
        }
        let numberLeft = left + cells.lead + m.cellGap
        let style = m.number(m.t)
        _ = putDigits(parts.number, style, pen, x: numberLeft + cells.number - digits(parts.number, style), baseline: baseline,
                      color: ink.number, into: &scene)
        _ = put(parts.unit, m.unit(m.t), pen, x: numberLeft + cells.number + m.cellGap, baseline: baseline,
                color: ink.secondary, into: &scene)
    }

    private func drawCost(_ money: LiveMoney, _ pen: CharacterBodyPainter, origin: CGPoint, ink: CharacterCardInk,
                          into scene: inout OrbScene) {
        let baseline = origin.y + m.baseline(m.p)
        var x = origin.x
        x += put(character?.costGlyph ?? "", m.glyph(m.p), pen, x: x, baseline: baseline, color: ink.secondary, into: &scene)
        x += m.glyphGap * 1.25
        if money.atLeast {
            x += put("≥", m.unit(m.p), pen, x: x, baseline: baseline, color: ink.secondary, into: &scene) + 0.08 * m.p
        }
        x += putDigits(money.number, m.number(m.p), pen, x: x, baseline: baseline,
                       color: money.unknown ? ink.secondary : ink.number, into: &scene)
        guard !money.unknown else { return }
        _ = put(money.unit, m.unit(m.t), pen, x: x + m.cellGap, baseline: baseline, color: ink.secondary, into: &scene)
    }

    private func drawChip(_ name: String, _ pen: CharacterBodyPainter, box: CGSize, ink: CharacterCardInk,
                          into scene: inout OrbScene) {
        let left = -box.width / 2 + 0.85 * m.t
        let dot = 0.62 * m.t
        let baseline = -m.rowHeight(m.t) / 2 + m.baseline(m.t)
        scene.add(.circle(center: pen.point(left + dot / 2, baseline - 0.36 * m.t), radius: dot / 2 * pen.scale,
                          color: ink.accent.opacity(pen.alpha)), blend: .over)
        _ = put(name, m.chip(m.t), pen, x: left + dot + 0.42 * m.t, baseline: baseline, color: ink.number, into: &scene)
    }

    // MARK: Text

    func scale(_ pen: CharacterBodyPainter) -> CGFloat { backing * max(1, pen.scale) }

    /// A whole string from `x` (local); returns its advance.
    @discardableResult
    func put(_ text: String, _ style: OrbTextStyle, _ pen: CharacterBodyPainter, x: CGFloat, baseline: CGFloat,
             color: RGBColor, into scene: inout OrbScene) -> CGFloat {
        guard !text.isEmpty, let texture = cache.texture(text, style: style, language: language, scale: scale(pen)) else { return 0 }
        scene.add(pen.text(texture, centerX: x + texture.advance / 2, baseline: baseline, color: color.opacity(pen.alpha)))
        return texture.advance
    }

    /// Digits character by character from `x` (local); returns their advance.
    @discardableResult
    func putDigits(_ text: String, _ style: OrbTextStyle, _ pen: CharacterBodyPainter, x: CGFloat, baseline: CGFloat,
                   color: RGBColor, into scene: inout OrbScene) -> CGFloat {
        var dx: CGFloat = 0
        for character in text {
            guard let texture = cache.texture(character, style: style, language: .en, scale: scale(pen)) else { continue }
            if color.alpha > 0.005 {
                scene.add(pen.text(texture, centerX: x + dx + texture.advance / 2, baseline: baseline,
                                   color: color.opacity(pen.alpha)))
            }
            dx += texture.advance
        }
        return dx
    }

    func advance(_ text: String, _ style: OrbTextStyle) -> CGFloat {
        guard !text.isEmpty else { return 0 }
        return cache.texture(text, style: style, language: language, scale: backing)?.advance ?? 0
    }

    func digits(_ text: String, _ style: OrbTextStyle) -> CGFloat {
        var width: CGFloat = 0
        for character in text { width += cache.texture(character, style: style, language: .en, scale: backing)?.advance ?? 0 }
        return width
    }
}
