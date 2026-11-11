import AgentSpeedCharacter
import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Metal
import simd

/// Draws one `character` orb (DESIGN 2a.6, CHARACTERS 3–8): the skinned character of the chosen pack, its pose and
/// character swaps, the ring of lights, the agent lights, her particles and the readout cards floating beside her —
/// the speed at her right, the facts in a column at her left, the model above her head, always in front of her.
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
    private var particles = CharacterParticles()
    private var particleDots: [CharacterDot] = []
    private var hudTime = 0.0
    private var loading = false
    private var lastModel: String?
    /// Reused every frame: agent colors, her outline, skinned positions, card sizes and switches.
    private var agents: [(Double, Double, Double)] = []
    private var wings = HudWings()
    private var silhouette: HudSilhouette?
    private var scratch: [Double] = []
    private var cardSizes = [(w: Double, h: Double)](repeating: (0, 0), count: HudCard.allCases.count)
    private var cardShown = [Bool](repeating: false, count: HudCard.allCases.count)
    private var cardDynamics = CharacterCardDynamics()
    /// The speed's digits center below its card's center (the card spans the digits down to the title chip).
    private var speedDrop = 0.0
    /// The last backing scale drawn at, for measuring text in `step`.
    private var backing: CGFloat = 2
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
        let enabled = frame.effects.particles
        if let shown = loaded[stage.current.pack.id] {
            particles.use(shown.pack, hasSprite: shown.petals.contains { $0 != nil })
        }
        for event in stage.current.events where enabled {
            switch event {
            case .petals, .poseSwapped:
                particles.burst(x: g.feet.x + g.bodyDX, y: g.feet.y - 0.55 * g.height, unit: g.unit)
            }
        }
        let side = 2 * Double(frame.center.x)
        // Petals blow across the whole stage; the other kinds start around her (CHARACTERS, Particles).
        let area = particles.isPetals ? (0.05 * side, 0.1 * side, 0.95 * side, 0.85 * side)
            : (g.feet.x - 0.625 * g.height, g.feet.y - 1.083 * g.height, g.feet.x + 0.625 * g.height,
               g.feet.y - 0.042 * g.height)
        particles.update(dt, intensity: enabled ? stage.current.intensity : 0, area: area, unit: g.unit,
                         rate: enabled ? frame.effects.particleRate : 0)

        hudTime += dt
        hud.emberRate = enabled ? frame.effects.particleRate : 0
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
        if let model = character?.modelName { lastModel = model }
        layoutCards(dt, frame, character: character, performer: stage.current, g: g)
        let speed = wings.places[HudCard.speed.rawValue]
        let speedTop = speed.y - wings.sizes[HudCard.speed.rawValue].h / 2
        hud.layout(dt, stage.current.hudLayout(feetOnScreen: (g.feet.x, g.feet.y), pointsPerPixel: g.pointsPerPixel,
                                               unit: g.unit), agents: agents, digitAdvance: advance,
                   peakAt: (speed.x, speedTop - 1.1 * Double(metrics.peak)))
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
        drawParticles(front: false, atlas: activeAtlas, uv: current.petals[stage.current.activePose], frame: frame,
                      g: g, into: &scene)

        scene.layer = Layer.character
        if let outgoing = stage.outgoing, let pack = loaded[outgoing.pack.id] {
            drawCharacter(pack, outgoing, alpha: 1 - stage.outgoingFade, frame: frame, scene: &scene, now: now)
        }
        drawCharacter(current, stage.current, alpha: stage.outgoing == nil ? 1 : stage.outgoingFade, frame: frame,
                      scene: &scene, now: now)
        addDots(hud.front, frame: frame, into: &scene)
        drawParticles(front: true, atlas: activeAtlas, uv: current.petals[stage.current.activePose], frame: frame,
                      g: g, into: &scene)

        backing = scene.backingScale
        scene.layer = Layer.inFront
        drawCards(frame, character: character, into: &scene)
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
            || hud.peakAge != nil || particles.isLively || wings.isFading || cardDynamics.isMoving
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

    /// Her particles on one side: petals sample the atlas, the other kinds are soft dots (glows add up on dark glass and
    /// are laid over on light) or glyphs.
    private func drawParticles(front: Bool, atlas: MTLTexture?, uv: SIMD4<Float>?, frame: OrbFrame, g: Geometry,
                               into scene: inout OrbScene) {
        guard !particles.items.isEmpty else { return }
        guard let kind = particles.kind else {
            if let atlas, let uv {
                scene.addCharacterSprites(particles.sprites(front: front, uv: uv, alpha: frame.alpha), atlas: atlas)
            }
            return
        }
        if kind.look != .glyph {
            particleDots.removeAll(keepingCapacity: true)
            particles.dots(front: front, alpha: frame.alpha, into: &particleDots)
            let blend: OrbBlend = kind.additive && frame.dark ? .additive : .over
            for dot in particleDots { scene.addCharacterDot(dot, blend: blend) }
            return
        }
        let style = OrbTextStyle(size: CGFloat(particles.glyphCeiling(unit: g.unit).rounded(.up)), weight: .bold,
                                 tracking: 0, padding: 1)
        let cap = OrbTextCache.shared.capHeight(style)
        for p in particles.glyphs(front: front) where p.alpha > 0.01 {
            guard let texture = OrbTextCache.shared.texture(p.glyph, style: style, language: frame.content.language,
                                                            scale: scene.backingScale) else { continue }
            let center = CGPoint(x: p.x, y: p.y)
            let color = RGBColor(red: Double(p.color.x), green: Double(p.color.y), blue: Double(p.color.z))
            let draw = OrbTextDraw(texture, x: center.x - texture.advance / 2, baseline: center.y + cap / 2,
                                   color: color.opacity(p.alpha * frame.alpha), scale: CGFloat(p.size) / style.size,
                                   pivot: center)
            scene.add(p.angle == 0 ? draw : draw.rotated(p.angle, about: center))
        }
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

    /// Places the cards for this frame: her outline from the skinned mesh, each card's size and whether it shows
    /// (switched on and loud; at small sizes the column keeps its first two), then the wings' layout.
    private func layoutCards(_ dt: Double, _ frame: OrbFrame, character: OrbCharacterContent?,
                             performer: CharacterPerformer, g: Geometry) {
        let d = Double(frame.s)
        let side = 2 * Double(frame.center.x)
        let step = max(2, (d / 150).rounded())
        if silhouette?.step != step || silhouette?.left.count != Int((side / step).rounded(.up)) {
            silhouette = HudSilhouette(top: 0, bottom: side, step: step)
        }
        let anchors = performer.trace(into: &silhouette!, feetOnScreen: (g.feet.x, g.feet.y),
                                      pointsPerPixel: g.pointsPerPixel, scratch: &scratch)
        let models = HudCard.models.rawValue, quota = HudCard.quota.rawValue
        if !cardShown[models], wings.presence[models] == 0 { cardDynamics.resetModels() }
        if !cardShown[quota], wings.presence[quota] == 0 { cardDynamics.resetQuota() }
        cardDynamics.update(dt, character: character, small: frame.isSmallCharacter,
                            instant: frame.effects.reduceMotion || frame.effects.level == .off)
        // The names narrow to the room the wings found last frame; the wings fit the card's narrowest form.
        let measure = CharacterCards(frame: frame, character: character, backing: backing, dynamics: cardDynamics)
            .modelsMeasure()
        if let measure {
            cardDynamics.fitNames(dt, room: wings.modelsRoom, measure: measure, t: CharacterCardMetrics(disc: frame.s).t,
                                  spare: 0.04 * d, snap: !cardShown[models] || wings.presence[models] == 0)
        }
        wings.modelsNarrowest = measure.map { Double($0.narrowest) } ?? 0
        // 額度 takes the widest form its room allows; the wings spill it only when its narrowest does not fit.
        let quotaWidths = CharacterCards(frame: frame, character: character, backing: backing, dynamics: cardDynamics)
            .quotaWidths()
        cardDynamics.fitQuota(dt, widths: quotaWidths, room: wings.quotaRoom, spare: 0.04 * d,
                              snap: !cardShown[quota] || wings.presence[quota] == 0)
        wings.quotaNarrowest = quotaWidths.min() ?? 0
        let cards = CharacterCards(frame: frame, character: character, backing: backing, dynamics: cardDynamics)
        let model = modelName(frame, character: character)
        let readouts = character?.readouts ?? .default
        let readings = character?.readings
        let speed = speedBox(frame)
        speedDrop = speed.drop
        for card in HudCard.allCases {
            cardSizes[card.rawValue] = card == .speed ? (speed.w, speed.h) : cards.size(card, model: model)
        }
        func has(_ card: HudCard) -> Bool { cardSizes[card.rawValue].w > 0 }
        cardShown[HudCard.speed.rawValue] = true
        cardShown[HudCard.model.rawValue] = readouts.model && has(.model)
        cardShown[HudCard.performance.rawValue] = readings?.performanceLoud == true && has(.performance)
        cardShown[HudCard.network.rawValue] = readouts.network && readings?.networkLoud == true && has(.network)
        cardShown[HudCard.cost.rawValue] = readouts.cost && readings?.costLoud == true && has(.cost)
        cardShown[HudCard.agents.rawValue] = readouts.agents && !frame.isSmallCharacter && has(.agents)
        cardShown[models] = readouts.models && readings?.modelsLoud == true && has(.models)
        // No quiet rule: a quota does not change by the second, it shows whenever it applies.
        cardShown[quota] = readouts.quota && !(character?.quota.isEmpty ?? true) && has(.quota)
        if frame.isSmallCharacter {
            // The column keeps two cards; 額度 is one of them when it shows.
            var room = cardShown[quota] ? 1 : 2
            for card in HudCard.column where card != .quota && cardShown[card.rawValue] {
                if room == 0 { cardShown[card.rawValue] = false } else { room -= 1 }
            }
        }
        wings.still = frame.effects.reduceMotion || frame.effects.level == .off
        wings.balances = !frame.isSmallCharacter
        wings.update(dt, time: hudTime, frame: HudFrame(bounds: (0, 0, side, side), silhouette: silhouette!, footprint: d,
                                                         anchors: anchors),
                     sizes: cardSizes, shown: cardShown)
    }

    /// The model chip's text: the fastest model, the last one shown, else her own name.
    private func modelName(_ frame: OrbFrame, character: OrbCharacterContent?) -> String? {
        character?.modelName ?? lastModel
            ?? character?.pack.map { OrbCharacterSection.name($0.manifest.name, frame.content.language) }
    }

    /// The speed's card: the rolling digits (or the static value) over the unit and the title chip; `drop` is the
    /// digits' center below the card's center.
    private func speedBox(_ frame: OrbFrame) -> (w: Double, h: Double, drop: Double) {
        let m = CharacterReadoutMetrics(disc: frame.s)
        let ink = CharacterInk(frame, contrast: frame.content.increaseContrast)
        let cache = OrbTextCache.shared
        let pad = ink.width(m.value) + 1
        let half = cache.capHeight(m.digitStyle) * 0.62 + pad
        var width = CGFloat(hud.speedWidth) + 2 * pad
        if let text = staticValue(frame) {
            width = (cache.texture(text, style: m.digitStyle, language: frame.content.language, scale: backing)?.advance ?? 0)
                + 2 * pad
        }
        if let unit = cache.texture(frame.content.unit, style: m.unitStyle, language: frame.content.language, scale: backing) {
            width = max(width, unit.advance)
        }
        var bottom = 50 * m.q + cache.capHeight(m.unitStyle)
        let title = frame.content.unavailableTitle ?? frame.content.titleLabel
        if let chip = CharacterBodyPainter.chipHalfSize(title, size: m.title, language: frame.content.language, scale: backing) {
            width = max(width, 2 * chip.width)
            bottom = 86 * m.q + m.title + chip.height
        }
        return (Double(width), Double(bottom + half), Double((bottom - half) / 2))
    }

    private static let drawOrder = HudCard.column + [.models, .model]

    /// The column, the model chip, then the speed over everything.
    private func drawCards(_ frame: OrbFrame, character: OrbCharacterContent?, into scene: inout OrbScene) {
        let cards = CharacterCards(frame: frame, character: character, backing: scene.backingScale, dynamics: cardDynamics)
        let model = modelName(frame, character: character)
        for card in Self.drawOrder {
            cards.draw(card, at: wings.places[card.rawValue], size: wings.sizes[card.rawValue], model: model,
                       right: wings.isAtHerRight(card), into: &scene)
        }
        let place = wings.places[HudCard.speed.rawValue]
        let spin = place.spin
        let drop = speedDrop * place.scale
        let center = CGPoint(x: place.x + drop * sin(spin), y: place.y - drop * cos(spin))
        let painter = CharacterBodyPainter(frame: frame, center: center, scale: CGFloat(place.scale), spin: spin,
                                           alpha: place.alpha * frame.alpha, metrics: CharacterReadoutMetrics(disc: frame.s),
                                           contrast: frame.content.increaseContrast)
        drawSpeed(painter, frame, into: &scene)
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
