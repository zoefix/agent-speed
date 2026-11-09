import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// `character` — an animated character instead of an instrument (DESIGN 2a.6). Its disc is twice the orb size; the
/// character stands in it with her feet at 0.45 D below the center. The character, the ring of lights at her feet and
/// the readout cards beside her come from the registered `CharacterLayerProvider`; without one (or while it cannot draw) the
/// style draws the ring and the standard readout.
@MainActor
final class CharacterStyle: OrbStyleRenderer {
    let kind = OrbStyleKind.character
    let linePlacement = OrbLinePlacement.character
    let readoutLayout = OrbReadoutLayout(
        valueSize: 0.13, valueBaseline: -0.02, valueMaxWidth: 0.36, unitSize: 0.036, unitGap: 0.055,
        title: .belowUnit(0.048), titleSize: 0.032, titleMaxWidth: 0.34, infoMaxWidth: 0.4, dots: nil, pillCenter: -0.16
    )
    let hasAura = false
    let hasWave = false
    let hasRays = false
    let signOfLifeDuration: TimeInterval = 0

    /// Feet below the disc center, × D.
    static let feet: CGFloat = 0.45
    /// The ring of lights at the feet: horizontal radius × D and its vertical squash.
    static let ringRadius: CGFloat = 0.23
    static let ringSquash: CGFloat = 0.17
    static let ringLights = 96

    private let provider: CharacterLayerProvider?
    private var providerDrew = false
    private var comet = 0.0

    init(provider: CharacterLayerProvider? = CharacterLayers.factory?()) {
        self.provider = provider
    }

    func step(_ frame: OrbFrame) {
        provider?.step(frame, character: frame.content.character)
        guard frame.dt > 0, !frame.effects.reduceMotion else { return }
        comet = (comet + frame.dt / (6.0 - 5.3 * frame.heat)).truncatingRemainder(dividingBy: 1)
    }

    func pulse(at time: TimeInterval, strength: Double, heat: Double) {
        provider?.pulse(at: time, strength: strength, heat: heat)
    }

    func celebrate(at time: TimeInterval) {
        provider?.celebrate(at: time)
    }

    func isBusy(_ frame: OrbFrame) -> Bool {
        provider?.isBusy(frame) ?? false
    }

    /// 30 fps at idle and 60 when active (DESIGN 2a.6); the fallback ring moves only while active.
    func ambientFrameClass(_ frame: OrbFrame) -> OrbFrameClass {
        if let provider { return provider.ambientFrameClass(frame) }
        return frame.isIdle || frame.unavailable || frame.effects.reduceMotion ? .none : .fps30
    }

    func build(into scene: inout OrbScene, _ frame: OrbFrame) {
        providerDrew = provider?.build(into: &scene, frame, character: frame.content.character) ?? false
        guard !providerDrew else { return }
        buildRing(into: &scene, frame)
    }

    func buildReadout(into scene: inout OrbScene, _ frame: OrbFrame, readout: OrbReadoutBuilder) {
        guard !providerDrew else { return }
        readout.add(into: &scene, frame, layout: readoutLayout)
        if let character = frame.content.character, character.pack == nil, !frame.content.isThumbnail {
            addEmptyLabel(character.emptyLabel, into: &scene, frame)
        }
    }

    /// The character's cards carry network speed and 性能 themselves (DESIGN 2a.6, Readouts); only the fallback draws
    /// the two-line readout.
    func buildLines(into scene: inout OrbScene, _ frame: OrbFrame, lines: OrbLineReadout) {
        guard !providerDrew else { return }
        lines.add(into: &scene, frame, placement: linePlacement,
                  palette: .standard(dark: frame.dark, increaseContrast: frame.content.increaseContrast))
    }

    /// `character.empty` above the fallback readout while no character is installed (DESIGN 2a.6).
    private func addEmptyLabel(_ text: String, into scene: inout OrbScene, _ frame: OrbFrame) {
        let style = OrbTextStyle(size: max(7, 0.03 * frame.s), weight: .medium, tracking: 0)
        guard let texture = OrbTextCache.shared.texture(text, style: style, language: frame.content.language,
                                                        scale: scene.backingScale) else { return }
        let baseline = frame.center.y + (readoutLayout.valueBaseline - readoutLayout.valueSize - 0.03) * frame.s
        scene.add(OrbTextDraw(texture, centerX: frame.center.x, baseline: baseline,
                              color: frame.tokens.secondaryText.opacity(frame.alpha)))
    }

    /// The gauge: lights on a flat ellipse at the feet, filled in the heat colors with a bright comet (CHARACTERS 8).
    private func buildRing(into scene: inout OrbScene, _ frame: OrbFrame) {
        let d = frame.s
        let center = CGPoint(x: frame.center.x, y: frame.center.y + Self.feet * d)
        let rx = Self.ringRadius * d, ry = rx * Self.ringSquash
        let heat = frame.unavailable ? 0 : frame.heat
        let lit = heat * Double(Self.ringLights)
        let size = max(0.6, 0.006 * d)
        scene.add(.ellipse(center: center, radii: CGSize(width: rx * 1.15, height: ry * 1.6),
                           color: frame.labelColor(heat).opacity(0.08 + 0.25 * heat), core: 0,
                           glow: 0.4 * rx, glowAlpha: 1), blend: frame.glowBlend)
        for i in 0..<Self.ringLights {
            let t = Double(i) / Double(Self.ringLights)
            let angle = Double.pi / 2 + 2 * Double.pi * t
            let depth = 0.55 + 0.45 * (sin(angle) + 1) / 2
            let point = CGPoint(x: center.x + rx * CGFloat(cos(angle)), y: center.y + ry * CGFloat(sin(angle)))
            let offset = (t - comet + 1.5).truncatingRemainder(dividingBy: 1) - 0.5
            let glint = exp(-pow(offset * 18, 2))
            if Double(i) < lit {
                let color = (i == Int(lit) ? HeatPalette.whiteHot : frame.labelColor(t)).opacity((0.75 + 0.25 * glint) * depth)
                scene.add(.circle(center: point, radius: size * CGFloat(depth), color: color, glow: size * 2, glowAlpha: 0.5),
                          blend: frame.glowBlend)
            } else {
                let color = frame.tokens.unlitSegment.opacity((1.4 + 2 * glint) * depth)
                scene.add(.circle(center: point, radius: size * 0.75 * CGFloat(depth), color: color), blend: .over)
            }
        }
    }
}
