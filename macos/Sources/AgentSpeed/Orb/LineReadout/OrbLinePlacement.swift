import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit

/// Where a style puts the two-line readout (DESIGN 10, Floating orb readout), in diameters from the center.
struct OrbLinePlacement: Equatable {
    /// Baseline of the line nearest the value.
    var nearestBaseline: CGFloat
    /// The lines sit below the value (`needle`, ↓ line nearest); otherwise above it (↑ line nearest).
    var below: Bool
    /// Text size `t` = max(7 pt, size × S).
    var size: CGFloat
    /// A line must fit in this × S, else the readout is not drawn.
    var fit: CGFloat
    /// The readout stack moves down while the block shows, so block, value, unit and title stay centered as one stack;
    /// `reactor` and `needle` keep their readout in a fixed window or hub.
    var recenters = false
    /// The thinking line's baseline above the value where it sits lower than the nearest line (`reactor`, so the larger
    /// intelligence line clears the core's rim), × S.
    var thinkingBaseline: CGFloat?
    /// The thinking line ends with `tok/s`; `needle` prints the unit on its dial instead.
    var thinkingUnit = true
    /// The performance readout sits in a round hub (`needle`): each line's bottom corners stay within this × S of the
    /// center.
    var hubReach: CGFloat?
    /// The 性能 line's size `p`, × t: 1.25, and 1.1 in `needle`'s hub (DESIGN 10, Performance readout).
    var scoreScale: CGFloat = 1.25

    static let led = OrbLinePlacement(nearestBaseline: -0.245, below: false, size: 0.050, fit: 0.44, recenters: true)
    static let liquid = led
    static let reactor = OrbLinePlacement(nearestBaseline: -0.175, below: false, size: 0.046, fit: 0.32, thinkingBaseline: -0.155)
    static let needle = OrbLinePlacement(nearestBaseline: 0.108, below: true, size: 0.046, fit: 0.285, thinkingUnit: false,
                                         hubReach: 0.223, scoreScale: 1.1)
    static let minimal = OrbLinePlacement(nearestBaseline: -0.228, below: false, size: 0.054, fit: 0.50, recenters: true)

    static func of(_ style: OrbStyleKind) -> OrbLinePlacement {
        switch style {
        case .led: .led
        case .needle: .needle
        case .liquid: .liquid
        case .reactor: .reactor
        case .minimal: .minimal
        }
    }

    /// Line pitch, × t.
    static let pitch: CGFloat = 1.32
    /// Gaps between arrow, number and unit, × t.
    static let gap: CGFloat = 0.16
    /// Arrow box, × t.
    static let arrowWidth: CGFloat = 0.50
    static let arrowHeight: CGFloat = 0.66
    /// Unit size, × t.
    static let unitScale: CGFloat = 0.82
    /// The gap after the 性能 line's glyphs, × p (DESIGN 10, Performance readout).
    static let intelligenceGap: CGFloat = 0.20

    /// Never below the 7 pt floor of every orb label.
    func textSize(diameter s: CGFloat) -> CGFloat { max(7, size * s) }

    /// Cap height of the orb fonts, × size.
    static let capHeight: CGFloat = 0.70

    /// Descent of a title line below its baseline, × its size.
    static let titleDescent: CGFloat = 0.22
    /// Clearance kept between the title and the agent dots, × S.
    static let dotsClearance: CGFloat = 0.05

    /// How far the readout stack (value, unit, title, pill and this block) moves down while the block shows above the
    /// value, so the whole stack stays centered (DESIGN 10): half the block's height over the value's cap top, but never
    /// so far that the title comes closer to the agent dots than the clearance. The block's top is the cap top of its
    /// upper line, the larger intelligence line in that readout, which `single` (eased, 0...1) glides down to the
    /// nearest baseline while the thinking line is hidden.
    func stackShift(layout: OrbReadoutLayout, diameter s: CGFloat, format: OrbLineFormat, single: CGFloat = 0) -> CGFloat {
        guard recenters, !below else { return 0 }
        let t = textSize(diameter: s)
        let top = format == .intelligence ? scoreScale * t : t
        let nearest = format == .intelligence ? (thinkingBaseline ?? nearestBaseline) : nearestBaseline
        let blockTop = nearest * s - (1 - single) * Self.pitch * t - Self.capHeight * top
        let valueTop = (layout.valueBaseline - Self.capHeight * layout.valueSize) * s
        let centered = max(0, (valueTop - blockTop) / 2)
        guard let dots = layout.dots else { return centered }
        let titleSize = max(7, layout.titleSize * s)
        let titleBottom = layout.titleOffset * s + Self.titleDescent * titleSize
        let dotsTop = dots * s - OrbAgentDotsLayout.dotDiameter(s) / 2
        return min(centered, max(0, dotsTop - Self.dotsClearance * s - titleBottom))
    }

    /// Baselines of the network readout's line nearest the value and of the other one.
    func baselines(center y: CGFloat, diameter s: CGFloat) -> (nearest: CGFloat, other: CGFloat) {
        let nearest = y + nearestBaseline * s
        let pitch = Self.pitch * textSize(diameter: s)
        return (nearest, below ? nearest + pitch : nearest - pitch)
    }

    /// Baselines of the intelligence line and the thinking line under it: above the value the thinking line is the
    /// nearest; below it (`needle`) the larger intelligence line keeps a `t` line's cap top. `single` (eased, 0...1)
    /// glides the intelligence line to the single-line position while the thinking line is hidden: onto the thinking
    /// line's baseline above the value; below it, where it already is.
    func intelligenceBaselines(center y: CGFloat, diameter s: CGFloat, single: CGFloat = 0) -> (intelligence: CGFloat, thinking: CGFloat) {
        let t = textSize(diameter: s)
        let pitch = Self.pitch * t
        guard below else {
            let thinking = y + (thinkingBaseline ?? nearestBaseline) * s
            return (thinking - (1 - single) * pitch, thinking)
        }
        let intelligence = y + nearestBaseline * s + Self.capHeight * (scoreScale - 1) * t
        return (intelligence, intelligence + pitch)
    }
}

/// The intelligence readout's glyphs in a language (`intel.glyph`, `intel.thinking.glyph`).
struct OrbLineGlyphs: Hashable {
    var intelligence: String
    var thinking: String

    init(_ language: AppLanguage) {
        intelligence = Localizer.string("intel.glyph", language: language)
        thinking = Localizer.string("intel.thinking.glyph", language: language)
    }
}

/// The readout's widths, measured with the platform font once per size (DESIGN 10: the readout shows only where its
/// widest lines fit, computed per size, never truncated or shrunk below 7 pt). The network readout keeps fixed cells:
/// the arrow, the `8.88` number cell and the unit cell (`MB/s`, `Mb/s` for bits). Each performance line is centred
/// with its current text, so only its widest text is measured: the 性能 line with `8,888`, the thinking line
/// with `8.88` (and `tok/s` where the line carries it).
@MainActor
enum OrbLineMetrics {
    struct Cells: Equatable {
        var lead: CGFloat
        var number: CGFloat
        var unit: CGFloat

        /// lead, gap, number, gap, unit.
        func lineWidth(size t: CGFloat) -> CGFloat { lead + 2 * OrbLinePlacement.gap * t + number + unit }
    }

    /// The widest intelligence and thinking lines.
    struct Widest: Equatable {
        var intelligence: CGFloat
        var thinking: CGFloat

        var line: CGFloat { max(intelligence, thinking) }
    }

    private struct CellsKey: Hashable {
        var size: CGFloat
        var bits: Bool
    }

    private struct WidestKey: Hashable {
        var size: CGFloat
        var language: AppLanguage
        var unit: Bool
        var scoreScale: CGFloat
    }

    private static var cellsCache: [CellsKey: Cells] = [:]
    private static var widestCache: [WidestKey: Widest] = [:]

    /// The thinking line's unit (DESIGN 10, Intelligence readout).
    static let thinkingUnit = "tok/s"

    static func numberFont(_ t: CGFloat) -> NSFont { .monospacedDigitSystemFont(ofSize: t, weight: .semibold) }
    static func unitFont(_ t: CGFloat) -> NSFont { .systemFont(ofSize: OrbLinePlacement.unitScale * t, weight: .medium) }
    static func glyphFont(_ t: CGFloat) -> NSFont { .systemFont(ofSize: OrbLinePlacement.unitScale * t, weight: .semibold) }

    static func cells(size t: CGFloat, bits: Bool) -> Cells {
        let key = CellsKey(size: t, bits: bits)
        if let cached = cellsCache[key] { return cached }
        let cells = Cells(lead: OrbLinePlacement.arrowWidth * t, number: width("8.88", numberFont(t)),
                          unit: width(bits ? "Mb/s" : "MB/s", unitFont(t)))
        if cellsCache.count > 64 { cellsCache.removeAll(keepingCapacity: true) }
        cellsCache[key] = cells
        return cells
    }

    static func widest(size t: CGFloat, language: AppLanguage, unit: Bool = true, scoreScale: CGFloat = 1.25) -> Widest {
        let key = WidestKey(size: t, language: language, unit: unit, scoreScale: scoreScale)
        if let cached = widestCache[key] { return cached }
        let glyphs = OrbLineGlyphs(language)
        let p = scoreScale * t
        let gap = OrbLinePlacement.gap * t
        let widest = Widest(
            intelligence: width(glyphs.intelligence, glyphFont(p), language: language) + OrbLinePlacement.intelligenceGap * p
                + width(PerfFormat.measureText, numberFont(p)),
            thinking: width(glyphs.thinking, glyphFont(t), language: language) + gap + width("8.88", numberFont(t))
                + (unit ? gap + width(thinkingUnit, unitFont(t)) : 0)
        )
        if widestCache.count > 64 { widestCache.removeAll(keepingCapacity: true) }
        widestCache[key] = widest
        return widest
    }

    /// Whether the readout is drawn at this size: its widest lines fit in the placement's `fit × S` and, in a hub, keep
    /// their bottom corners within its reach.
    static func fits(_ placement: OrbLinePlacement, diameter s: CGFloat, format: OrbLineFormat, language: AppLanguage) -> Bool {
        let t = placement.textSize(diameter: s)
        switch format {
        case .traffic(let bits):
            return cells(size: t, bits: bits).lineWidth(size: t) <= placement.fit * s
        case .intelligence:
            let widest = widest(size: t, language: language, unit: placement.thinkingUnit, scoreScale: placement.scoreScale)
            guard widest.line <= placement.fit * s else { return false }
            guard let reach = placement.hubReach else { return true }
            let baselines = placement.intelligenceBaselines(center: 0, diameter: s)
            return hypot(widest.intelligence / 2, baselines.intelligence) <= reach * s
                && hypot(widest.thinking / 2, baselines.thinking) <= reach * s
        }
    }

    /// Advance width of `text` as the glyph atlas lays it out (the language picks the CJK face).
    private static func width(_ text: String, _ font: NSFont, language: AppLanguage = .en) -> CGFloat {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: font,
            NSAttributedString.Key(kCTLanguageAttributeName as String): language.rawValue,
        ]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
        return CGFloat(CTLineGetTypographicBounds(line, nil, nil, nil))
    }
}
