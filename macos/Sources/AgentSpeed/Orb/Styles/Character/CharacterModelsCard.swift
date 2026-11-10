import AgentSpeedCharacter
import AppKit
import CoreText

/// The models card (DESIGN 2a.6, *Models*): one row per running model, fastest first — the vendor's accent dot, the
/// model id at `t` in the value token (truncated in the middle beyond `9 t`) and its whole tok/s at the trailing edge
/// in tabular digits, the unit once after the first row's number; at most four rows (two below S = 96) and a secondary
/// `+N` line. Rows glide and fade as `CharacterCardDynamics` moves them.
extension CharacterCards {
    struct ModelsLayout {
        var names: CGFloat
        var numbers: CGFloat
        /// The unit's cell (gap included); zero when the first row has no speed.
        var unit: CGFloat
        var rows: Double
    }

    var modelsDot: CGFloat { 0.62 * m.t }
    var modelsDotGap: CGFloat { 0.42 * m.t }
    var modelsNumberGap: CGFloat { 0.9 * m.t }
    var modelsRowGap: CGFloat { 0.3 * m.t }
    var modelsNameWidth: CGFloat { 9 * m.t }

    /// The model id as drawn: whole up to `9 t`, else cut in the middle.
    func modelsName(_ id: String) -> String {
        CharacterTextFit.middle(id, style: m.label(m.t), language: language, width: modelsNameWidth)
    }

    func modelsLayout() -> ModelsLayout? {
        let rows = dynamics.modelRows.rows
        guard !rows.isEmpty else { return nil }
        var names: CGFloat = 0, numbers: CGFloat = 0
        for row in rows where row.presence > 0 {
            if row.id == CharacterCardDynamics.moreId {
                names = max(names, advance(moreText, m.unit(m.t)))
            } else if let line = dynamics.modelLines[row.id] {
                names = max(names, advance(modelsName(line.id), m.label(m.t)))
                numbers = max(numbers, digits(line.speed, m.number(m.t)))
            }
        }
        let first = dynamics.modelRows.rows.first { $0.wanted }.flatMap { dynamics.modelLines[$0.id] }
        let unit = first?.hasSpeed == true ? m.cellGap + advance(OrbLineMetrics.thinkingUnit, m.unit(m.t)) : 0
        return ModelsLayout(names: names, numbers: numbers, unit: unit, rows: dynamics.modelRows.extent)
    }

    var moreText: String { CharacterReadoutText.more(dynamics.moreCount, language: language) }

    func modelsSize() -> CGSize {
        guard let layout = modelsLayout(), layout.rows > 0 else { return .zero }
        let width = modelsDot + modelsDotGap + layout.names + modelsNumberGap + layout.numbers + layout.unit
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
            let accent = line.accent ?? ink.secondary
            scene.add(.circle(center: pen.point(origin.x + modelsDot / 2, baseline - 0.36 * m.t), radius: modelsDot / 2 * pen.scale,
                              color: accent.opacity(alpha * pen.alpha)), blend: .over)
            put(modelsName(line.id), m.label(m.t), pen, x: nameLeft, baseline: baseline, color: ink.number.opacity(alpha),
                into: &scene)
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

/// Fits text into a width by cutting it in the middle (`claude-op…5-20251101`), measured with Core Text as the glyph
/// atlas lays it out; results are kept, since the same ids are fitted every frame.
@MainActor
enum CharacterTextFit {
    private struct Key: Hashable {
        let text: String
        let style: OrbTextStyle
        let language: AppLanguage
        let width: CGFloat
    }

    private static var fitted: [Key: String] = [:]

    static func middle(_ text: String, style: OrbTextStyle, language: AppLanguage, width: CGFloat) -> String {
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
        guard measure(text, style: style, language: language) > width else { return text }
        let characters = Array(text)
        func cut(_ kept: Int) -> String {
            let head = (kept + 1) / 2
            return String(characters.prefix(head)) + "…" + String(characters.suffix(kept - head))
        }
        var low = 0, high = characters.count - 1
        while low < high {
            let mid = (low + high + 1) / 2
            if measure(cut(mid), style: style, language: language) <= width { low = mid } else { high = mid - 1 }
        }
        return cut(low)
    }
}
