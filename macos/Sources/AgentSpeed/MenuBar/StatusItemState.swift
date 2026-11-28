import AgentSpeedCore
import Foundation

/// Everything one menu bar frame shows. Colors are resolved when drawing, against the menu bar's own appearance.
struct StatusItemState: Equatable {
    var layout: StatusItemLayout
    var ring: Ring
    var colored: Bool
    var value: String
    var idle: Bool
    var increasedContrast: Bool
    /// The money block (DESIGN 3), when shown.
    var secondary: StatusItemSecondary?
    /// The readout blocks (DESIGN 10), each when shown: the network speed, the intelligence block, the quota block.
    var traffic: StatusItemTraffic?
    var intelligence: StatusItemIntelligence?
    var quota: StatusItemQuota?

    static let glowFade: TimeInterval = 0.35

    /// `valueWidth`, `secondaryWidth`, `intelligenceWidth` and `quotaCells` are the rooms the governors reserve for the
    /// value, the blocks' numbers and the quota block's labels and percents (nil: exactly the current text).
    @MainActor
    init(model: AppModel, time: TimeInterval, valueWidth: CGFloat? = nil, secondaryWidth: CGFloat? = nil,
         intelligenceWidth: CGFloat? = nil, quotaCells: (label: CGFloat, number: CGFloat)? = nil) {
        let gauge = model.gauge
        let (text, unit) = StatusItemValue.current(model)
        layout = StatusItemLayout(model: model, unit: unit, valueWidth: valueWidth ?? StatusItemLayout.valueWidth(of: text))
        colored = model.settings.barColoredGlyph
        ring = layout.showsGlyph ? Ring(gauge: gauge, at: time, colored: colored, flickers: Self.flickers(model)) : .hidden
        value = text
        idle = gauge.tier == .idle
        increasedContrast = model.accessibility.increaseContrast
        secondary = StatusItemSecondary(model: model)
        if let secondaryWidth, let width = secondary?.numberWidth { secondary?.cell = max(secondaryWidth, width) }
        layout.secondaryWidth = secondary?.width ?? 0
        traffic = StatusItemTraffic(model: model)
        intelligence = StatusItemIntelligence(model: model)
        if let intelligenceWidth, let cell = intelligence?.cell { intelligence?.cell = max(intelligenceWidth, cell) }
        quota = StatusItemQuota(model: model)
        if let quotaCells, let label = quota?.labelCell, let number = quota?.numberCell {
            quota?.labelCell = max(quotaCells.label, label)
            quota?.numberCell = max(quotaCells.number, number)
        }
        layout.trafficWidth = traffic?.width ?? 0
        layout.intelligenceWidth = intelligence?.width ?? 0
        layout.quotaWidth = quota?.width ?? 0
    }

    /// Whether the redline flicker may run (DESIGN section 3; off with Reduce Motion or effects off).
    @MainActor
    static func flickers(_ model: AppModel) -> Bool {
        !model.accessibility.reduceMotion && model.settings.effects != .off
    }

    /// Frames are needed while the ring is shown and its springs move, a glow fades, or the redline flicker runs.
    @MainActor
    static func needsFrames(_ model: AppModel, at time: TimeInterval) -> Bool {
        guard model.settings.barMode.showsGlyph else { return false }
        let gauge = model.gauge
        if flickers(model) && gauge.tier == .redline { return true }
        if time - gauge.tierChangedAt < glowFade { return true }
        return !gauge.isSettled(at: time)
    }
}

extension StatusItemState {
    /// The gauge ring exactly as drawn. Each change of the state puts a new image in the menu bar, which the system
    /// then re-lays out and snapshots for every display, so the ring keeps only what can be seen: the fill in
    /// half-pixel steps at 2x (which also pick its heat color, a 1/132 step being invisible), the redline flicker and
    /// the glow in steps of 5 %. Spring motion below a step changes nothing, and a hidden glyph is one constant ring.
    struct Ring: Equatable {
        var fraction: Double
        var fillOpacity: Double
        /// Glow strength around the fill, 0...1.
        var glow: Double

        static let fractionSteps = 132.0
        static let flickerRate = 6.0
        static let hidden = Ring(fraction: 0, fillOpacity: 1, glow: 0)

        init(fraction: Double, fillOpacity: Double, glow: Double) {
            self.fraction = fraction
            self.fillOpacity = fillOpacity
            self.glow = glow
        }

        @MainActor
        init(gauge: LiveGauge, at time: TimeInterval, colored: Bool, flickers: Bool) {
            fraction = (gauge.heat(at: time) * Self.fractionSteps).rounded() / Self.fractionSteps
            fillOpacity = flickers && gauge.tier == .redline ? (Self.flicker(at: time) * 20).rounded() / 20 : 1
            glow = colored ? (Self.glow(gauge: gauge, at: time) * 20).rounded() / 20 : 0
        }

        /// Smooth random opacity between 85 % and 100 % at 6 Hz.
        private static func flicker(at time: TimeInterval) -> Double {
            let phase = time * flickerRate
            let index = Int(phase.rounded(.down))
            let blend = Easing.smoothstep(0, 1, phase - Double(index))
            let a = Noise.hash(index, 17), b = Noise.hash(index + 1, 17)
            return 0.85 + 0.15 * (a + (b - a) * blend)
        }

        /// Fades the glow in and out across a tier change into or out of overdrive.
        @MainActor
        private static func glow(gauge: LiveGauge, at time: TimeInterval) -> Double {
            let now = gauge.tier >= .overdrive ? 1.0 : 0
            let before = gauge.previousTier >= .overdrive ? 1.0 : 0
            guard now != before else { return now }
            let progress = Easing.easeOut((time - gauge.tierChangedAt) / StatusItemState.glowFade)
            return before + (now - before) * progress
        }
    }
}
