import AgentSpeedCharacter
import AgentSpeedCore
import AppKit

/// The 額度 card (DESIGN 2a.6): per source a header line (the source's color dot and its name), then one line per
/// window — the label in a column as wide as the card's widest, the bar, the percent right-aligned in a `100%` cell
/// and the short reset in a cell as wide as the widest. When the room to the orb's edge is short the reset cells go
/// first, then the bars shorten from 5 t to 3 t, then the headers go and the dot moves before each source's first label.
/// A remote host's source reads `<source> · <host>` (DESIGN 13): the host follows the name while the card's width
/// holds it, else it takes its own line under the name (in the compact form, above the source's lines), shortened
/// with an ellipsis when even that line is too short; the host never widens the card.
extension CharacterCards {
    /// The card's forms, widest first.
    enum QuotaForm: Int, CaseIterable {
        case full, noReset, shortBars, compact

        var hasReset: Bool { self == .full }
        var hasHeader: Bool { self != .compact }
        var barLength: CGFloat { self == .shortBars || self == .compact ? 3 : 5 }
    }

    /// Widths of the card's columns over every source and line it draws.
    struct QuotaColumns {
        var label: CGFloat
        var percent: CGFloat
        var reset: CGFloat
        var header: CGFloat
    }

    var quotaHeaderSize: CGFloat { 0.9 * m.t }
    var quotaDot: CGFloat { 0.55 * m.t }
    var quotaDotGap: CGFloat { 0.35 * m.t }
    var quotaLineGap: CGFloat { 0.25 * m.t }
    var quotaSourceGap: CGFloat { 0.45 * m.t }
    var quotaBarHeight: CGFloat { 0.26 * m.t }
    var quotaLabelStyle: OrbTextStyle { m.label(quotaHeaderSize) }
    /// The source's name leads its lines: the same size and token, a step heavier.
    var quotaNameStyle: OrbTextStyle { m.chip(quotaHeaderSize) }
    var quotaPercentStyle: OrbTextStyle { m.number(m.t) }

    func quotaColumns() -> QuotaColumns? {
        let sources = dynamics.quota.sources
        guard !sources.isEmpty else { return nil }
        var columns = QuotaColumns(label: 0, percent: digits("100%", quotaPercentStyle), reset: 0, header: 0)
        for source in sources {
            columns.header = max(columns.header, quotaDot + quotaDotGap + advance(source.name, quotaNameStyle))
            for line in source.lines {
                columns.label = max(columns.label, advance(line.label, quotaLabelStyle))
                for reset in [line.reset, line.oldReset] {
                    if let reset { columns.reset = max(columns.reset, advance(reset, quotaLabelStyle)) }
                }
            }
        }
        return columns
    }

    /// The content width of a form.
    func quotaWidth(_ form: QuotaForm, _ c: QuotaColumns) -> CGFloat {
        let lead = form.hasHeader ? 0 : quotaDot + quotaDotGap
        var line = lead + c.label + 0.5 * m.t + form.barLength * m.t + 0.5 * m.t + c.percent
        if form.hasReset, c.reset > 0 { line += 0.4 * m.t + c.reset }
        return form.hasHeader ? max(line, c.header) : line
    }

    /// The plate width of every form, widest first; a form the card does not use (reset cells below S = 96, or while
    /// no window has a reset time) is infinite, so it never fits.
    func quotaWidths() -> [Double] {
        guard let c = quotaColumns() else { return [] }
        return QuotaForm.allCases.map { form in
            if form == .full, frame.isSmallCharacter || c.reset == 0 { return .infinity }
            return Double(quotaWidth(form, c) + 2 * m.padX)
        }
    }

    var quotaForm: QuotaForm {
        let form = QuotaForm(rawValue: dynamics.quotaFit.form) ?? .full
        return frame.isSmallCharacter && form == .full ? .noReset : form
    }

    private func quotaHeight(_ source: CharacterQuotaDynamics.Source, _ form: QuotaForm, _ c: QuotaColumns) -> CGFloat {
        let lines = source.lines.reduce(CGFloat(0)) { $0 + CGFloat($1.presence) * (m.rowHeight(m.t) + quotaLineGap) }
        let rows = (form.hasHeader ? 1 : 0) + (quotaHostOwnLine(source, form, c) ? 1 : 0)
        let header = CGFloat(rows) * (m.rowHeight(quotaHeaderSize) + quotaLineGap)
        return max(0, header + lines - quotaLineGap)
    }

    /// `·` and the host after the name, in the label style.
    private func quotaHostTail(_ host: String) -> String { " · " + host }

    /// The host does not fit after the name: it takes a line of its own.
    private func quotaHostOwnLine(_ source: CharacterQuotaDynamics.Source, _ form: QuotaForm, _ c: QuotaColumns) -> Bool {
        guard let host = source.host else { return false }
        guard form.hasHeader else { return true }
        let header = quotaDot + quotaDotGap + advance(source.name, quotaNameStyle) + advance(quotaHostTail(host), quotaLabelStyle)
        return header > quotaWidth(form, c) + 0.5
    }

    /// `text` shortened with a trailing ellipsis to `room`.
    private func quotaFitted(_ text: String, _ style: OrbTextStyle, room: CGFloat) -> String {
        guard advance(text, style) > room else { return text }
        let characters = Array(text)
        var low = 0, high = characters.count
        while low < high {
            let mid = (low + high + 1) / 2
            if advance(String(characters[..<mid]) + "…", style) <= room { low = mid } else { high = mid - 1 }
        }
        return low == 0 ? "…" : String(characters[..<low]) + "…"
    }

    func quotaSize() -> CGSize {
        guard let c = quotaColumns() else { return .zero }
        let form = quotaForm
        var height: CGFloat = 0
        for source in dynamics.quota.sources {
            height += CGFloat(source.presence) * (quotaHeight(source, form, c) + quotaSourceGap)
        }
        height -= quotaSourceGap
        guard height > 0.5 else { return .zero }
        return plate(CGSize(width: quotaWidth(form, c), height: height))
    }

    func drawQuota(_ pen: CharacterBodyPainter, origin: CGPoint, box: CGSize, ink: CharacterCardInk,
                   into scene: inout OrbScene) {
        guard let c = quotaColumns() else { return }
        let form = quotaForm
        let dark = frame.dark
        let contrast = frame.content.increaseContrast
        let base = dark ? RGBColor.white : RGBColor(hex: 0x0B0D12)
        let track = base.opacity(contrast ? 0.3 : dark ? 0.16 : 0.12)
        let red = CharacterQuotaInk.red(dark: dark)
        let bottom = box.height / 2 - m.padY
        let lead = form.hasHeader ? 0 : quotaDot + quotaDotGap
        let labelX = origin.x + lead
        let barX = labelX + c.label + 0.5 * m.t
        let barLength = form.barLength * m.t
        let percentRight = barX + barLength + 0.5 * m.t + c.percent
        let resetX = percentRight + 0.4 * m.t
        var top = origin.y
        for source in dynamics.quota.sources {
            let sourceTop = top
            let sourceAlpha = Easing.smoothstep(0, 1, source.presence)
            let tint = dark ? source.tint : source.tint.darkened(0.14)
            // A row the plate has not grown to yet fades instead of leaving it.
            func alpha(_ rowTop: CGFloat, _ height: CGFloat) -> Double {
                let overflow = Double((rowTop + height - bottom) / (0.5 * height))
                return sourceAlpha * (1 - overflow).clamped(to: 0...1)
            }
            let ownLine = quotaHostOwnLine(source, form, c)
            let nameX = origin.x + quotaDot + quotaDotGap
            if form.hasHeader {
                let h = m.rowHeight(quotaHeaderSize)
                let a = alpha(top, h)
                if a > 0.01 {
                    let baseline = top + m.baseline(quotaHeaderSize)
                    let middle = quotaMiddle(source.name, quotaNameStyle, pen, baseline: baseline)
                    scene.add(.circle(center: pen.point(origin.x + quotaDot / 2, middle),
                                      radius: quotaDot / 2 * pen.scale, color: tint.opacity(a * pen.alpha)), blend: .over)
                    let name = put(source.name, quotaNameStyle, pen, x: nameX, baseline: baseline,
                                   color: ink.secondary.opacity(a), into: &scene)
                    if let host = source.host, !ownLine {
                        put(quotaHostTail(host), quotaLabelStyle, pen, x: nameX + name, baseline: baseline,
                            color: ink.secondary.opacity(a * 0.72), into: &scene)
                    }
                }
                top += h + quotaLineGap
            }
            if ownLine, let host = source.host {
                let h = m.rowHeight(quotaHeaderSize)
                let a = alpha(top, h)
                let x = form.hasHeader ? nameX : origin.x
                let room = quotaWidth(form, c) - (x - origin.x)
                if a > 0.01 {
                    put(quotaFitted(host, quotaLabelStyle, room: room), quotaLabelStyle, pen, x: x,
                        baseline: top + m.baseline(quotaHeaderSize), color: ink.secondary.opacity(a * 0.72), into: &scene)
                }
                top += h + quotaLineGap
            }
            var first = true
            for line in source.lines {
                let h = m.rowHeight(m.t)
                let a = alpha(top, h) * Easing.smoothstep(0, 1, line.presence)
                defer {
                    top += CGFloat(line.presence) * (h + quotaLineGap)
                    first = false
                }
                guard a > 0.01 else { continue }
                let baseline = top + m.baseline(m.t)
                let middle = quotaMiddle("0", quotaPercentStyle, pen, baseline: baseline)
                if !form.hasHeader, first {
                    scene.add(.circle(center: pen.point(origin.x + quotaDot / 2, middle), radius: quotaDot / 2 * pen.scale,
                                      color: tint.opacity(a * pen.alpha)), blend: .over)
                }
                put(line.label, quotaLabelStyle, pen, x: labelX, baseline: baseline, color: ink.secondary.opacity(a),
                    into: &scene)
                let level = QuotaLevel(percent: line.percent)
                let fill = contrast ? ink.number.opacity(1) : level == .normal ? tint : level == .critical ? red
                    : QuotaLevel.amber(dark: dark)
                drawQuotaBar(pen, x: barX, y: middle, length: barLength, share: line.bar, track: track, fill: fill, alpha: a,
                             into: &scene)
                drawQuotaPercent(line, pen, right: percentRight, baseline: baseline, ink: ink, red: red, alpha: a,
                                 into: &scene)
                guard form.hasReset else { continue }
                if let reset = line.reset, line.resetPresence > 0.01 {
                    put(reset, quotaLabelStyle, pen, x: resetX, baseline: baseline,
                        color: ink.secondary.opacity(a * line.resetPresence * line.resetMix), into: &scene)
                }
                if let old = line.oldReset, line.resetMix < 1 {
                    put(old, quotaLabelStyle, pen, x: resetX, baseline: baseline,
                        color: ink.secondary.opacity(a * line.resetPresence * (1 - line.resetMix)), into: &scene)
                }
            }
            top = sourceTop + CGFloat(source.presence) * (quotaHeight(source, form, c) + quotaSourceGap)
        }
    }

    /// The visual middle of `text` drawn at `baseline` (half its cap height above the glyphs' baseline). A texture's
    /// height is rounded up, which sets its glyphs a little below the baseline asked for; the bars and dots follow the
    /// glyphs as drawn.
    func quotaMiddle(_ text: String, _ style: OrbTextStyle, _ pen: CharacterBodyPainter, baseline: CGFloat) -> CGFloat {
        let font = CharacterQuotaInk.metrics(style)
        guard let texture = cache.texture(text, style: style, language: .en, scale: scale(pen)) else {
            return baseline - 0.36 * style.size
        }
        let lowered = max(0, texture.size.height - 2 * texture.padding - font.lineHeight)
        return baseline + lowered - font.capHeight / 2
    }

    /// The track, then the fill from its left end; both fully rounded, the fill a shorter pill when it is short.
    private func drawQuotaBar(_ pen: CharacterBodyPainter, x: CGFloat, y: CGFloat, length: CGFloat, share: Double,
                              track: RGBColor, fill: RGBColor, alpha: Double, into scene: inout OrbScene) {
        let s = pen.scale
        let h = quotaBarHeight
        scene.add(.roundedRect(center: pen.point(x + length / 2, y), halfSize: CGSize(width: length / 2 * s, height: h / 2 * s),
                               rotation: pen.spin, corner: h / 2 * s, color: track.opacity(alpha * pen.alpha)), blend: .over)
        let filled = length * CGFloat(share.clamped(to: 0...1))
        guard filled > 0.2 else { return }
        let half = CGSize(width: filled / 2 * s, height: min(h, filled) / 2 * s)
        scene.add(.roundedRect(center: pen.point(x + filled / 2, y), halfSize: half, rotation: pen.spin,
                               corner: min(half.width, half.height), color: fill.opacity(alpha * pen.alpha)), blend: .over)
    }

    /// The percent in tabular digits, right-aligned; a changed value crossfades. From 95 % it takes the bar's red.
    private func drawQuotaPercent(_ line: CharacterQuotaDynamics.Line, _ pen: CharacterBodyPainter, right: CGFloat,
                                  baseline: CGFloat, ink: CharacterCardInk, red: RGBColor, alpha: Double,
                                  into scene: inout OrbScene) {
        let style = quotaPercentStyle
        func color(_ percent: Int) -> RGBColor { QuotaLevel(percent: percent) == .critical ? red.opacity(0.95) : ink.number }
        if let old = line.oldPercent, line.percentMix < 1 {
            let text = "\(old)%"
            putDigits(text, style, pen, x: right - digits(text, style), baseline: baseline,
                      color: color(old).opacity(alpha * (1 - line.percentMix)), into: &scene)
        }
        let text = "\(line.percent)%"
        putDigits(text, style, pen, x: right - digits(text, style), baseline: baseline,
                  color: color(line.percent).opacity(alpha * line.percentMix), into: &scene)
    }
}

/// The system red as it resolves on a dark or light card, kept until the system colors change, and the font metrics
/// the bars line up with.
@MainActor
enum CharacterQuotaInk {
    private static var reds: [Bool: RGBColor] = [:]
    private static var observer: NSObjectProtocol?
    private static var fonts: [OrbTextStyle: (lineHeight: CGFloat, capHeight: CGFloat)] = [:]

    /// Ascent plus descent (a line's typographic height) and the cap height of a style's font.
    static func metrics(_ style: OrbTextStyle) -> (lineHeight: CGFloat, capHeight: CGFloat) {
        if let known = fonts[style] { return known }
        let font = style.font()
        let metrics = (lineHeight: font.ascender - font.descender, capHeight: font.capHeight)
        fonts[style] = metrics
        return metrics
    }

    static func red(dark: Bool) -> RGBColor {
        if observer == nil {
            observer = NotificationCenter.default.addObserver(forName: NSColor.systemColorsDidChangeNotification,
                                                              object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { reds = [:] }
            }
        }
        if let red = reds[dark] { return red }
        let red = QuotaLevel.red(dark: dark)
        reds[dark] = red
        return red
    }
}
