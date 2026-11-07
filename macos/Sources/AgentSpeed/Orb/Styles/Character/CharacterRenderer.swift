import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Metal
import simd

/// Draws one `character` orb (DESIGN 2a.6, CHARACTERS 3–8): the skinned character of the chosen pack, its pose and
/// character swaps, the ring of lights, the agent lights, the petals and the readouts orbiting her.
@MainActor
final class CharacterRenderer: CharacterLayerProvider {
    /// The character's height × D and her feet below the disc center × D.
    static let height = 0.86
    static let feet = Double(CharacterStyle.feet)
    /// Depth layers of the scene, back to front.
    enum Layer {
        static let behind = 0
        static let character = 3
        static let inFront = 4
        static let peak = 7
    }

    private var stage: CharacterStage?
    private var loaded: [String: LoadedCharacter] = [:]
    private var hud = CharacterHud(seed: 5, unit: 1)
    private var petals = CharacterPetals()
    private var hudTime = 0.0
    private var loading = false
    private var lastModel: String?
    /// Reused every frame: agent colors, parked bodies, readout bodies back to front.
    private var agents: [(Double, Double, Double)] = []
    private var parked: [HudBody] = []
    private var order = CharacterHud.BodyKind.allCases
    private var lightning = CharacterLightning()
    private var bolt: [SIMD2<Double>] = []
    /// The last pack looked up, so a frame does not hash the pack folder again.
    private var lastPack: (folder: URL, version: Int, loaded: LoadedCharacter)?
    private static var seeds: UInt64 = 7

    // MARK: CharacterLayerProvider

    func step(_ frame: OrbFrame, character: OrbCharacterContent?) {
        guard let installed = character?.pack, !frame.content.isThumbnail else {
            stage = nil
            return
        }
        let pack: LoadedCharacter
        if let cached = lastPack, cached.folder == installed.folder, cached.version == installed.manifest.packVersion {
            pack = cached.loaded
        } else if let loaded = CharacterAssets.shared.pack(installed) {
            pack = loaded
            lastPack = (installed.folder, installed.manifest.packVersion, loaded)
        } else {
            loading = true
            return
        }
        loading = false
        // A resumed or repeated frame still advances a hair, so springs and physics never divide by zero.
        let dt = frame.resumed ? 1.0 / 240 : min(max(frame.dt, 1.0 / 240), 0.1)
        if stage == nil {
            stage = CharacterStage(Self.performer(pack.pack))
            loaded = [pack.pack.id: pack]
        } else if let current = stage?.current, current.pack.id != pack.pack.id, stage?.incoming?.pack.id != pack.pack.id {
            loaded[pack.pack.id] = pack
            stage?.switchTo(Self.performer(pack.pack))
        }
        guard self.stage != nil else { return }
        // Mutated in place: a local copy would make every array of the stage copy itself on write.
        self.stage!.calm = frame.effects.reduceMotion
        self.stage!.amplitude = switch frame.effects.level {
        case .off: 0
        case .soft: 0.5
        default: 1
        }
        self.stage!.step(dt, level: frame.speedLevel)
        guard let stage = self.stage else { return }
        if loaded.count > 1, stage.incoming == nil, stage.outgoing == nil {
            loaded = [stage.current.pack.id: loaded[stage.current.pack.id] ?? pack]
        }

        let g = geometry(frame, stage.current)
        let particles = frame.effects.particles
        for event in stage.current.events where particles {
            switch event {
            case .petals, .poseSwapped:
                petals.burst(x: g.feet.x + g.bodyDX, y: g.feet.y - 0.55 * g.height, unit: g.unit)
            }
        }
        let side = 2 * Double(frame.center.x)
        petals.update(dt, intensity: particles ? stage.current.intensity : 0,
                      area: (0.05 * side, 0.1 * side, 0.95 * side, 0.85 * side), unit: g.unit,
                      rate: frame.effects.particleRate)

        hudTime += dt
        hud.emberRate = particles ? frame.effects.particleRate : 0
        hud.update(dt, time: hudTime, speed: frame.unavailable ? 0 : frame.readoutValue, heat: frame.instrumentHeat,
                   intensity: stage.current.intensity, title: frame.content.unavailableTitle ?? frame.content.titleLabel)
        lightning.update(dt, digits: hud.odometer.digits, heat: frame.instrumentHeat,
                         strength: Self.lightningStrength(frame.effects, small: frame.isSmallCharacter))
        let metrics = CharacterReadoutMetrics(disc: frame.s)
        let advance = OrbTextCache.shared.texture("0", style: metrics.digitStyle, language: frame.content.language,
                                                  scale: 2).map { Double($0.advance) * 0.95 } ?? Double(metrics.value) * 0.6
        agents.removeAll(keepingCapacity: true)
        if !frame.isSmallCharacter, !frame.isIdle {
            for source in frame.content.generating {
                agents.append((source.tint.red * 255, source.tint.green * 255, source.tint.blue * 255))
            }
        }
        if frame.effects.reduceMotion || frame.effects.level == .off {
            Self.park(&parked, g)
            hud.parked = parked
        } else if hud.parked != nil {
            hud.parked = nil
        }
        hud.layout(dt, stage.current.hudLayout(feetOnScreen: (g.feet.x, g.feet.y), pointsPerPixel: g.pointsPerPixel,
                                               unit: g.unit), agents: agents, digitAdvance: advance)
        order.sort { a, b in
            let bodies = hud.bodies
            return bodies.count == 3 ? bodies[a.rawValue].depth < bodies[b.rawValue].depth : a.rawValue < b.rawValue
        }
        if let model = character?.modelName { lastModel = model }
    }

    func build(into scene: inout OrbScene, _ frame: OrbFrame, character: OrbCharacterContent?) -> Bool {
        guard let stage, let current = loaded[stage.current.pack.id] else { return false }
        let now = frame.time
        let assets = CharacterAssets.shared
        defer { assets.sweep(now: now) }
        let g = geometry(frame, stage.current)

        scene.layer = Layer.behind
        addDots(hud.back, frame: frame, into: &scene)
        let activeAtlas = atlas(current, stage.current, pose: stage.current.activePose, g: g, scene: scene, now: now)
        if let atlas = activeAtlas, let uv = current.petals[stage.current.activePose] {
            scene.addCharacterSprites(petals.sprites(front: false, uv: uv, alpha: frame.alpha), atlas: atlas)
        }
        var behindLayer = Layer.behind
        for kind in order where !body(kind, frame, g).inFront {
            scene.layer = behindLayer
            drawBody(kind, frame, character: character, g: g, into: &scene)
            behindLayer = min(behindLayer + 1, Layer.character - 1)
        }

        scene.layer = Layer.character
        if let outgoing = stage.outgoing, let pack = loaded[outgoing.pack.id] {
            drawCharacter(pack, outgoing, alpha: 1 - stage.outgoingFade, frame: frame, scene: &scene, now: now)
        }
        drawCharacter(current, stage.current, alpha: stage.outgoing == nil ? 1 : stage.outgoingFade, frame: frame,
                      scene: &scene, now: now)
        addDots(hud.front, frame: frame, into: &scene)
        if let atlas = activeAtlas, let uv = current.petals[stage.current.activePose] {
            scene.addCharacterSprites(petals.sprites(front: true, uv: uv, alpha: frame.alpha), atlas: atlas)
        }

        var frontLayer = Layer.inFront
        for kind in order where body(kind, frame, g).inFront {
            scene.layer = frontLayer
            drawBody(kind, frame, character: character, g: g, into: &scene)
            frontLayer = min(frontLayer + 1, Layer.peak - 1)
        }
        scene.layer = Layer.peak
        drawPeak(frame, into: &scene)
        scene.layer = 0
        return true
    }

    func ambientFrameClass(_ frame: OrbFrame) -> OrbFrameClass {
        if loading { return .fps30 }
        if frame.effects.level == .off || frame.effects.reduceMotion {
            return isBusy(frame) ? .fps30 : .none
        }
        return Self.frameClass(band: stage?.current.band, acting: isBusy(frame) || stage?.current.isActing == true,
                               unavailable: frame.unavailable)
    }

    /// Calm breathing and swaying need few frames; anything she acts out gets 60 (DESIGN 2a.6, Frame policy).
    nonisolated static func frameClass(band: SpeedBand?, acting: Bool, unavailable: Bool) -> OrbFrameClass {
        if acting { return .fps60 }
        if unavailable { return .fps15 }
        switch band {
        case .idle, nil: return .fps15
        case .cruise: return .fps30
        default: return .fps60
        }
    }

    func isBusy(_ frame: OrbFrame) -> Bool {
        if lightning.isActive { return true }
        guard let stage else { return loading }
        return loading || stage.outgoing != nil || stage.incoming != nil || stage.current.fadingPose != nil
            || hud.peakAge != nil || !petals.items.isEmpty
    }

    func pulse(at time: TimeInterval, strength: Double, heat: Double) {
        hud.pulse(strength: strength)
    }

    func celebrate(at time: TimeInterval) {
        hud.celebratePeak()
    }

    // MARK: Layout

    struct Geometry {
        /// The idle pose's feet on the canvas.
        var feet: (x: Double, y: Double)
        var height: Double
        /// Canvas points per idle pose pixel.
        var pointsPerPixel: Double
        /// Points per reference unit (the reference film draws the character 972 units tall).
        var unit: Double
        var bodyDX: Double
        var d: Double
    }

    private func geometry(_ frame: OrbFrame, _ performer: CharacterPerformer) -> Geometry {
        let d = Double(frame.s)
        let height = Self.height * d
        let ppp = height / max(1, performer.idleHeight)
        return Geometry(feet: (Double(frame.center.x), Double(frame.center.y) + Self.feet * d), height: height,
                        pointsPerPixel: ppp, unit: max(height / 972, 0.42), bodyDX: performer.bodyOffset.dx * ppp, d: d)
    }

    private static func performer(_ pack: CharacterPack) -> CharacterPerformer {
        seeds = seeds &* 6364136223846793005 &+ 1442695040888963407
        return CharacterPerformer(pack: pack, seed: seeds >> 33, orb: 1)
    }

    // MARK: Character

    private func atlas(_ loaded: LoadedCharacter, _ performer: CharacterPerformer, pose: Int, g: Geometry, scene: OrbScene,
                       now: TimeInterval) -> MTLTexture? {
        let placement = performer.placements[pose]
        let needed = CharacterAtlasScale.needed(pointsPerPixel: g.pointsPerPixel * placement.scale,
                                                backingScale: Double(scene.backingScale))
        return CharacterAssets.shared.atlas(loaded.pack.poses[pose], scale: needed, now: now)
    }

    private func drawCharacter(_ loaded: LoadedCharacter, _ performer: CharacterPerformer, alpha: Double, frame: OrbFrame,
                               scene: inout OrbScene, now: TimeInterval) {
        let g = geometry(frame, performer)
        // Keep the pose the band asks for resident too, so a swap finds its atlas ready.
        if let band = performer.band {
            let wanted = performer.poseIndex(for: band)
            if wanted != performer.activePose { _ = atlas(loaded, performer, pose: wanted, g: g, scene: scene, now: now) }
        }
        if let fading = performer.fadingPose {
            drawPose(loaded, performer, pose: fading, world: performer.fadingWorld, opacities: performer.fadingOpacities,
                     alpha: alpha * (1 - performer.fadeProgress), g: g, frame: frame, scene: &scene, now: now)
        }
        let fadeIn = performer.fadingPose == nil ? 1 : performer.fadeProgress
        drawPose(loaded, performer, pose: performer.activePose, world: performer.world, opacities: performer.opacities,
                 alpha: alpha * fadeIn, g: g, frame: frame, scene: &scene, now: now)
    }

    private func drawPose(_ loaded: LoadedCharacter, _ performer: CharacterPerformer, pose: Int, world: [Affine2],
                          opacities: [Double], alpha: Double, g: Geometry, frame: OrbFrame, scene: inout OrbScene,
                          now: TimeInterval) {
        guard alpha > 0.002, pose < loaded.poses.count, let gpu = loaded.poses[pose], !world.isEmpty,
              let atlas = atlas(loaded, performer, pose: pose, g: g, scene: scene, now: now) else { return }
        let placement = performer.placements[pose]
        scene.addCharacterMesh(gpu, atlas: atlas, world: world, opacities: opacities,
                               placement: SIMD4(Float(g.feet.x), Float(g.feet.y), Float(g.pointsPerPixel * placement.scale), 1),
                               feet: SIMD2(Float(placement.feet.x), Float(placement.feet.y)), alpha: Float(alpha))
    }

    /// The readout's lights: additive on dark glass, laid over on light; unlit (white) ring lights take the unlit token's
    /// ink on light, doubled with Increase Contrast (DESIGN 2a.6).
    private func addDots(_ dots: [HudDot], frame: OrbFrame, into scene: inout OrbScene) {
        let dark = frame.dark
        let contrast: Float = frame.content.increaseContrast ? 2 : 1
        for dot in dots {
            var light = CharacterDot(dot)
            if dot.r == 1, dot.g == 1, dot.b == 1, dot.softness < 0.99 {
                if !dark { light.color = SIMD4(0, 0, 0, dot.alpha * 0.55) }
                light.color.w = min(1, light.color.w * contrast)
            }
            scene.addCharacterDot(light, blend: dot.additive && dark ? .additive : .over)
        }
    }

    // MARK: Readouts

    private func body(_ kind: CharacterHud.BodyKind, _ frame: OrbFrame, _ g: Geometry) -> HudBody {
        hud.bodies.indices.contains(kind.rawValue) ? hud.bodies[kind.rawValue] : HudBody(x: g.feet.x, y: g.feet.y, depth: 1,
                                                                                          scale: 1, spin: 0, alpha: 0)
    }

    /// Reduce Motion and effects Off park the bodies around her (DESIGN 2a.6): the speed at her right shoulder, the
    /// model above her head, the agents at her left knee.
    static func park(_ bodies: inout [HudBody], _ g: Geometry) {
        let x = g.feet.x, y = g.feet.y, h = g.height
        bodies.removeAll(keepingCapacity: true)
        bodies.append(HudBody(x: x + 0.36 * h, y: y - 0.72 * h, depth: 1, scale: 1, spin: 0, alpha: 1))
        bodies.append(HudBody(x: x, y: y - 1.05 * h, depth: 1, scale: 1, spin: 0, alpha: 1))
        bodies.append(HudBody(x: x - 0.34 * h, y: y - 0.25 * h, depth: 1, scale: 1, spin: 0, alpha: 1))
    }

    private func drawBody(_ kind: CharacterHud.BodyKind, _ frame: OrbFrame, character: OrbCharacterContent?, g: Geometry,
                          into scene: inout OrbScene) {
        let b = body(kind, frame, g)
        let metrics = CharacterReadoutMetrics(disc: frame.s)
        let contrast = frame.content.increaseContrast
        let painter = CharacterBodyPainter(frame: frame, center: CGPoint(x: b.x, y: b.y), scale: CGFloat(b.scale),
                                           spin: b.spin, alpha: b.alpha * frame.alpha, metrics: metrics, contrast: contrast)
        let rim = frame.dark ? RGBColor(red: 1, green: 1, blue: 1).opacity(0.16) : RGBColor(red: 0, green: 0, blue: 0).opacity(0.1)
        switch kind {
        case .speed:
            drawSpeed(painter, frame, into: &scene)
        case .model:
            let name = character?.modelName ?? lastModel
                ?? character?.pack.map { OrbCharacterSection.name($0.manifest.name, frame.content.language) }
            guard let name else { return }
            let accent = character?.accent ?? RGBColor(red: 0.85, green: 0.47, blue: 0.34)
            _ = painter.chip(name, size: metrics.model, at: 0, text: frame.tokens.valueText, rim: rim, dot: accent, into: &scene)
        case .info:
            guard !frame.isSmallCharacter, let line = character?.agentLine else { return }
            _ = painter.chip(line, size: metrics.info, at: 0, text: frame.tokens.secondaryText, rim: rim, into: &scene)
        }
    }

    nonisolated static func lightningStrength(_ effects: OrbEffects, small: Bool) -> CharacterLightning.Strength {
        if effects.level == .off { return .off }
        if effects.reduceMotion { return .flashOnly }
        switch effects.level {
        case .soft: return .soft
        case .max: return small ? .standard : .max
        default: return small ? .soft : .standard
        }
    }

    /// The bolts of every live strike, as glowing three-stroke polylines under the digits.
    private func drawLightning(_ p: CharacterBodyPainter, _ frame: OrbFrame, digitCount: Int, advance: CGFloat, cap: CGFloat,
                               into scene: inout OrbScene) {
        guard lightning.isActive else { return }
        let strength = Self.lightningStrength(frame.effects, small: frame.isSmallCharacter)
        let ink = CharacterInk(frame, contrast: p.contrast)
        let value = p.metrics.value
        let heat = frame.labelColor(frame.instrumentHeat)
        let colors = ink.bolt(heat: heat)
        for strike in lightning.strikes {
            let envelope = lightning.envelope(strike) * strike.power * p.alpha
            guard envelope > 0.01 else { continue }
            var index = 0
            while lightning.bolt(strike, index: index, strength: strength, digitCount: digitCount, advance: Double(advance),
                                 cap: Double(cap)) {
                bolt.removeAll(keepingCapacity: true)
                for point in lightning.path {
                    let c = p.point(CGFloat(point.x), CGFloat(point.y))
                    bolt.append(SIMD2(Double(c.x), Double(c.y)))
                }
                scene.addPolyline(bolt, origin: .zero, glowColor: colors.glow, coreColor: colors.core,
                                  outer: OrbStroke(width: 0.22 * value * p.scale, alpha: 0.3 * envelope),
                                  mid: OrbStroke(width: 0.09 * value * p.scale, alpha: 0.6 * envelope),
                                  core: OrbStroke(width: 0.04 * value * p.scale, alpha: envelope), blend: frame.glowBlend)
                index += 1
            }
        }
    }

    private func drawSpeed(_ p: CharacterBodyPainter, _ frame: OrbFrame, into scene: inout OrbScene) {
        let m = p.metrics
        let q = m.q
        let width = CGFloat(hud.speedWidth)
        let heat = frame.instrumentHeat
        let glow = frame.labelColor(heat)
        let glowAlpha = (0.12 + 0.5 * max(0, heat - 0.3)) * p.alpha
        scene.addCharacterDot(CharacterDot(x: Float(p.center.x), y: Float(p.center.y),
                                           rx: Float((width * 0.75 + 30 * q) * p.scale), ry: Float(50 * q * p.scale),
                                           color: SIMD4(Float(glow.red), Float(glow.green), Float(glow.blue), Float(glowAlpha)),
                                           softness: 1), blend: frame.glowBlend)

        let style = m.digitStyle
        let cap = OrbTextCache.shared.capHeight(style)
        let ink = CharacterInk(frame, contrast: p.contrast)
        let valueColor = frame.isIdle || frame.unavailable ? frame.tokens.idleValueText : frame.tokens.valueText
        if let text = staticValue(frame) {
            if let texture = OrbTextCache.shared.texture(text, style: style, language: p.language, scale: scene.backingScale) {
                scene.add(p.text(texture, centerX: 0, baseline: cap / 2, color: valueColor.opacity(p.alpha),
                                 glow: ink.shadow.opacity(ink.shadowAlpha * p.alpha), glowRadius: 0.1 * m.value)
                    .outlined(ink.outline.opacity(ink.outline.alpha * p.alpha), width: ink.width(m.value) * p.scale))
            }
        } else {
            let digits = hud.odometer.digits
            let advance = digits.isEmpty ? 0 : width / CGFloat(digits.count)
            drawLightning(p, frame, digitCount: digits.count, advance: advance, cap: cap, into: &scene)
            // The window holds one digit and its outline; a rolling digit travels a full window, so the incoming one
            // starts out of sight below and the outgoing one ends out of sight above.
            let pad = ink.width(m.value) + 1
            let half = cap * 0.62 + pad
            let line = 2 * half
            let clip = OrbTextClip.rect(CGRect(x: p.center.x - (width / 2 + pad) * p.scale, y: p.center.y - half * p.scale,
                                               width: (width + 2 * pad) * p.scale, height: line * p.scale))
            for (i, digit) in digits.enumerated() {
                let ox = -width / 2 + advance * (CGFloat(i) + 0.5)
                let ease = 1 - pow(1 - digit.t, 3)
                let flash = lightning.flash(fromRight: digits.count - 1 - i)
                let halo = ink.halo(flash: flash, heat: glow)
                // The old digit slides up and out as the new one slides in from below, one line apart, so the two never
                // overlap.
                func roll(_ ch: Character?, _ dy: Double, _ a: Double) {
                    guard a > 0.01, let ch, let texture = OrbTextCache.shared.texture(ch, style: style, language: p.language,
                                                                                     scale: scene.backingScale) else { return }
                    scene.add(p.text(texture, centerX: ox, baseline: cap / 2 + CGFloat(dy) * line,
                                     color: valueColor.opacity(a * p.alpha), glow: halo.color.opacity(halo.alpha * a * p.alpha),
                                     glowRadius: halo.radius * m.value, clip: clip)
                        .outlined(ink.outline.opacity(ink.outline.alpha * a * p.alpha), width: ink.width(m.value) * p.scale))
                }
                roll(digit.previous, -ease, 1 - ease)
                roll(digit.current, 1 - ease, ease)
            }
        }
        if let unit = OrbTextCache.shared.texture(frame.content.unit, style: m.unitStyle, language: p.language,
                                                  scale: scene.backingScale * max(1, p.scale)) {
            let unitCap = OrbTextCache.shared.capHeight(m.unitStyle)
            scene.add(p.text(unit, centerX: 0, baseline: 50 * q + unitCap, color: frame.tokens.secondaryText.opacity(p.alpha))
                .outlined(ink.outline.opacity(ink.outline.alpha * 0.85 * p.alpha), width: ink.width(m.unit) * p.scale))
        }
        let titleColor = frame.labelColor(heat)
        let e = 1 - pow(1 - hud.titleProgress, 3)
        func titleChip(_ title: String?, incoming: Bool) {
            guard let title else { return }
            let a = incoming ? max(0, (e - 0.3) / 0.7) : max(0, 1 - 2.5 * e)
            guard a > 0.01 else { return }
            let slide = incoming ? (1 - e) * 14 * q : -e * 14 * q
            let fade = CharacterBodyPainter(frame: frame, center: p.center, scale: p.scale, spin: p.spin,
                                            alpha: p.alpha * a, metrics: m, contrast: p.contrast)
            _ = fade.chip(title, size: m.title, at: 86 * q + m.title + slide, text: titleColor, rim: titleColor.opacity(0.35),
                          into: &scene)
        }
        titleChip(hud.previousTitle, incoming: false)
        titleChip(hud.title, incoming: true)
    }

    /// The value when it does not roll: `–` while unavailable, the money readout in the money unit.
    private func staticValue(_ frame: OrbFrame) -> String? {
        if frame.unavailable { return "–" }
        return frame.content.money?.number
    }

    private func drawPeak(_ frame: OrbFrame, into scene: inout OrbScene) {
        guard frame.effects.celebrate, let peak = hud.peak else { return }
        let side = 2 * frame.center.x
        let metrics = CharacterReadoutMetrics(disc: frame.s)
        let x = min(max(CGFloat(peak.x), 0.2 * side), 0.8 * side)
        let gold = RGBColor(red: 1, green: 200 / 255, blue: 80 / 255)
        let painter = CharacterBodyPainter(frame: frame, center: CGPoint(x: x, y: peak.y), scale: CGFloat(peak.scale), spin: 0,
                                           alpha: peak.alpha * frame.alpha, metrics: metrics, contrast: frame.content.increaseContrast)
        _ = painter.chip(frame.content.peakLabel, size: metrics.peak, at: 0, text: frame.dark ? gold : gold.darkened(0.35),
                         rim: gold.opacity(0.5), into: &scene)
    }
}

extension OrbFrame {
    /// Below S = 96 (D = 192) the agent lights and the agents body are not drawn (DESIGN 2a.6, LOD).
    var isSmallCharacter: Bool { s < 192 }
}
