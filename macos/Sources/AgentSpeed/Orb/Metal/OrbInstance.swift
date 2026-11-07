import AgentSpeedCharacter
import AgentSpeedHero
import AgentSpeedOrbModel
import CoreGraphics
import Metal
import simd

/// One instanced shape for the orb shaders (layout matches `Instance` in `OrbShaderSource`).
///
/// Angles taken by the builders are degrees clockwise from 3 o'clock in screen space (y down); glows are given as the
/// Gaussian falloff radius of the shader (`OrbInstance.glow(sigma:)` converts a spec σ).
struct OrbInstance {
    enum Shape: Float {
        case roundedRect = 0
        case circle = 1
        case ring = 2
        case capsule = 3
        case radial = 4
        case arc = 5
        case bandArc = 6
        case ellipse = 7
        case blade = 8
        case polyline = 9
        case flare = 10
        case perimeter = 11
    }

    enum Fill: Float {
        case solid = 0
        case linear = 1
        case radial = 2
        case palette = 3
    }

    var a: SIMD4<Float>
    var b: SIMD4<Float>
    var color: SIMD4<Float>
    var color2: SIMD4<Float>
    var extra: SIMD4<Float>
    var extra2: SIMD4<Float>

    private init(shape: Shape, center: CGPoint, rotation: Double = 0, size: SIMD2<Float>, corner: Float = 0,
                 glow: CGFloat, color: RGBColor, tail: RGBColor? = nil, core: Double, glowAlpha: Double,
                 p0: Float = 0, p1: Float = 0, extra2: SIMD4<Float> = SIMD4(0, 0, 0, 1)) {
        a = SIMD4(Float(center.x), Float(center.y), Float(rotation), shape.rawValue)
        b = SIMD4(size.x, size.y, corner, Float(glow))
        self.color = color.simd
        color2 = (tail ?? color).simd
        extra = SIMD4(Float(core), Float(glowAlpha), p0, p1)
        self.extra2 = extra2
    }

    /// Shader glow radius for a Gaussian glow of standard deviation `sigma` (a canvas `shadowBlur` of 2σ).
    static func glow(sigma: CGFloat) -> CGFloat { sigma * 1.414 }

    /// The same shape with an exponential glow `exp(−d / glow)` instead of the Gaussian (a negative glow radius).
    func exponentialGlow() -> OrbInstance {
        var copy = self
        copy.b.w = -abs(copy.b.w)
        return copy
    }

    static func roundedRect(center: CGPoint, halfSize: CGSize, rotation: Double, corner: CGFloat, color: RGBColor,
                            core: Double = 1, glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .roundedRect, center: center, rotation: rotation,
                    size: SIMD2(Float(halfSize.width), Float(halfSize.height)), corner: Float(corner),
                    glow: glow, color: color, core: core, glowAlpha: glowAlpha)
    }

    static func circle(center: CGPoint, radius: CGFloat, color: RGBColor, core: Double = 1, glow: CGFloat = 0,
                       glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .circle, center: center, size: SIMD2(Float(radius), 0), glow: glow, color: color,
                    core: core, glowAlpha: glowAlpha)
    }

    /// A soft disc: `color` in full up to `inner`, falling to zero at `outer` with a smoothstep.
    static func softDisc(center: CGPoint, inner: CGFloat, outer: CGFloat, color: RGBColor) -> OrbInstance {
        OrbInstance(shape: .circle, center: center, size: SIMD2(Float(inner), 0), corner: Float(max(outer, inner + 0.01)),
                    glow: 0, color: color, core: 1, glowAlpha: 0)
    }

    static func ring(center: CGPoint, radius: CGFloat, width: CGFloat, color: RGBColor, core: Double = 1,
                     glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .ring, center: center, size: SIMD2(Float(radius), Float(width)), glow: glow,
                    color: color, core: core, glowAlpha: glowAlpha)
    }

    /// A round-capped line from `tail` to `head`, colored `tailColor` → `color`.
    static func capsule(from tail: CGPoint, to head: CGPoint, width: CGFloat, color: RGBColor, tailColor: RGBColor? = nil,
                        core: Double = 1, glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        let dx = head.x - tail.x, dy = head.y - tail.y
        let length = max(0.001, sqrt(dx * dx + dy * dy))
        let center = CGPoint(x: (head.x + tail.x) / 2, y: (head.y + tail.y) / 2)
        return OrbInstance(shape: .capsule, center: center, rotation: atan2(dy, dx),
                           size: SIMD2(Float(length), Float(width)), glow: glow, color: color, tail: tailColor,
                           core: core, glowAlpha: glowAlpha)
    }

    /// A soft radial halo between two radii.
    static func radial(center: CGPoint, inner: CGFloat, outer: CGFloat, color: RGBColor, alpha: Double) -> OrbInstance {
        OrbInstance(shape: .radial, center: center, size: .zero, glow: 0, color: color, core: alpha, glowAlpha: 0,
                    p0: Float(inner), p1: Float(outer))
    }

    /// A tapered arc from `start` over `sweep` (degrees, clockwise on screen), brightest at the head.
    static func arc(center: CGPoint, radius: CGFloat, width: CGFloat, start: Double, sweep: Double, color: RGBColor,
                    tailColor: RGBColor, glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .arc, center: center, size: SIMD2(Float(radius), Float(width)), glow: glow, color: color,
                    tail: tailColor, core: 1, glowAlpha: glowAlpha,
                    p0: Float(start * .pi / 180), p1: Float(sweep * .pi / 180))
    }

    /// A constant-width arc from `start` over `sweep` degrees (clockwise, 0...360), colored `startColor` → `color`
    /// with its alpha rising from `startAlpha` × to 1 × at the end; butt caps unless `roundCaps`.
    static func bandArc(center: CGPoint, radius: CGFloat, width: CGFloat, start: Double, sweep: Double, color: RGBColor,
                        startColor: RGBColor? = nil, startAlpha: Double = 1, roundCaps: Bool = false, core: Double = 1,
                        glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .bandArc, center: center, size: SIMD2(Float(radius), Float(width)), corner: roundCaps ? 1 : 0,
                    glow: glow, color: color, tail: startColor, core: core, glowAlpha: glowAlpha,
                    p0: Float(start * .pi / 180), p1: Float(max(0.0001, min(360, sweep)) * .pi / 180),
                    extra2: SIMD4(Fill.solid.rawValue, 0, 0, Float(startAlpha)))
    }

    /// A band arc whose color follows the heat palette from `heatFrom` at the start to `heatTo` at the end (the
    /// spectrum of a dial); `alpha` scales it and `startAlpha` ramps it like `bandArc`.
    static func paletteArc(center: CGPoint, radius: CGFloat, width: CGFloat, start: Double, sweep: Double,
                           heatFrom: Double, heatTo: Double, alpha: Double, startAlpha: Double = 1, roundCaps: Bool = false,
                           core: Double = 1, glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .bandArc, center: center, size: SIMD2(Float(radius), Float(width)), corner: roundCaps ? 1 : 0,
                    glow: glow, color: RGBColor.white.opacity(alpha), core: core, glowAlpha: glowAlpha,
                    p0: Float(start * .pi / 180), p1: Float(max(0.0001, min(360, sweep)) * .pi / 180),
                    extra2: SIMD4(Fill.palette.rawValue, Float(heatFrom), Float(heatTo), Float(startAlpha)))
    }

    /// An ellipse with radii `radii`, rotated by `rotation` degrees.
    static func ellipse(center: CGPoint, radii: CGSize, rotation: Double = 0, color: RGBColor, core: Double = 1,
                        glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .ellipse, center: center, rotation: rotation * .pi / 180,
                    size: SIMD2(Float(radii.width), Float(radii.height)), glow: glow, color: color, core: core,
                    glowAlpha: glowAlpha)
    }

    /// The needle: pivot at `pivot`, pointing at `angle` degrees, a tapered blade to a round tip at `tipLength`
    /// (half widths `pivotHalfWidth` → `tipHalfWidth`) and a counterweight `tailLength` behind the pivot.
    static func blade(pivot: CGPoint, angle: Double, tipLength: CGFloat, tailLength: CGFloat, pivotHalfWidth: CGFloat,
                      tipHalfWidth: CGFloat, tailHalfWidth: CGFloat, color: RGBColor, core: Double = 1,
                      glow: CGFloat = 0, glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .blade, center: pivot, rotation: angle * .pi / 180,
                    size: SIMD2(Float(tipLength), Float(tailLength)), corner: Float(tailHalfWidth), glow: glow,
                    color: color, core: core, glowAlpha: glowAlpha, p0: Float(pivotHalfWidth), p1: Float(tipHalfWidth))
    }

    /// A four-pointed star of two crossed exponential lines (the `led` tip flare): along `rotation` (radians)
    /// `exp(−|t| / tangential) · exp(−|n| / radialWidth)`, across it `exp(−|n| / radial) · exp(−|t| / tangentialWidth)`;
    /// `color.alpha` scales the sum (clamped to 1).
    static func flare(center: CGPoint, rotation: Double, tangential: CGFloat, radial: CGFloat, radialWidth: CGFloat,
                      tangentialWidth: CGFloat, color: RGBColor) -> OrbInstance {
        OrbInstance(shape: .flare, center: center, rotation: rotation, size: SIMD2(Float(tangential), Float(radial)),
                    glow: 0, color: color, core: 0, glowAlpha: 0, p0: Float(radialWidth), p1: Float(tangentialWidth))
    }

    /// A stroke along the outline of a rounded rectangle (the board's perimeter, DESIGN 2c): the whole outline when
    /// `length` is 0, else the `length` points behind `head` (the distance along the outline, clockwise on screen from
    /// the end of the top-left corner, as `BoardPerimeter`) with round caps. Along the segment the color runs
    /// `tailColor` → `color`, the width `tailWidth` × → 1 × and the alpha `tailAlpha` × → 1 × (raised to `alphaCurve`).
    static func perimeter(center: CGPoint, halfSize: CGSize, corner: CGFloat, width: CGFloat, head: CGFloat = 0,
                          length: CGFloat = 0, color: RGBColor, tailColor: RGBColor? = nil, tailWidth: Double = 1,
                          tailAlpha: Double = 1, alphaCurve: Double = 1, core: Double = 1, glow: CGFloat = 0,
                          glowAlpha: Double = 0) -> OrbInstance {
        OrbInstance(shape: .perimeter, center: center, rotation: Double(head),
                    size: SIMD2(Float(halfSize.width), Float(halfSize.height)), corner: Float(corner), glow: glow,
                    color: color, tail: tailColor, core: core, glowAlpha: glowAlpha, p0: Float(width), p1: Float(length),
                    extra2: SIMD4(0, Float(tailWidth), Float(tailAlpha), Float(alphaCurve)))
    }

    /// Light outside a rounded rectangle only (a CSS `box-shadow` bloom): `spread` points of full light, then the
    /// edge of a Gaussian blur of standard deviation `sigma`, times (1 − (d / reach)²)² of the distance d from the
    /// rectangle; nothing inside it. It fades out along the rounded outline's offsets and ends `reach` points out.
    static func outsideBloom(center: CGPoint, halfSize: CGSize, corner: CGFloat, spread: CGFloat, sigma: CGFloat,
                             reach: CGFloat, color: RGBColor) -> OrbInstance {
        OrbInstance(shape: .perimeter, center: center, size: SIMD2(Float(halfSize.width), Float(halfSize.height)),
                    corner: Float(corner), glow: sigma, color: color, core: 0, glowAlpha: 1, p0: Float(spread),
                    p1: Float(reach), extra2: SIMD4(1, 1, 1, 1))
    }

    /// A polyline of `count` points starting at `first` in the scene's point buffer, drawn as three round-joined strokes
    /// of one path (the prototype's canvas strokes): `outer` and `mid` in `glowColor`, `core` in `coreColor`. `sum`
    /// adds the strokes (dark glass), otherwise they are laid over each other in order.
    static func polyline(first: Int, count: Int, bounds: CGRect, glowColor: RGBColor, coreColor: RGBColor,
                         outer: OrbStroke, mid: OrbStroke, core: OrbStroke, sum: Bool) -> OrbInstance {
        var instance = OrbInstance(
            shape: .polyline, center: CGPoint(x: bounds.midX, y: bounds.midY), rotation: Double(first),
            size: SIMD2(Float(bounds.width / 2), Float(bounds.height / 2)), corner: Float(count), glow: 0,
            color: glowColor.opacity(outer.alpha), tail: coreColor.opacity(core.alpha), core: 0, glowAlpha: 0,
            extra2: SIMD4(Fill.solid.rawValue, sum ? 1 : 0, 0, 0)
        )
        instance.extra = SIMD4(Float(outer.width), Float(mid.width), Float(core.width), Float(mid.alpha.clamped(to: 0...1)))
        return instance
    }

    // MARK: Fills (circle, ring, rounded rect, ellipse, blade)

    /// Linear gradient from `color` to `end` along `angle` degrees, over `halfLength` points either side of the center.
    func linearGradient(to end: RGBColor, angle: Double = 90, halfLength: CGFloat) -> OrbInstance {
        var copy = self
        copy.color2 = end.simd
        copy.extra2 = SIMD4(Fill.linear.rawValue, Float(angle * .pi / 180), Float(halfLength), 1)
        return copy
    }

    /// Radial gradient from `color` at `offset` (from the shape center) to `end` at `radius`.
    func radialGradient(to end: RGBColor, offset: CGPoint = .zero, radius: CGFloat) -> OrbInstance {
        var copy = self
        copy.color2 = end.simd
        copy.extra2 = SIMD4(Fill.radial.rawValue, Float(offset.x), Float(offset.y), Float(radius))
        return copy
    }

    /// Multiplies every alpha (crossfades).
    mutating func scaleAlpha(_ factor: Float) {
        color.w *= factor
        color2.w *= factor
        if a.w == Shape.polyline.rawValue { extra.w *= factor }
    }
}

/// One stroke of a polyline: width in points and alpha.
struct OrbStroke {
    var width: CGFloat
    var alpha: Double
}

extension RGBColor {
    var simd: SIMD4<Float> { SIMD4(Float(red), Float(green), Float(blue), Float(alpha.clamped(to: 0...1))) }
}

/// The dashboard hero's vehicle layer for one frame: canvas unit (0, 0) at `origin`, `unit` points per canvas unit.
struct OrbVehicleLayer {
    let list: HeroDrawList
    let origin: CGPoint
    let unit: CGFloat
    /// Opacity of the whole layer (the vehicle ↔ title badge crossfade, DESIGN 1 Title themes).
    var alpha: Float = 1
}

/// Blending of a run of instances: light adds up on dark glass and blends normally on porcelain.
enum OrbBlend: Equatable {
    case over
    case additive
}

/// One frame of GPU work, back to front. Its storage is reused frame after frame (`reset()` keeps capacity), so a
/// warm scene never allocates.
struct OrbScene {
    enum Pass: Equatable {
        case shapes(OrbBlend)
        /// The liquid volume shader in a mode (0 the volume, 1 the meniscus).
        case liquid(Int32)
        /// The `led` per-pixel fields in a mode (0 the silk core, 1 the corona and warp streaks).
        case ledField(Int32, OrbBlend)
        /// A skinned character (`characterMeshes[index]`, DESIGN 2a.6).
        case characterMesh(Int)
        /// Soft lights of the character style (`characterDots[start..<start + count]`).
        case characterDots(OrbBlend)
        /// Atlas sprites of the character style (petals): `characterSpriteBatches[index]`.
        case characterSprites(Int)
    }

    struct Batch {
        var pass: Pass
        /// Clip radius around the canvas center in points, 0 for none.
        var clip: Float
        /// Width of a soft clip edge inside `clip` (0: an anti-aliased hard edge).
        var feather: Float = 0
        var start: Int
        var count: Int
        /// Drawn after the texts (the `led` digit lightning).
        var overlay = false
        /// Depth layer: layers draw in order, each its shapes, then its texts, then its overlay shapes.
        var layer = 0
    }

    /// Drawn first, over the body and under everything else (DESIGN 4c, Placement).
    var vehicle: OrbVehicleLayer?
    private(set) var instances: ContiguousArray<OrbInstance> = []
    private(set) var batches: ContiguousArray<Batch> = []
    /// Drawn after all shapes, in order.
    private(set) var texts: ContiguousArray<OrbTextDraw> = []
    /// Polyline points (canvas points), indexed by the polyline instances.
    private(set) var points: ContiguousArray<SIMD2<Float>> = []
    /// Parameters of the liquid volume when a `.liquid` pass is present.
    var liquid = OrbLiquidUniforms()
    /// Parameters of the `led` fields when a `.ledField` pass is present, and the corona's body noise around the rim.
    var ledField = OrbLedFieldUniforms()
    var ledCoronaBody = ContiguousArray<Float>(repeating: 0.5, count: LedNoise.coronaSamples)
    /// Shapes added while set are drawn after the texts.
    var overlay = false
    /// Multiplies the alpha of everything added while set (style crossfades).
    var alphaScale: Float = 1
    /// Clip radius for shapes added from now on (0 = none).
    var clipRadius: CGFloat = 0
    /// Soft edge of that clip: fully clear at `clipRadius`, fully kept `clipFeather` inside it.
    var clipFeather: CGFloat = 0
    /// Scale of everything around the canvas center (the 1.03 hover scale).
    var contentScale: CGFloat = 1
    var center: CGPoint = .zero
    /// Pixels per point of the target, for text rasterization.
    var backingScale: CGFloat = 2
    /// Depth layer of batches and texts added from now on (0 for every style but `character`, whose readouts pass
    /// behind and in front of her).
    var layer = 0
    /// The highest layer used this frame.
    private(set) var maxLayer = 0
    /// The `character` style's meshes, lights and sprites, referenced by its passes.
    private(set) var characterMeshes: ContiguousArray<CharacterMeshDraw> = []
    private(set) var characterBones: ContiguousArray<SIMD4<Float>> = []
    private(set) var characterOpacities: ContiguousArray<Float> = []
    private(set) var characterDots: ContiguousArray<CharacterDot> = []
    private(set) var characterSprites: ContiguousArray<CharacterSprite> = []
    private(set) var characterSpriteBatches: ContiguousArray<CharacterSpriteBatch> = []

    init(capacity: Int = 0) {
        instances.reserveCapacity(capacity)
        batches.reserveCapacity(32)
        texts.reserveCapacity(48)
        points.reserveCapacity(capacity > 0 ? 2048 : 0)
    }

    mutating func reset() {
        vehicle = nil
        instances.removeAll(keepingCapacity: true)
        batches.removeAll(keepingCapacity: true)
        texts.removeAll(keepingCapacity: true)
        points.removeAll(keepingCapacity: true)
        alphaScale = 1
        clipRadius = 0
        clipFeather = 0
        contentScale = 1
        overlay = false
        layer = 0
        maxLayer = 0
        if !characterMeshes.isEmpty || !characterDots.isEmpty || !characterSprites.isEmpty {
            characterMeshes.removeAll(keepingCapacity: true)
            characterBones.removeAll(keepingCapacity: true)
            characterOpacities.removeAll(keepingCapacity: true)
            characterDots.removeAll(keepingCapacity: true)
            characterSprites.removeAll(keepingCapacity: true)
            characterSpriteBatches.removeAll(keepingCapacity: true)
        }
    }

    var hasCharacterLayers: Bool { !characterMeshes.isEmpty || !characterDots.isEmpty || !characterSprites.isEmpty }

    /// Adds a skinned character: `world` bone transforms and `opacities` per part are copied (alpha scaled by the
    /// style crossfade).
    mutating func addCharacterMesh(_ pose: CharacterPoseGPU, atlas: MTLTexture, world: [Affine2], opacities: [Double],
                                   placement: SIMD4<Float>, feet: SIMD2<Float>, alpha: Float) {
        let firstBone = characterBones.count
        for m in world {
            characterBones.append(SIMD4(Float(m.a), Float(m.b), Float(m.tx), 0))
            characterBones.append(SIMD4(Float(m.c), Float(m.d), Float(m.ty), 0))
        }
        let firstOpacity = characterOpacities.count
        for o in opacities { characterOpacities.append(Float(o)) }
        var placement = placement
        placement.w = alpha * alphaScale
        characterMeshes.append(CharacterMeshDraw(pose: pose, atlas: atlas, firstBone: firstBone, boneCount: world.count,
                                                 firstOpacity: firstOpacity, opacityCount: opacities.count,
                                                 placement: placement, feet: feet))
        appendBatch(.characterMesh(characterMeshes.count - 1), start: 0, count: 0)
    }

    /// Adds soft lights; consecutive calls with the same blend join one batch.
    mutating func addCharacterDot(_ dot: CharacterDot, blend: OrbBlend) {
        var dot = dot
        if alphaScale < 1 { dot.color.w *= alphaScale }
        if let last = batches.indices.last, batches[last].pass == .characterDots(blend), batches[last].layer == layer,
           batches[last].start + batches[last].count == characterDots.count {
            batches[last].count += 1
        } else {
            appendBatch(.characterDots(blend), start: characterDots.count, count: 1)
        }
        characterDots.append(dot)
    }

    /// Adds atlas sprites of one texture as one batch.
    mutating func addCharacterSprites<S: Sequence>(_ sprites: S, atlas: MTLTexture) where S.Element == CharacterSprite {
        let start = characterSprites.count
        for var sprite in sprites {
            if alphaScale < 1 { sprite.extra.y *= alphaScale }
            characterSprites.append(sprite)
        }
        guard characterSprites.count > start else { return }
        characterSpriteBatches.append(CharacterSpriteBatch(atlas: atlas, start: start, count: characterSprites.count - start))
        appendBatch(.characterSprites(characterSpriteBatches.count - 1), start: 0, count: 0)
    }

    private mutating func appendBatch(_ pass: Pass, start: Int, count: Int) {
        maxLayer = max(maxLayer, layer)
        batches.append(Batch(pass: pass, clip: 0, start: start, count: count, overlay: overlay, layer: layer))
    }

    mutating func add(_ instance: OrbInstance, blend: OrbBlend) {
        var instance = instance
        if alphaScale < 1 { instance.scaleAlpha(alphaScale) }
        let clip = Float(clipRadius)
        let feather = Float(clipFeather)
        if let last = batches.indices.last, batches[last].pass == .shapes(blend), batches[last].clip == clip,
           batches[last].feather == feather, batches[last].overlay == overlay, batches[last].layer == layer,
           batches[last].start + batches[last].count == instances.count {
            batches[last].count += 1
        } else {
            maxLayer = max(maxLayer, layer)
            batches.append(Batch(pass: .shapes(blend), clip: clip, feather: feather, start: instances.count, count: 1,
                                 overlay: overlay, layer: layer))
        }
        instances.append(instance)
    }

    /// Adds a three-stroke polyline through `path` (points relative to `origin`, y down); see `OrbInstance.polyline`.
    mutating func addPolyline<Path: Sequence>(_ path: Path, origin: CGPoint, glowColor: RGBColor, coreColor: RGBColor,
                                              outer: OrbStroke, mid: OrbStroke, core: OrbStroke, blend: OrbBlend)
        where Path.Element == SIMD2<Double> {
        let first = points.count
        var lo = SIMD2<Float>(repeating: .greatestFiniteMagnitude)
        var hi = SIMD2<Float>(repeating: -.greatestFiniteMagnitude)
        let o = SIMD2<Float>(Float(origin.x), Float(origin.y))
        for point in path {
            let p = o + SIMD2<Float>(Float(point.x), Float(point.y))
            lo = pointwiseMin(lo, p)
            hi = pointwiseMax(hi, p)
            points.append(p)
        }
        let count = points.count - first
        guard count >= 2 else {
            points.removeLast(count)
            return
        }
        let pad = Float(max(outer.width, mid.width, core.width) / 2 + 1)
        let bounds = CGRect(x: CGFloat(lo.x - pad), y: CGFloat(lo.y - pad), width: CGFloat(hi.x - lo.x + 2 * pad),
                            height: CGFloat(hi.y - lo.y + 2 * pad))
        add(.polyline(first: first, count: count, bounds: bounds, glowColor: glowColor, coreColor: coreColor, outer: outer,
                      mid: mid, core: core, sum: blend == .additive), blend: blend)
    }

    mutating func addLiquid(mode: Int32) {
        batches.append(Batch(pass: .liquid(mode), clip: 0, start: instances.count, count: 0))
    }

    mutating func addLedField(mode: Int32, blend: OrbBlend) {
        batches.append(Batch(pass: .ledField(mode, blend), clip: 0, start: instances.count, count: 0))
    }

    mutating func add(_ text: OrbTextDraw) {
        var text = text
        if alphaScale < 1 {
            text.instance.color.w *= alphaScale
            text.instance.glow.w *= alphaScale
        }
        text.layer = layer
        maxLayer = max(maxLayer, layer)
        texts.append(text)
    }

    var instanceCount: Int { instances.count }
    var hasLiquid: Bool { batches.contains { if case .liquid = $0.pass { true } else { false } } }
}
