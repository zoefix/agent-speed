import AgentSpeedCore
import SwiftUI

/// Builds one frame of an orb (DESIGN 2, 2a) into a reused scene: the shared layers around the style's instrument,
/// back to front, with both styles at their crossfade alphas during a style change.
@MainActor
enum OrbSceneBuilder {
    static func build(
        into scene: inout OrbScene,
        content: OrbContent,
        animator: OrbAnimator,
        model: AppModel,
        feed: OrbFeed,
        canvasSide: CGFloat,
        scale: CGFloat,
        time: TimeInterval,
        contentScale: CGFloat = 1
    ) {
        let gauge = feed.gauge
        let effects = animator.effects(for: model)
        let unavailable = content.unavailableTitle != nil
        let heat = gauge.heat(at: time)
        let overdrive = gauge.overdrive(at: time)
        let tier = unavailable ? SpeedTier.idle : gauge.tier
        let dt = animator.stepShared(to: time, input: .init(
            heat: heat, overdrive: overdrive, tier: tier, effects: effects, dark: content.tokens.isDark,
            hovered: animator.hoverTarget, unavailable: unavailable
        ))

        let sinceChange = time - gauge.tierChangedAt
        let flash = effects.flash && gauge.tierChangedAt.isFinite && sinceChange < 0.4 ? 1 - Easing.easeOut(sinceChange / 0.4) : 0
        let geometry = animator.geometry(diameter: content.diameter, side: canvasSide)
        let peakAge = time - animator.peakStart
        let mix = animator.styleMix(at: time)

        scene.reset()
        scene.center = geometry.center
        scene.backingScale = scale
        scene.contentScale = contentScale
        if animator.hero != nil {
            HeroCenter.build(into: &scene, animator: animator, model: model, content: content, geometry: geometry, time: time,
                             sweepEpoch: animator.idleEpoch(for: gauge))
        }

        let readout = animator.readout
        let resumed = animator.isResuming
        if content.showsReadout {
            if let money = content.money {
                if resumed {
                    readout.reset(text: money.number, value: money.value)
                } else {
                    readout.update(text: money.number, value: money.value, at: time)
                }
            } else {
                let value = gauge.readoutValue
                if resumed {
                    readout.reset(text: SpeedFormat.speed(value), value: value)
                } else {
                    readout.update(value: value, at: time)
                }
            }
            readout.valueGlowStrength = animator.valueGlow.value
            readout.leanAmount = animator.skew.value
            readout.hoverAmount = animator.hover.value
        }

        func frame(alpha: Double) -> OrbFrame {
            var frame = OrbFrame(
                time: time,
                dt: dt,
                geometry: geometry,
                content: content,
                effects: effects,
                heat: heat,
                overdrive: overdrive,
                rawHeat: HeatPalette.heat(value: gauge.rawValue, redline: gauge.redline),
                rawOverdrive: HeatPalette.overdrive(value: gauge.rawValue, redline: gauge.redline),
                displayHeat: heat * (effects.celebrate ? animator.peakSweep(at: time) : 1),
                tier: tier,
                redline: gauge.redline,
                tierChangedAt: gauge.tierChangedAt,
                titleChangedAt: gauge.titleChangedAt,
                flash: unavailable ? 0 : flash,
                breath: effects.breathing ? 1 + 0.15 * sin(2 * .pi * animator.breathPhase) : 1,
                breathPhase: animator.breathPhase,
                redlineAmount: animator.redline.value,
                activity: animator.activity.value,
                signOfLife: animator.signOfLife(at: time, gauge: gauge, effects: effects, unavailable: unavailable),
                peakAge: peakAge,
                alpha: alpha,
                resumed: resumed
            )
            frame.speedLevel = unavailable ? 0 : gauge.title.rawValue
            frame.readoutValue = gauge.readoutValue
            return frame
        }

        if let outgoing = animator.outgoing, mix < 1 {
            draw(outgoing, frame(alpha: 1 - mix), into: &scene, animator: animator)
        }
        draw(animator.renderer, frame(alpha: animator.outgoing == nil ? 1 : mix), into: &scene, animator: animator)
        animator.finishCrossfadeIfDone(at: time)
        scene.alphaScale = 1
    }

    private static func draw(_ renderer: OrbStyleRenderer, _ frame: OrbFrame, into scene: inout OrbScene, animator: OrbAnimator) {
        var frame = frame
        if frame.content.showsReadout, !frame.content.isThumbnail, !frame.content.valueOnly {
            frame.readoutShift = animator.lines.stackShift(frame, placement: renderer.linePlacement,
                                                             layout: renderer.readoutLayout)
        }
        frame.auraFlare = animator.flare.value
        renderer.step(frame)
        scene.alphaScale = Float(frame.alpha)
        guard frame.alpha > 0.002 else { return }
        let thumbnail = frame.content.valueOnly
        if renderer.hasAura, !thumbnail { OrbSharedLayers.addAura(&scene, frame, flare: animator.flare.value) }
        if renderer.hasWave, !thumbnail { OrbSharedLayers.addPulses(&scene, frame, pulses: animator.pulses) }
        if renderer.hasRays, !thumbnail { OrbSharedLayers.addPeakRays(&scene, frame) }
        renderer.build(into: &scene, frame)
        OrbSharedLayers.addBezel(&scene, frame)
        if frame.content.showsReadout {
            OrbSharedLayers.addAgentDots(&scene, frame, layout: renderer.readoutLayout)
            renderer.buildReadout(into: &scene, frame, readout: animator.readout)
            if frame.content.lines?.reading != nil, !frame.content.isThumbnail, !thumbnail {
                renderer.buildLines(into: &scene, frame, lines: animator.lines)
            }
        } else {
            OrbSharedLayers.addAgentDots(&scene, frame, layout: renderer.readoutLayout)
        }
    }
}
