import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// Values the renderer needs that come from observable state (resolved when they change, never per frame).
struct OrbContent {
    var diameter: CGFloat
    var tokens: OrbTokens
    var increaseContrast: Bool
    var reduceTransparency = false
    var unit: String
    /// The speed title (DESIGN section 1) and the one it replaces while the label slides.
    var titleLabel: String
    var previousTitleLabel: String
    var infoLine: String
    var peakLabel: String
    var latinTracking: Bool
    var language: AppLanguage
    var generating: [SourceKind]
    /// The readout is drawn only on the floating orb; the dashboard shows its own numbers.
    var showsReadout: Bool
    var style: OrbStyleKind = .led
    /// The source bezel of an orb tracking a subset (DESIGN 2b); nil for all sources.
    var bezel: OrbBezel?
    /// Set when every source of the orb is unavailable (DESIGN 2b): the value reads `–` and the title explains.
    var unavailableTitle: String?
    /// The money unit (DESIGN 9); nil shows tok/s.
    var money: OrbMoneyText?
    /// Thumbnails and previews (Settings) cap effects and draw at small sizes.
    var isThumbnail = false
    /// The two-line readout while it is on for this orb (DESIGN 10).
    var lines: OrbLines?
    /// The `character` style's character and readouts (CHARACTERS 7, 8); nil for other styles.
    var character: OrbCharacterContent?

    /// Below 64 pt only the value is drawn (DESIGN 2a, Shared foundation).
    var valueOnly: Bool { diameter < 64 }
}

/// The value of an orb in the money unit (DESIGN 9): the number the odometer rolls (with the symbol in front where
/// the style has no unit line), a small `≥` before it when part of the value is unpriced, and `–` in the idle value
/// color when nothing is priced.
struct OrbMoneyText: Equatable {
    var number: String
    /// micro-USD per second, for the odometer's direction.
    var value: Double
    var atLeast: Bool
    var unknown: Bool
}

/// The colour rim of an orb tracking a subset: one arc per source, dashed when unavailable.
struct OrbBezel: Equatable {
    struct Arc: Equatable {
        var color: RGBColor
        var available: Bool
    }

    var arcs: [Arc]
    /// 0...1 while the bezel draws on after a split (DESIGN 2b, Split and merge); 1 when complete.
    var drawOn: Double = 1
}

/// One frame of an orb, derived from its gauge, its animator and its content at a time.
struct OrbFrame {
    let time: TimeInterval
    let dt: Double
    let geometry: OrbGeometry
    let content: OrbContent
    let effects: OrbEffects
    /// Smoothed heat and overdrive (DESIGN 1).
    let heat: Double
    let overdrive: Double
    /// Unsmoothed heat and overdrive, stepping with the 500 ms engine tick (DESIGN 2a conventions).
    let rawHeat: Double
    let rawOverdrive: Double
    /// `led`: heat during the new-peak gauge sweep.
    let displayHeat: Double
    let tier: SpeedTier
    /// The orb's redline of the selected metric.
    let redline: Double
    let tierChangedAt: TimeInterval
    let titleChangedAt: TimeInterval
    /// Tier flash: 1 at an effect-tier change, fading to 0 over 0.4 s.
    let flash: Double
    /// Aura breathing factor around 1 and its accumulated phase (0...1).
    let breath: Double
    let breathPhase: Double
    let redlineAmount: Double
    /// 0 at idle, 1 when active (eased).
    let activity: Double
    /// Progress (0...1) of the idle sign of life on the global clock, nil when none runs.
    let signOfLife: Double?
    /// Seconds since the last celebrated new peak (or +∞).
    let peakAge: TimeInterval
    /// Alpha multiplier of this style during a style crossfade.
    let alpha: Double
    /// The first frame after the orb could not be seen: show the current state, never a catch-up animation.
    var resumed = false
    /// How far the readout stack sits below its resting place while the two-line block shows above it (DESIGN 10).
    var readoutShift: CGFloat = 0
    /// The aura's flare after a response pulse (0...1), for styles that draw their own aura.
    var auraFlare: Double = 0
    /// The speed title's step, 0 (idle) ... 13 (rocket): the `character` director's level (CHARACTERS 5).
    var speedLevel = 0
    /// The value the readout shows (tok/s), for styles that roll their own digits.
    var readoutValue = 0.0

    var tokens: OrbTokens { content.tokens }
    var dark: Bool { content.tokens.isDark }
    var s: CGFloat { geometry.diameter }
    var center: CGPoint { geometry.center }
    /// Scale relative to the default 148 pt orb, for point-sized details.
    var unit: CGFloat { geometry.diameter / 148 }
    /// How far (in diameters from the center) the aura and pulses reach; the embedded orb stays inside its margins.
    var reach: CGFloat { content.showsReadout ? 0.75 : 0.66 }
    /// Light elements add up on dark glass and blend normally on porcelain.
    var glowBlend: OrbBlend { dark ? .additive : .over }
    var isIdle: Bool { tier == .idle }
    /// Level of detail: small orbs drop fine details (DESIGN 2a, LOD).
    var isSmall: Bool { geometry.diameter < 120 }
    var unavailable: Bool { content.unavailableTitle != nil }
    /// The heat the instrument shows: 0 while unavailable.
    var instrumentHeat: Double { unavailable ? 0 : heat }

    /// Label color of DESIGN 2a: heat on dark; on light, heat darkened 18 % (OKLab L).
    func labelColor(_ h: Double) -> RGBColor { HeatPalette.color(h, dark: dark) }

    /// *hot* of DESIGN 2a: `mix(heat(h), #FFF7D6, 0.55 + 0.45 o)` in dark, `mix(label, #FFB020, 0.3)` in light.
    func hotColor(_ h: Double) -> RGBColor {
        dark ? HeatPalette.color(h).mix(HeatPalette.whiteHot, 0.55 + 0.45 * overdrive)
            : labelColor(h).mix(HeatPalette.amberHot, 0.3)
    }

    /// The point at `degrees` (clockwise from 3 o'clock, y down) and `radius` × S from the center.
    func point(_ degrees: Double, _ radius: CGFloat) -> CGPoint {
        geometry.point(angle: degrees, radius: radius * s)
    }

    /// Fade of particles inside the readout zone (an ellipse 0.30 × 0.19 S around c + (0, 0.07 S)), so they never
    /// compete with the readout (DESIGN 2a, Shared foundation).
    func readoutZoneFade(at point: CGPoint) -> Double {
        guard content.showsReadout else { return 1 }
        let qx = (point.x - center.x) / (0.30 * s)
        let qy = (point.y - center.y - readoutShift - 0.07 * s) / (0.19 * s)
        return 1 - 0.65 * exp(-Double(qx * qx + qy * qy) * 1.6)
    }
}

/// Where a style places the shared readout (the frame table of DESIGN 2a, Shared foundation), in diameters.
struct OrbReadoutLayout {
    enum TitlePlacement {
        /// Baseline this far below the unit baseline (or the value when there is no unit).
        case belowUnit(CGFloat)
        case belowValue(CGFloat)
        /// Baseline at the center plus this.
        case fixed(CGFloat)
    }

    var valueSize: CGFloat
    var valueBaseline: CGFloat
    var valueMaxWidth: CGFloat
    /// Nil: no unit.
    var unitSize: CGFloat?
    var unitGap: CGFloat
    var title: TitlePlacement
    var titleSize: CGFloat
    var titleMaxWidth: CGFloat
    var infoScale: CGFloat = 1
    var infoMaxWidth: CGFloat?
    /// Agent dots center line below the center; nil: no dots.
    var dots: CGFloat?
    var dotHalo = false
    var pillCenter: CGFloat
    var rolls = true
    var titleCrossfades = false
    var valueGlow = true
    /// Forward lean from overdrive and redline jitter of the value (`minimal`, which barely moves, has neither).
    var valueMotion = true
    /// The v1.4 `led` value (DESIGN 2.1, Readout): growing and doubled glow, a dark backing, a stronger lean and jitter
    /// and the redline chromatic split.
    var silkValue = false

    /// Smaller orbs draw no unit line (DESIGN 2a, Shared foundation): at the 7 pt floor it would outgrow its gap.
    static let unitFloor: CGFloat = 96

    static func hasUnitLine(style: OrbStyleKind, diameter: CGFloat) -> Bool {
        style != .minimal && diameter >= unitFloor
    }

    /// The layout at a diameter: below `unitFloor` without the unit line, the title where it was.
    func at(diameter: CGFloat) -> OrbReadoutLayout {
        guard diameter < Self.unitFloor, unitSize != nil else { return self }
        var layout = self
        if case .belowUnit(let gap) = title { layout.title = .belowValue(unitGap + gap) }
        layout.unitSize = nil
        return layout
    }

    /// The title's baseline below the center, × S.
    var titleOffset: CGFloat {
        switch title {
        case .belowUnit(let gap): valueBaseline + (unitSize == nil ? 0 : unitGap) + gap
        case .belowValue(let gap): valueBaseline + gap
        case .fixed(let offset): offset
        }
    }

    static let led = OrbReadoutLayout(
        valueSize: 0.24, valueBaseline: 0.012, valueMaxWidth: 0.62, unitSize: 0.072, unitGap: 0.104,
        title: .belowUnit(0.09), titleSize: 0.062, titleMaxWidth: 0.58, infoMaxWidth: 0.62, dots: 0.3785, pillCenter: -0.232
    )
}

/// What each style contributes to an orb (DESIGN 2a). Springs, trackers and particles that must survive a style change
/// live in the animator; a style keeps only its own instrument state, created from the current values without a jump.
@MainActor
protocol OrbStyleRenderer: AnyObject {
    var kind: OrbStyleKind { get }
    var readoutLayout: OrbReadoutLayout { get }
    /// Where the two-line readout goes (DESIGN 10).
    var linePlacement: OrbLinePlacement { get }
    /// Layers of the shared foundation this style draws.
    var hasAura: Bool { get }
    var hasWave: Bool { get }
    var hasRays: Bool { get }

    /// Advances the style's own simulation to the frame (called once per rendered frame, before `build`).
    func step(_ frame: OrbFrame)
    /// Adds the instrument between the body and the readout.
    func build(into scene: inout OrbScene, _ frame: OrbFrame)
    /// Adds the readout; styles that split or recolor it override this.
    func buildReadout(into scene: inout OrbScene, _ frame: OrbFrame, readout: OrbReadoutBuilder)
    /// Adds the two-line readout; `liquid` splits it at its surface.
    func buildLines(into scene: inout OrbScene, _ frame: OrbFrame, lines: OrbLineReadout)
    /// The frame class the instrument needs at rest in this state (transitions are handled by the animator).
    func ambientFrameClass(_ frame: OrbFrame) -> OrbFrameClass
    /// Whether anything of the style still moves (springs settling, particles alive).
    func isBusy(_ frame: OrbFrame) -> Bool
    /// A `responseCompleted` of the orb's sources (strength 0.3...1, heat of the response).
    func pulse(at time: TimeInterval, strength: Double, heat: Double)
    /// A celebrated new peak.
    func celebrate(at time: TimeInterval)
    /// Duration of the idle sign of life (0: none).
    var signOfLifeDuration: TimeInterval { get }
}

extension OrbStyleRenderer {
    var hasAura: Bool { true }
    var hasWave: Bool { true }
    var hasRays: Bool { true }
    var signOfLifeDuration: TimeInterval { 0.9 }

    func buildReadout(into scene: inout OrbScene, _ frame: OrbFrame, readout: OrbReadoutBuilder) {
        readout.add(into: &scene, frame, layout: readoutLayout)
    }

    func buildLines(into scene: inout OrbScene, _ frame: OrbFrame, lines: OrbLineReadout) {
        lines.add(into: &scene, frame, placement: linePlacement,
                    palette: .standard(dark: frame.dark, increaseContrast: frame.content.increaseContrast))
    }

    func pulse(at time: TimeInterval, strength: Double, heat: Double) {}
    func celebrate(at time: TimeInterval) {}
}

/// Creates the renderer of a style.
@MainActor
enum OrbStyles {
    static func make(_ kind: OrbStyleKind) -> OrbStyleRenderer {
        switch kind {
        case .led: LedStyle()
        case .needle: NeedleStyle()
        case .liquid: LiquidStyle()
        case .reactor: ReactorStyle()
        case .minimal: MinimalStyle()
        case .character: CharacterStyle()
        }
    }
}

extension OrbContent {
    /// Content without strings, for scheduling questions and tests.
    static func placeholder(diameter: CGFloat, dark: Bool = true) -> OrbContent {
        OrbContent(
            diameter: diameter, tokens: dark ? .dark : .light, increaseContrast: false, unit: "", titleLabel: "",
            previousTitleLabel: "", infoLine: "", peakLabel: "", latinTracking: false, language: .en, generating: [],
            showsReadout: true
        )
    }
}

extension OrbFrame {
    /// A frame of the current state without content, for frame-demand questions (`ambientFrameClass`, `isBusy`).
    @MainActor
    static func scheduling(time: TimeInterval, gauge: LiveGauge?, effects: OrbEffects, unavailable: Bool,
                           geometry: OrbGeometry?) -> OrbFrame {
        let diameter = geometry?.diameter ?? 148
        var content = OrbContent.placeholder(diameter: diameter)
        if unavailable { content.unavailableTitle = "" }
        let heat = gauge?.heat(at: time) ?? 0
        let overdrive = gauge?.overdrive(at: time) ?? 0
        return OrbFrame(
            time: time, dt: 0, geometry: geometry ?? OrbGeometry(diameter: diameter, canvasSide: diameter * 1.5),
            content: content, effects: effects, heat: heat, overdrive: overdrive,
            rawHeat: gauge.map { HeatPalette.heat(value: $0.rawValue, redline: $0.redline) } ?? 0,
            rawOverdrive: gauge.map { HeatPalette.overdrive(value: $0.rawValue, redline: $0.redline) } ?? 0,
            displayHeat: heat, tier: gauge?.tier ?? .idle, redline: gauge?.redline ?? 400,
            tierChangedAt: gauge?.tierChangedAt ?? -.infinity, titleChangedAt: gauge?.titleChangedAt ?? -.infinity,
            flash: 0, breath: 1, breathPhase: 0, redlineAmount: gauge?.tier == .redline ? 1 : 0,
            activity: gauge?.tier == .idle ? 0 : 1, signOfLife: nil, peakAge: .infinity, alpha: 1
        )
    }
}
