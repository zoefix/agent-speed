import AgentSpeedCore
import SwiftUI

/// Layers every style shares (DESIGN 2a, Shared foundation): aura, response wave, new-peak rays, source bezel and
/// agent dots. The readout is `OrbReadoutBuilder`.
@MainActor
enum OrbSharedLayers {
    static let pulseDuration: TimeInterval = 0.7

    // MARK: Aura

    /// `scale` and `hot` (a share of white-hot by overdrive) tune it per style (`led` v1.4: 0.8, 0.25).
    static func addAura(_ scene: inout OrbScene, _ frame: OrbFrame, flare: Double, scale: Double = 1, hot: Double = 0) {
        let heat = frame.instrumentHeat
        let alpha = (frame.tokens.auraOpacity * heat * frame.breath * (1 + 0.7 * flare) + (frame.dark ? 0.05 : 0.03) * flare) * scale
        guard alpha > 0.004 else { return }
        var color = frame.tokens.heat(max(heat, 0.12))
        if hot > 0 { color = color.mix(HeatPalette.whiteHot, hot * frame.overdrive) }
        scene.add(.radial(center: frame.center, inner: 0.5 * frame.s, outer: frame.reach * frame.s, color: color, alpha: min(1, alpha)),
                  blend: frame.glowBlend)
    }

    // MARK: Response wave

    static func addPulses(_ scene: inout OrbScene, _ frame: OrbFrame, pulses: ContiguousArray<OrbAnimator.Pulse>) {
        for pulse in pulses {
            let p = ((frame.time - pulse.start) / pulseDuration).clamped(to: 0...1)
            let eased = Easing.easeOut(p)
            let color = frame.dark
                ? HeatPalette.color(pulse.heat).mixOKLab(HeatPalette.whiteHot, 0.18)
                : HeatPalette.color(pulse.heat, dark: false)
            let radius: CGFloat
            let alpha: Double
            if frame.effects.reduceMotion {
                radius = 0.53 * frame.s
                alpha = 0.5 * sin(.pi * p) * pulse.strength
            } else {
                radius = (0.5 + (frame.reach - 0.51) * eased) * frame.s
                alpha = 0.6 * (1 - eased) * (0.45 + 0.55 * pulse.strength)
            }
            let width = max(0.3, 2 * frame.unit * (1 - eased) * (0.55 + 0.45 * pulse.strength))
            scene.add(.ring(center: frame.center, radius: radius, width: width, color: color.opacity(alpha),
                            glow: 3 * frame.unit, glowAlpha: 0.35), blend: frame.glowBlend)
        }
    }

    // MARK: New peak rays

    static func addPeakRays(_ scene: inout OrbScene, _ frame: OrbFrame) {
        let elapsed = frame.peakAge
        guard frame.effects.celebrate, elapsed >= 0, elapsed < 0.95 else { return }
        let p = elapsed / 0.95
        let gold = frame.dark ? HeatPalette.gold.mixOKLab(HeatPalette.whiteHot, 0.25) : HeatPalette.gold.darkened(0.1)
        if frame.effects.reduceMotion {
            scene.add(.ring(center: frame.center, radius: 0.54 * frame.s, width: 3 * frame.unit, color: gold.opacity(0.45 * sin(.pi * p)),
                            glow: 4 * frame.unit, glowAlpha: 0.4), blend: frame.glowBlend)
            return
        }
        let travel = Easing.easeOut(p)
        let alpha = pow(1 - p, 1.3)
        for index in 0..<24 {
            let long = index.isMultiple(of: 2)
            let angle = Double(index) * 15 + 3.75
            let reach = long ? 0.23 : 0.16
            let head = 0.515 + reach * travel
            let tail = 0.505 + reach * max(0, travel - 0.42) * 1.25
            scene.add(.capsule(
                from: frame.point(angle, tail),
                to: frame.point(angle, head),
                width: (long ? 1.7 : 1.2) * frame.unit,
                color: gold.opacity(alpha),
                tailColor: gold.opacity(alpha * 0.25),
                glow: 2.2 * frame.unit,
                glowAlpha: 0.45
            ), blend: frame.glowBlend)
        }
    }

    // MARK: Source bezel

    /// The colour rim of an orb tracking a subset: equal arcs clockwise from 12 o'clock with 4° gaps, dashed when the
    /// source is unavailable (DESIGN 2b, Identity at a glance).
    static func addBezel(_ scene: inout OrbScene, _ frame: OrbFrame) {
        guard let bezel = frame.content.bezel, !bezel.arcs.isEmpty, frame.content.style != .character else { return }
        let width = frame.content.isThumbnail ? 1 : max(1, 0.0105 * frame.s)
        let radius = frame.s / 2 - width / 2
        let count = bezel.arcs.count
        let gap = count > 1 ? 4.0 : 0
        let span = 360 / Double(count)
        let unlit = frame.tokens.unlitSegment.opacity(2.5)
        for (index, arc) in bezel.arcs.enumerated() {
            let start = -90 + span * Double(index) + gap / 2
            let sweep = (span - gap) * bezel.drawOn
            guard sweep > 0.05 else { continue }
            if arc.available {
                let color = frame.dark ? arc.color.opacity(0.85) : arc.color.darkened(0.1).opacity(0.9)
                scene.add(.bandArc(center: frame.center, radius: radius, width: width, start: start, sweep: sweep,
                                   color: color, startColor: color), blend: .over)
            } else {
                // Dashes of 1.2 w on, 1.4 w off along the arc.
                let circumference = Double(radius) * .pi / 180
                let on = Double(width) * 1.2 / circumference
                let period = Double(width) * 2.6 / circumference
                var angle = start
                while angle < start + sweep {
                    let length = min(on, start + sweep - angle)
                    scene.add(.bandArc(center: frame.center, radius: radius, width: width, start: angle, sweep: length,
                                       color: unlit, startColor: unlit), blend: .over)
                    angle += period
                }
            }
        }
    }

    // MARK: Agent dots

    static func addAgentDots(_ scene: inout OrbScene, _ frame: OrbFrame, layout: OrbReadoutLayout) {
        guard let offset = layout.dots, !frame.content.valueOnly, !frame.unavailable, !frame.isIdle else { return }
        let sources = frame.content.generating
        guard !sources.isEmpty else { return }
        let dots = OrbAgentDotsLayout(count: sources.count, diameter: frame.s, center: frame.center, offset: offset)
        for index in 0..<min(dots.dotCount, sources.count) {
            let source = sources[index]
            let color = frame.dark ? source.tint : source.tint.darkened(0.12)
            let pulse = frame.effects.ambient && !frame.effects.reduceMotion
                ? 0.62 + 0.38 * (0.5 + 0.5 * sin(2 * .pi * (frame.time * 0.9 + Double(index) * 0.19)))
                : 1
            let center = dots.center(of: index)
            if layout.dotHalo {
                let halo = frame.dark ? RGBColor(red: 6 / 255, green: 8 / 255, blue: 12 / 255, alpha: 0.55) : RGBColor.white.opacity(0.75)
                scene.add(.circle(center: center, radius: dots.dotDiameter / 2 + 1.2 * frame.unit, color: halo), blend: .over)
            }
            scene.add(.circle(center: center, radius: dots.dotDiameter / 2, color: color.opacity(pulse),
                              glow: frame.dark ? dots.dotDiameter * 0.7 : 0, glowAlpha: 0.45), blend: .over)
        }
    }
}

/// Positions of the agent dots (max 6, then 5 dots and `+n`).
struct OrbAgentDotsLayout {
    let dotCount: Int
    let overflow: Int
    let dotDiameter: CGFloat
    let spacing: CGFloat
    let y: CGFloat
    let startX: CGFloat
    /// Room reserved for the `+n` label.
    static func overflowWidth(_ diameter: CGFloat) -> CGFloat { 0.075 * diameter }
    static func dotDiameter(_ diameter: CGFloat) -> CGFloat { 4 * diameter / 148 }

    init(count: Int, diameter: CGFloat, center: CGPoint, offset: CGFloat = 0.3785) {
        overflow = count > 6 ? count - 5 : 0
        dotCount = overflow > 0 ? 5 : count
        let unit = diameter / 148
        dotDiameter = Self.dotDiameter(diameter)
        spacing = 3.2 * unit
        y = center.y + offset * diameter
        let total = CGFloat(dotCount) * dotDiameter + CGFloat(max(0, dotCount - 1)) * spacing
            + (overflow > 0 ? spacing + Self.overflowWidth(diameter) : 0)
        startX = center.x - total / 2
    }

    func center(of index: Int) -> CGPoint {
        CGPoint(x: startX + CGFloat(index) * (dotDiameter + spacing) + dotDiameter / 2, y: y)
    }

    /// Leading edge of the `+n` label.
    var overflowX: CGFloat { startX + CGFloat(dotCount) * (dotDiameter + spacing) }
}
