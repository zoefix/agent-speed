import AgentSpeedCharacter
import AppKit
import CoreText

/// The models card (DESIGN 2a.6, *Models*): one row per running model, fastest first — the vendor's accent dot, the
/// model's display name at `t` in the value token (cut at its end to the name width) and its whole tok/s at the
/// trailing edge in tabular digits, the unit once after the first row's number; at most four rows (two below S = 96)
/// and a secondary `+N` line. The name width follows the room (`HudNameWidth`, kept in `CharacterCardDynamics`);
/// without names the rows keep the dot and the speed. Rows glide and fade as the dynamics move them.
extension CharacterCards {
    /// The card's columns: names (zero without them), numbers, the unit's cell, the `+N` line, and its height in rows.
    struct ModelsLayout {
        var names: CGFloat
        var numbers: CGFloat
        var unit: CGFloat
        var more: CGFloat
        var rows: Double
    }

    /// What the name width is fitted from: the widest name in full, the card's width without its names, and its
    /// narrowest width (no names).
    struct ModelsMeasure {
        var natural: CGFloat
        var rest: CGFloat
        var narrowest: CGFloat
    }

    var modelsDot: CGFloat { 0.62 * m.t }
    var modelsDotGap: CGFloat { 0.42 * m.t }
    var modelsNameGap: CGFloat { 0.9 * m.t }
    var modelsRowGap: CGFloat { 0.3 * m.t }
    var moreText: String { CharacterReadoutText.more(dynamics.moreCount, language: language) }

    /// A model's display name as drawn in `width`.
    func modelsName(_ id: String, width: CGFloat) -> String {
        CharacterTextFit.tail(CharacterReadoutText.displayName(id), style: m.label(m.t), language: language, width: width)
    }

    private func columns() -> (natural: CGFloat, numbers: CGFloat, unit: CGFloat, more: CGFloat)? {
        let rows = dynamics.modelRows.rows
        guard !rows.isEmpty else { return nil }
        var natural: CGFloat = 0, numbers: CGFloat = 0, more: CGFloat = 0
        for row in rows where row.presence > 0 {
            if row.id == CharacterCardDynamics.moreId {
                more = advance(moreText, m.unit(m.t))
            } else if let line = dynamics.modelLines[row.id] {
                natural = max(natural, CharacterTextFit.width(CharacterReadoutText.displayName(line.id), style: m.label(m.t),
                                                              language: language))
                numbers = max(numbers, digits(line.speed, m.number(m.t)))
            }
        }
        let first = rows.first { $0.wanted }.flatMap { dynamics.modelLines[$0.id] }
        let unit = first?.hasSpeed == true ? m.cellGap + advance(OrbLineMetrics.thinkingUnit, m.unit(m.t)) : 0
        return (natural, numbers, unit, more)
    }

    func modelsMeasure() -> ModelsMeasure? {
        guard let c = columns() else { return nil }
        let lead = 2 * m.padX + modelsDot + modelsDotGap
        let numbers = c.numbers + c.unit
        return ModelsMeasure(natural: c.natural, rest: lead + modelsNameGap + numbers,
                             narrowest: lead + max(numbers, c.more))
    }

    func modelsLayout() -> ModelsLayout? {
        guard let c = columns() else { return nil }
        let names = dynamics.nameWidth.map { min($0, c.natural) } ?? 0
        return ModelsLayout(names: names, numbers: c.numbers, unit: c.unit, more: c.more, rows: dynamics.modelRows.extent)
    }

    func modelsSize() -> CGSize {
        guard let layout = modelsLayout(), layout.rows > 0 else { return .zero }
        let rows = layout.names > 0 ? layout.names + modelsNameGap + layout.numbers + layout.unit
            : layout.numbers + layout.unit
        let width = modelsDot + modelsDotGap + max(rows, layout.more)
        let height = CGFloat(layout.rows) * (m.rowHeight(m.t) + modelsRowGap) - modelsRowGap
        return plate(CGSize(width: width, height: max(0, height)))
    }

    func drawModels(_ pen: CharacterBodyPainter, origin: CGPoint, box: CGSize, ink: CharacterCardInk,
                    into scene: inout OrbScene) {
        guard let layout = modelsLayout() else { return }
        let pitch = m.rowHeight(m.t) + modelsRowGap
        let bottom = box.height / 2 - m.padY
        let numberRight = box.width / 2 - m.padX - layout.unit
        let nameLeft = origin.x + modelsDot + modelsDotGap
        // Increase Contrast marks every model in the system's highlight color.
        let highlight = frame.content.increaseContrast ? CharacterHighlight.color(dark: frame.dark) : nil
        var unitDrawn = false
        for row in dynamics.modelRows.rows {
            let top = origin.y + CGFloat(row.slot) * pitch
            // A row the plate has not grown to yet fades instead of leaving it.
            let overflow = (top + m.rowHeight(m.t) - bottom) / (0.5 * m.rowHeight(m.t))
            let alpha = Easing.smoothstep(0, 1, row.presence) * (1 - overflow).clamped(to: 0...1)
            guard alpha > 0.01 else { continue }
            let baseline = top + m.baseline(m.t)
            if row.id == CharacterCardDynamics.moreId {
                put(moreText, m.unit(m.t), pen, x: nameLeft, baseline: baseline, color: ink.secondary.opacity(alpha),
                    into: &scene)
                continue
            }
            guard let line = dynamics.modelLines[row.id] else { continue }
            let accent = highlight ?? line.accent ?? ink.secondary
            scene.add(.circle(center: pen.point(origin.x + modelsDot / 2, baseline - 0.36 * m.t), radius: modelsDot / 2 * pen.scale,
                              color: accent.opacity(alpha * pen.alpha)), blend: .over)
            if layout.names > 0 {
                put(modelsName(line.id, width: layout.names), m.label(m.t), pen, x: nameLeft, baseline: baseline,
                    color: ink.number.opacity(alpha), into: &scene)
            }
            let style = m.number(m.t)
            putDigits(line.speed, style, pen, x: numberRight - digits(line.speed, style), baseline: baseline,
                      color: (line.hasSpeed ? ink.number : ink.secondary).opacity(alpha), into: &scene)
            if !unitDrawn, row.wanted, line.hasSpeed, layout.unit > 0 {
                put(OrbLineMetrics.thinkingUnit, m.unit(m.t), pen, x: numberRight + m.cellGap, baseline: baseline,
                    color: ink.secondary.opacity(alpha), into: &scene)
            }
            if row.wanted { unitDrawn = true }
        }
    }
}

/// The system highlight (accent) color as it resolves on a dark or light card, kept until the system colors change.
@MainActor
enum CharacterHighlight {
    private static var resolved: [Bool: RGBColor] = [:]
    private static var observer: NSObjectProtocol?

    static func color(dark: Bool) -> RGBColor {
        if observer == nil {
            observer = NotificationCenter.default.addObserver(forName: NSColor.systemColorsDidChangeNotification,
                                                              object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { resolved = [:] }
            }
        }
        if let color = resolved[dark] { return color }
        var color = RGBColor(hex: dark ? 0x4D9CFF : 0x0A64D6)
        NSAppearance(named: dark ? .darkAqua : .aqua)?.performAsCurrentDrawingAppearance {
            if let accent = NSColor.controlAccentColor.usingColorSpace(.sRGB) {
                color = RGBColor(red: accent.redComponent, green: accent.greenComponent, blue: accent.blueComponent)
            }
        }
        resolved[dark] = color
        return color
    }
}

/// Fits text into a width by cutting its end (`claude-sonnet-4…`), measured with Core Text as the glyph atlas lays it
/// out; results are kept, since the same names are fitted every frame.
@MainActor
enum CharacterTextFit {
    private struct Key: Hashable {
        let text: String
        let style: OrbTextStyle
        let language: AppLanguage
        let width: CGFloat
    }

    private static var fitted: [Key: String] = [:]
    private static var widths: [Key: CGFloat] = [:]

    /// The text's width in full, kept.
    static func width(_ text: String, style: OrbTextStyle, language: AppLanguage) -> CGFloat {
        let key = Key(text: text, style: style, language: language, width: 0)
        if let cached = widths[key] { return cached }
        let width = measure(text, style: style, language: language)
        if widths.count >= 256 { widths.removeAll(keepingCapacity: true) }
        widths[key] = width
        return width
    }

    static func tail(_ text: String, style: OrbTextStyle, language: AppLanguage, width: CGFloat) -> String {
        let key = Key(text: text, style: style, language: language, width: width)
        if let cached = fitted[key] { return cached }
        let result = fit(text, style: style, language: language, width: width)
        if fitted.count >= 256 { fitted.removeAll(keepingCapacity: true) }
        fitted[key] = result
        return result
    }

    static func measure(_ text: String, style: OrbTextStyle, language: AppLanguage) -> CGFloat {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: style.font(),
            .kern: style.tracking,
            NSAttributedString.Key(kCTLanguageAttributeName as String): language.rawValue,
        ]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
        return CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil))
    }

    private static func fit(_ text: String, style: OrbTextStyle, language: AppLanguage, width: CGFloat) -> String {
        guard measure(text, style: style, language: language) > width + 0.25 else { return text }
        let characters = Array(text)
        func cut(_ kept: Int) -> String { String(characters.prefix(kept)) + "…" }
        var low = 0, high = characters.count - 1
        while low < high {
            let mid = (low + high + 1) / 2
            if measure(cut(mid), style: style, language: language) <= width { low = mid } else { high = mid - 1 }
        }
        return cut(low)
    }
}
