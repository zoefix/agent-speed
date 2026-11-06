import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// Colors of the readout; styles that put text on their own lit surfaces override them (DESIGN 2a.3, readout split).
struct OrbReadoutPalette {
    var value: RGBColor?
    var unit: RGBColor?
    var title: RGBColor?
    /// Replaces the heat glow of the value (a dark shadow under the liquid surface); its alpha is the strength.
    var shadow: RGBColor?
    var shadowRadius: CGFloat = 0
    var valueGlow = true

    static let standard = OrbReadoutPalette()
}

/// The readout drawn on the GPU (DESIGN 2 layers 6 and 8, 2a Shared foundation): the value with the odometer, heat
/// glow, forward lean and redline jitter, the unit, the speed title (or the hover info line), the agents overflow
/// and the new-peak pill, placed by the style's frame table.
///
/// One builder per orb: it owns the odometer state and reuses its glyph storage, so drawing never allocates.
@MainActor
final class OrbReadoutBuilder {
    /// Duration of the title slide (DESIGN section 2, layer 6).
    static let titleSlide: TimeInterval = 0.35
    static let titleFade: TimeInterval = 0.25
    static let peakDuration: TimeInterval = 1.8
    /// Unit and title never go below this size.
    static let textFloor: CGFloat = 7

    let textCache: OrbTextCache
    private(set) var odometer = OrbOdometer()
    private var current: ContiguousArray<OrbTextTexture> = []
    private var previous: ContiguousArray<OrbTextTexture> = []
    private var currentX: ContiguousArray<CGFloat> = []
    /// The character cells of the value drawn last (canvas points: advance × ascent + descent at the baseline), for
    /// effects that hang off the digits (`led` lightning); `valueCellsText` is the text they belong to.
    private(set) var valueCells: ContiguousArray<CGRect> = []
    private(set) var valueCellsText = ""

    init(textCache: OrbTextCache = .shared) {
        self.textCache = textCache
        current.reserveCapacity(16)
        previous.reserveCapacity(16)
        currentX.reserveCapacity(16)
        valueCells.reserveCapacity(16)
    }

    /// Feeds the shown money value (called every frame; only changes start a roll).
    func update(text: String, value: Double, at time: TimeInterval) {
        if switchUnit(to: .money, at: time) {
            odometer.reset(text, value: value)
        } else {
            odometer.update(text, value: value, at: time)
        }
        formattedValue = -1
    }

    /// Feeds the shown value every frame; it is formatted only when it changes (formatting allocates).
    func update(value: Double, at time: TimeInterval) {
        if switchUnit(to: .tokens, at: time) {
            odometer.reset(SpeedFormat.speed(value), value: value)
            formattedValue = value
            return
        }
        guard value != formattedValue else { return }
        formattedValue = value
        odometer.update(SpeedFormat.speed(value), value: value, at: time)
    }

    private var formattedValue = -1.0

    func reset(text: String, value: Double) {
        odometer.reset(text, value: value)
        formattedValue = value
        mode = nil
        unitSwitch = nil
    }

    // MARK: Unit switch (DESIGN 9: the value and the unit slide up/out and up/in, 0.3 s; Reduce Motion: 0.15 s fade)

    private enum Mode { case tokens, money }
    private var mode: Mode?
    /// The readout before a switch, drawn leaving while the new one arrives.
    private var unitSwitch: (start: TimeInterval, value: String, unit: String, atLeast: Bool)?
    private var shownUnit = ""
    private var shownAtLeast = false
    static let unitSwitchDuration: TimeInterval = 0.3
    static let unitSwitchFade: TimeInterval = 0.15

    /// Returns true when the readout changes unit now (the odometer then starts from the new text without rolling).
    private func switchUnit(to next: Mode, at time: TimeInterval) -> Bool {
        defer { mode = next }
        guard let mode, mode != next else { return false }
        unitSwitch = (time, odometer.current, shownUnit, shownAtLeast)
        return true
    }

    private func unitSwitchProgress(at time: TimeInterval, reduceMotion: Bool) -> Double? {
        guard let unitSwitch else { return nil }
        let duration = reduceMotion ? Self.unitSwitchFade : Self.unitSwitchDuration
        let p = (time - unitSwitch.start) / duration
        guard p >= 0, p < 1 else { return nil }
        return p
    }

    var isRolling: Bool { odometer.changedAt.isFinite }

    func isSettled(at time: TimeInterval) -> Bool { time - odometer.changedAt >= OrbOdometer.duration + 0.05 }

    // MARK: Layout

    private struct Lines {
        var value: CGFloat
        var unit: CGFloat
        var title: CGFloat
        var pill: CGFloat
    }

    private func lines(_ frame: OrbFrame, _ layout: OrbReadoutLayout) -> Lines {
        let s = frame.s
        let y = frame.center.y + frame.readoutShift
        let value = y + layout.valueBaseline * s
        let unit = layout.unitSize == nil ? value : value + layout.unitGap * s
        let title: CGFloat = switch layout.title {
        case .belowUnit(let gap): unit + gap * s
        case .belowValue(let gap): value + gap * s
        case .fixed(let offset): y + offset * s
        }
        return Lines(value: value, unit: unit, title: title, pill: y + layout.pillCenter * s)
    }

    func add(into scene: inout OrbScene, _ frame: OrbFrame, layout: OrbReadoutLayout, palette: OrbReadoutPalette = .standard,
             clip: OrbTextClip = .none, pill: Bool = true) {
        let layout = layout.at(diameter: frame.s)
        let lines = lines(frame, layout)
        let reduce = frame.effects.reduceMotion || frame.effects.level == .off
        if let p = unitSwitchProgress(at: frame.time, reduceMotion: reduce), let old = unitSwitch, !frame.unavailable {
            let travel = reduce ? 0 : layout.valueSize * frame.s * 0.9
            let outgoing = reduce ? (alpha: 1 - p, travel: 0.0) : Self.titleOut(p)
            let incoming = reduce ? (alpha: p, travel: 1.0) : Self.titleIn(p)
            let up = -travel * outgoing.travel
            let down = travel * (1 - incoming.travel)
            // The moving readout stays inside its own band (value and unit lines), like the title inside its line.
            let s = frame.s
            let top = lines.value - layout.valueSize * s * 1.02
            let bottom = (layout.unitSize.map { lines.unit + $0 * s * 0.45 } ?? lines.value + layout.valueSize * s * 0.25)
            let band = Self.clip(clip, to: CGRect(x: frame.center.x - s / 2, y: top, width: s, height: bottom - top))
            addValue(&scene, frame, layout: layout, line: lines.value, palette: palette, clip: band,
                     shown: (old.value, old.atLeast), offset: up, alpha: outgoing.alpha)
            addValue(&scene, frame, layout: layout, line: lines.value, palette: palette, clip: band, offset: down, alpha: incoming.alpha)
            if !frame.content.valueOnly, let unitSize = layout.unitSize {
                addUnit(&scene, frame, size: unitSize, line: lines.unit, palette: palette, clip: band, text: old.unit,
                        offset: up, alpha: outgoing.alpha)
                addUnit(&scene, frame, size: unitSize, line: lines.unit, palette: palette, clip: band, offset: down,
                        alpha: incoming.alpha)
            }
        } else {
            addValue(&scene, frame, layout: layout, line: lines.value, palette: palette, clip: clip)
            if !frame.content.valueOnly, let unitSize = layout.unitSize {
                addUnit(&scene, frame, size: unitSize, line: lines.unit, palette: palette, clip: clip)
            }
        }
        guard !frame.content.valueOnly else { return }
        addLabels(&scene, frame, layout: layout, line: lines.title, palette: palette, clip: clip)
        if layout.dots != nil { addOverflow(&scene, frame, layout: layout, clip: clip) }
        if pill { addPeakPill(&scene, frame, line: lines.pill) }
    }

    /// `clip` narrowed to `rect`.
    private static func clip(_ clip: OrbTextClip, to rect: CGRect) -> OrbTextClip {
        switch clip {
        case .none: .rect(rect)
        case .rect(let other): .rect(other.intersection(rect))
        case .liquid(let below, let other): .liquid(below: below, rect: other.intersection(rect))
        // A sheen band cannot be narrowed to a rect; it keeps its own shape.
        case .band: clip
        }
    }

    private func secondary(_ frame: OrbFrame) -> RGBColor {
        frame.content.increaseContrast ? frame.tokens.secondaryText.opacity(1.35) : frame.tokens.secondaryText
    }

    // MARK: Value

    /// `shown` replaces the odometer's text (the readout leaving during a unit switch); `offset` and `alpha` move and
    /// fade the whole value.
    private func addValue(_ scene: inout OrbScene, _ frame: OrbFrame, layout: OrbReadoutLayout, line: CGFloat,
                          palette: OrbReadoutPalette, clip: OrbTextClip, shown: (text: String, atLeast: Bool)? = nil,
                          offset slide: CGFloat = 0, alpha fade: Double = 1) {
        let s = frame.s
        let unavailable = frame.unavailable
        let text = unavailable ? "–" : shown?.text ?? odometer.current
        let language = frame.content.language
        let fullSize = layout.valueSize * s
        var style = OrbTextStyle(size: fullSize, weight: .bold, monospacedDigits: true, padding: 0.07 * s)
        // Money (DESIGN 9): a small `≥` (60 %, secondary color) is part of a partly priced value's width; a value
        // without any price reads `–` in the idle value color.
        let atLeast = !unavailable && (shown?.atLeast ?? (frame.content.money?.atLeast == true))
        let unpriced = !unavailable && shown == nil && frame.content.money?.unknown == true
        if shown == nil { shownAtLeast = atLeast }
        func prefixStyle(_ style: OrbTextStyle) -> OrbTextStyle {
            var prefix = style
            prefix.size = style.size * 0.6
            prefix.padding = style.padding * 0.6
            return prefix
        }
        func advance(_ string: String, _ style: OrbTextStyle) -> CGFloat {
            var total: CGFloat = 0
            for character in string {
                total += textCache.texture(character, style: style, language: language, scale: scene.backingScale)?.advance ?? 0
            }
            return total
        }
        let natural = advance(text, style) + (atLeast ? advance("≥", prefixStyle(style)) : 0)
        let limit = layout.valueMaxWidth * s
        if natural > limit, natural > 0 {
            let fit = max(0.4, (limit / natural * 50).rounded(.down) / 50)
            style.size = fullSize * fit
            style.padding = 0.07 * s * fit
        }
        current.removeAll(keepingCapacity: true)
        currentX.removeAll(keepingCapacity: true)
        for character in text {
            guard let texture = textCache.texture(character, style: style, language: language, scale: scene.backingScale) else { continue }
            current.append(texture)
        }
        guard !current.isEmpty else { return }
        let prefix = atLeast ? textCache.texture("≥", style: prefixStyle(style), language: language, scale: scene.backingScale) : nil
        var total: CGFloat = prefix?.advance ?? 0
        for texture in current { total += texture.advance }
        let prefixX = frame.center.x - total / 2
        var x = prefixX + (prefix?.advance ?? 0)
        for texture in current {
            currentX.append(x)
            x += texture.advance
        }

        let tokens = frame.tokens
        let idleColor = tokens.idleValueText
        let color = palette.value ?? (unavailable || unpriced ? idleColor : idleColor.mix(tokens.valueText, frame.activity))
        var glow: RGBColor?
        var glowRadius: CGFloat = 0
        if let shadow = palette.shadow {
            glow = shadow
            glowRadius = palette.shadowRadius
        } else if layout.valueGlow, palette.valueGlow, !unavailable, !unpriced {
            let strength = valueGlowStrength * (frame.dark ? 0.85 : 0.45)
            if strength > 0.01 {
                glow = tokens.heat(max(frame.heat, 0.2)).opacity(strength)
                glowRadius = 0.035 * s * (layout.silkValue ? LedMath.glowScale(overdrive: frame.overdrive) : 1)
            }
        }
        let leanDegrees = layout.silkValue ? LedMath.leanDegrees(heat: frame.heat) : 6 * frame.heat
        let lean = frame.effects.skew && layout.valueMotion && !unavailable ? tan(leanDegrees * leanAmount * .pi / 180) : 0
        var jitter = CGSize.zero
        if frame.effects.jitter, layout.valueMotion, frame.tier == .redline, frame.redlineAmount > 0.01, !unavailable {
            let tick = Int((frame.time * 30).rounded(.down))
            let base = layout.silkValue ? LedMath.jitter(overdrive: frame.overdrive) : 0.6
            let amplitude = base * frame.redlineAmount * frame.unit
            jitter = CGSize(width: (Noise.hash(tick, 3) - 0.5) * 2 * amplitude, height: (Noise.hash(tick, 7) - 0.5) * 2 * amplitude)
        }
        let baseline = line + jitter.height + slide
        let extras = layout.silkValue && !unavailable && !unpriced
            ? ledValueExtras(&scene, frame, size: style.size, total: total, baseline: baseline,
                             glowStrength: glow == nil ? 0 : valueGlowStrength, fade: fade)
            : nil
        if shown == nil {
            valueCells.removeAll(keepingCapacity: true)
            for index in current.indices {
                let texture = current[index]
                let ascent = texture.baseline - texture.padding
                let descent = texture.size.height - texture.baseline - texture.padding
                valueCells.append(CGRect(x: currentX[index] + jitter.width, y: baseline - ascent, width: texture.advance,
                                         height: ascent + descent))
            }
            if valueCellsText != text { valueCellsText = text }
        }
        let rolls = layout.rolls && frame.effects.level > .off && !frame.effects.reduceMotion && s >= 80 && !unavailable
            && shown == nil
        let progress = rolls ? odometer.progress(at: frame.time) : 1
        if let prefix, fade > 0.01 {
            scene.add(OrbTextDraw(prefix, x: prefixX + jitter.width, baseline: baseline,
                                  color: (palette.unit ?? secondary(frame)).opacity(fade), glow: palette.shadow?.opacity(fade),
                                  glowRadius: palette.shadowRadius, shear: lean, shearPivot: baseline, clip: clip))
        }

        func place(_ texture: OrbTextTexture, x: CGFloat, offset: CGFloat, alpha rollAlpha: Double, cell: CGRect?) {
            let alpha = rollAlpha * fade
            guard alpha > 0.01 else { return }
            var textClip = clip
            if let cell {
                switch clip {
                case .none: textClip = .rect(cell)
                case .rect(let rect): textClip = .rect(rect.intersection(cell))
                case .liquid(let below, let rect): textClip = .liquid(below: below, rect: rect.intersection(cell))
                case .band: textClip = .rect(cell)
                }
            }
            let draw = OrbTextDraw(texture, x: x + jitter.width, baseline: baseline + offset, color: color.opacity(alpha),
                                   glow: glow?.opacity(alpha), glowRadius: glowRadius, shear: lean, shearPivot: baseline,
                                   clip: textClip)
            scene.add(layout.silkValue
                ? ledGlyph(draw, texture: texture, glowRadius: glow == nil ? 0 : glowRadius, alpha: alpha, extras: extras)
                : draw)
        }

        guard progress < 1, let old = odometer.previous, !unavailable else {
            for index in current.indices { place(current[index], x: currentX[index], offset: 0, alpha: 1, cell: nil) }
            return
        }
        previous.removeAll(keepingCapacity: true)
        for character in old {
            guard let texture = textCache.texture(character, style: style, language: language, scale: scene.backingScale) else { continue }
            previous.append(texture)
        }
        if previous.count == current.count {
            // Odometer: only the characters that changed roll, each clipped to its own cell (DESIGN 2a).
            let eased = 1 - pow(1 - progress, 3)
            let direction: CGFloat = odometer.rising ? -1 : 1
            let distance = style.size * 0.92
            var oldIterator = old.makeIterator()
            var newIterator = text.makeIterator()
            for index in current.indices {
                let oldCharacter = oldIterator.next()
                let newCharacter = newIterator.next()
                let x = currentX[index]
                if oldCharacter == newCharacter {
                    place(current[index], x: x, offset: 0, alpha: 1, cell: nil)
                    continue
                }
                let width = current[index].advance
                let cell = CGRect(x: x - 2 + jitter.width, y: baseline - style.size * 0.92, width: width + 4, height: style.size * 1.12)
                place(previous[index], x: x, offset: direction * distance * eased, alpha: 1 - eased, cell: cell)
                place(current[index], x: x, offset: -direction * distance * (1 - eased), alpha: eased, cell: cell)
            }
        } else {
            // Different lengths crossfade in the first half of the roll (0.12 s).
            let mix = min(1, progress / 0.5)
            var oldTotal: CGFloat = 0
            for texture in previous { oldTotal += texture.advance }
            var oldX = frame.center.x - oldTotal / 2
            for texture in previous {
                place(texture, x: oldX, offset: 0, alpha: 1 - mix, cell: nil)
                oldX += texture.advance
            }
            for index in current.indices { place(current[index], x: currentX[index], offset: 0, alpha: mix, cell: nil) }
        }
    }

    /// Glow and lean envelopes, set by the animator each frame.
    var valueGlowStrength = 0.0
    var leanAmount = 0.0

    // MARK: Unit and labels

    /// `text` replaces the content's unit (the unit leaving during a switch); `offset` and `alpha` move and fade it.
    private func addUnit(_ scene: inout OrbScene, _ frame: OrbFrame, size: CGFloat, line: CGFloat, palette: OrbReadoutPalette,
                         clip: OrbTextClip, text: String? = nil, offset: CGFloat = 0, alpha: Double = 1) {
        guard alpha > 0.01 else { return }
        if text == nil { shownUnit = frame.content.unit }
        let tracking = 0.5 * frame.unit
        let style = OrbTextStyle(size: max(Self.textFloor, size * frame.s), weight: .medium, tracking: tracking)
        guard let texture = textCache.texture(text ?? frame.content.unit, style: style, language: frame.content.language,
                                              scale: scene.backingScale) else { return }
        // Hovering the unit raises it from 55 % to 85 % with a 0.5 pt underline at 40 % (DESIGN 9, Switching).
        let hover = text == nil ? unitHoverAmount : 0
        let base = palette.unit ?? secondary(frame)
        let color = (hover > 0.001 ? base.opacity(1 + hover * (0.85 / 0.55 - 1)) : base).opacity(alpha)
        scene.add(OrbTextDraw(texture, centerX: frame.center.x + tracking / 2, baseline: line + offset,
                              color: color, glow: palette.shadow?.opacity(alpha), glowRadius: palette.shadowRadius, clip: clip))
        guard hover > 0.001 else { return }
        let width = texture.advance - tracking
        scene.add(.roundedRect(center: CGPoint(x: frame.center.x, y: line + offset + 0.18 * style.size),
                               halfSize: CGSize(width: width / 2, height: 0.25), rotation: 0, corner: 0,
                               color: base.opacity(0.4 / 0.55 * hover * alpha), glow: 0, glowAlpha: 0), blend: .over)
    }

    /// The unit's hover envelope (0...1), driven by the floating orb window.
    var unitHoverAmount = 0.0

    private func addLabels(_ scene: inout OrbScene, _ frame: OrbFrame, layout: OrbReadoutLayout, line: CGFloat,
                           palette: OrbReadoutPalette, clip: OrbTextClip) {
        let hover = frame.unavailable ? 0 : hoverAmount
        let labelAlpha = 1 - hover
        let x = frame.center.x
        let size = max(Self.textFloor, layout.titleSize * frame.s)
        let tracking = (frame.content.latinTracking ? 1.6 : 1.0) * frame.unit * size / (0.062 * frame.s)
        let style = OrbTextStyle(size: size, weight: .semibold, tracking: tracking)
        var color = palette.title ?? frame.tokens.tierColor(frame.heat)
        if palette.title == nil {
            if frame.isIdle || frame.unavailable { color = secondary(frame) }
            if frame.content.increaseContrast && !frame.dark { color = color.darkened(0.12) }
        }
        let limit = layout.titleMaxWidth * frame.s

        if labelAlpha > 0.01 {
            let title = frame.content.unavailableTitle ?? frame.content.titleLabel
            noteTitle(title, changedAt: frame.titleChangedAt, time: frame.time)
            let elapsed = frame.time - frame.titleChangedAt
            let duration = layout.titleCrossfades ? Self.titleFade : Self.titleSlide
            let transitioning = frame.content.unavailableTitle == nil && elapsed >= 0 && elapsed < duration
                && frame.content.previousTitleLabel != frame.content.titleLabel
            let p = transitioning ? elapsed / duration : 1
            let travel = frame.effects.reduceMotion || layout.titleCrossfades || frame.effects.level == .off ? 0 : size * 1.1
            // The sliding labels stay inside their own line, so the old one leaves and the new one arrives.
            let lineBox = CGRect(x: x - frame.s / 2, y: line - size * 1.25, width: frame.s, height: size * 1.6)
            let titleClip: OrbTextClip = switch clip {
            case .none: transitioning ? .rect(lineBox) : .none
            case .rect(let rect): .rect(rect.intersection(lineBox))
            case .liquid(let below, let rect): .liquid(below: below, rect: rect.intersection(lineBox))
            case .band: .rect(lineBox)
            }
            func draw(_ label: String, offset: CGFloat, alpha: Double) {
                guard alpha > 0.01, let (texture, labelTracking) = fittedTitle(label, style: style, limit: limit, frame: frame,
                                                                                  scale: scene.backingScale) else { return }
                scene.add(OrbTextDraw(texture, centerX: x + labelTracking / 2, baseline: line + offset, color: color.opacity(alpha),
                                      glow: palette.shadow, glowRadius: palette.shadowRadius, clip: titleClip))
            }
            if transitioning {
                // Staggered: the old title is gone (first 40 %) before the new one shows (from 35 %), so two titles
                // are never both clearly visible in the same place.
                let outgoing = Self.titleOut(p)
                let incoming = Self.titleIn(p)
                draw(frame.content.previousTitleLabel, offset: -travel * outgoing.travel, alpha: labelAlpha * outgoing.alpha)
                draw(title, offset: travel * (1 - incoming.travel), alpha: labelAlpha * incoming.alpha)
            } else if let fade = themeFade, frame.time - fade.start < Self.titleFade, fade.from != title {
                // A theme or language change renames the level: a plain 0.25 s crossfade, no level-up motion.
                let u = Easing.smoothstep(0, 1, (frame.time - fade.start) / Self.titleFade)
                draw(fade.from, offset: 0, alpha: labelAlpha * (1 - u))
                draw(title, offset: 0, alpha: labelAlpha * u)
            } else {
                draw(title, offset: 0, alpha: labelAlpha)
            }
        }
        if hover > 0.01 {
            let infoStyle = OrbTextStyle(size: max(Self.textFloor, size * 0.935 * layout.infoScale), weight: .medium, monospacedDigits: true)
            guard let texture = textCache.texture(frame.content.infoLine, style: infoStyle, language: frame.content.language,
                                                  scale: scene.backingScale) else { return }
            let infoLimit = (layout.infoMaxWidth ?? layout.titleMaxWidth) * frame.s
            let fit = max(0.7, min(1, infoLimit / max(texture.advance, 1)))
            scene.add(OrbTextDraw(texture, centerX: x, baseline: line, color: (palette.unit ?? secondary(frame)).opacity(1.25 * hover),
                                  glow: palette.shadow, glowRadius: palette.shadowRadius, scale: fit,
                                  pivot: CGPoint(x: x, y: line), clip: clip))
        }
    }

    /// Hover envelope, set by the animator each frame.
    var hoverAmount = 0.0

    /// Outgoing title of a level change at progress `p`: fades out over the first 40 % while sliding up.
    static func titleOut(_ p: Double) -> (alpha: Double, travel: Double) {
        (1 - Easing.smoothstep(0, 0.4, p), Easing.easeOut(min(1, p / 0.4)))
    }

    /// Incoming title: fades in only from 35 % while sliding into place.
    static func titleIn(_ p: Double) -> (alpha: Double, travel: Double) {
        (Easing.smoothstep(0.35, 0.8, p), Easing.easeOut(max(0, (p - 0.35) / 0.65)))
    }

    /// The label being crossfaded from after a rename of the shown level (theme or language change).
    private var themeFade: (from: String, start: TimeInterval)?
    private var shownTitle: String?
    private var shownTitleChangedAt: TimeInterval = -.infinity

    /// Whether a rename crossfade still runs (the animator keeps frames coming).
    func isRenaming(at time: TimeInterval) -> Bool {
        if let unitSwitch, time - unitSwitch.start < Self.unitSwitchDuration + 0.05 { return true }
        guard let themeFade else { return false }
        return time - themeFade.start < Self.titleFade
    }

    private func noteTitle(_ title: String, changedAt: TimeInterval, time: TimeInterval) {
        defer {
            shownTitle = title
            shownTitleChangedAt = changedAt
        }
        guard let shown = shownTitle, shown != title, changedAt == shownTitleChangedAt else { return }
        themeFade = (shown, time)
    }

    /// The title at its own size and tracking when it fits; otherwise the tracking tightens (linearly, down to 0),
    /// then the size shrinks (down to 80 %, never below the 7 pt floor); a title that still does not fit is drawn
    /// centered past the label width. It is never cut (DESIGN 1).
    private func fittedTitle(_ label: String, style: OrbTextStyle, limit: CGFloat, frame: OrbFrame, scale: CGFloat)
        -> (OrbTextTexture, CGFloat)? {
        let language = frame.content.language
        guard let natural = textCache.texture(label, style: style, language: language, scale: scale) else { return nil }
        let width = natural.advance - style.tracking
        guard width > limit else { return (natural, style.tracking) }
        let gaps = CGFloat(max(1, label.count - 1))
        let untracked = width - style.tracking * gaps
        var fitted = style
        if untracked <= limit, style.tracking > 0 {
            fitted.tracking = max(0, ((limit - untracked) / gaps * 20).rounded(.down) / 20)
        } else {
            fitted.tracking = 0
            let fit = TitleFit.scale(width: untracked, limit: limit)
            fitted.size = max(Self.textFloor, (style.size * fit * 4).rounded(.down) / 4)
        }
        guard let texture = textCache.texture(label, style: fitted, language: language, scale: scale) else {
            return (natural, style.tracking)
        }
        return (texture, fitted.tracking)
    }

    private func addOverflow(_ scene: inout OrbScene, _ frame: OrbFrame, layout: OrbReadoutLayout, clip: OrbTextClip) {
        let count = frame.content.generating.count
        guard count > 6, let offset = layout.dots, !frame.isIdle, !frame.unavailable else { return }
        let dots = OrbAgentDotsLayout(count: count, diameter: frame.s, center: frame.center, offset: offset)
        let style = OrbTextStyle(size: max(Self.textFloor, 0.055 * frame.s), weight: .semibold, monospacedDigits: true)
        guard let texture = textCache.overflowTexture(dots.overflow, style: style, language: frame.content.language,
                                                      scale: scene.backingScale) else { return }
        let capHeight = textCache.capHeight(style)
        scene.add(OrbTextDraw(texture, x: dots.overflowX, baseline: dots.y + capHeight / 2, color: secondary(frame), clip: clip))
    }

    // MARK: New peak

    private func addPeakPill(_ scene: inout OrbScene, _ frame: OrbFrame, line: CGFloat) {
        let elapsed = frame.peakAge
        let total = Self.peakDuration
        guard frame.effects.celebrate, elapsed >= 0, elapsed < total else { return }
        let fadeOut = Easing.smoothstep(total - 0.32, total, elapsed)
        let alpha = min(1, elapsed / (frame.effects.reduceMotion ? 0.2 : 0.12)) * (1 - fadeOut)
        let pop = frame.effects.reduceMotion ? 1 : (0.55 + 0.45 * Easing.springPop(elapsed)) * (1 - 0.04 * fadeOut)
        guard alpha > 0.01 else { return }

        let tracking = (frame.content.latinTracking ? 0.9 : 0.4) * frame.unit
        let style = OrbTextStyle(size: max(Self.textFloor, 0.056 * frame.s), weight: .heavy, tracking: tracking)
        guard let texture = textCache.texture(frame.content.peakLabel, style: style, language: frame.content.language,
                                              scale: scene.backingScale) else { return }
        let center = CGPoint(x: frame.center.x, y: line)
        let height = 0.082 * frame.s * pop
        let width = (texture.advance + 0.085 * frame.s) * pop
        let gold = HeatPalette.gold.mixOKLab(HeatPalette.whiteHot, 0.2).mixOKLab(HeatPalette.color(0.92), 0.25)
        scene.add(.roundedRect(center: center, halfSize: CGSize(width: width / 2, height: height / 2), rotation: 0,
                               corner: height / 2, color: gold.opacity(alpha), glow: frame.dark ? 0.025 * frame.s : 0.012 * frame.s,
                               glowAlpha: frame.dark ? 0.55 : 0.3), blend: .over)
        let capHeight = textCache.capHeight(style) * pop
        scene.add(OrbTextDraw(
            texture, centerX: center.x + tracking / 2 * pop, baseline: center.y + capHeight / 2,
            color: RGBColor(red: 0.10, green: 0.07, blue: 0.02, alpha: alpha),
            scale: pop, pivot: CGPoint(x: center.x, y: center.y + capHeight / 2)
        ))
    }
}

/// Odometer state of the value readout (DESIGN 2a): the shown string and the roll from the previous one.
struct OrbOdometer {
    static let duration: TimeInterval = 0.24

    private(set) var current = "0"
    private(set) var previous: String?
    private(set) var changedAt: TimeInterval = -.infinity
    private(set) var rising = true
    private var value = 0.0

    mutating func update(_ text: String, value: Double, at time: TimeInterval) {
        guard text != current else {
            self.value = value
            return
        }
        previous = current
        rising = value >= self.value
        current = text
        changedAt = time
        self.value = value
    }

    mutating func reset(_ text: String, value: Double) {
        current = text
        previous = nil
        changedAt = -.infinity
        self.value = value
    }

    /// 0 at the change, 1 when settled.
    func progress(at time: TimeInterval) -> Double {
        let t = (time - changedAt) / Self.duration
        guard t < 1 else { return 1 }
        return max(0, t)
    }
}
