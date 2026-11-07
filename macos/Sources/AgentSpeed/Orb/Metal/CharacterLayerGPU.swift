import AgentSpeedCharacter
import Metal
import os
import simd

/// One vertex of a character mesh (layout matches `CharacterVertex` in `CharacterShaderSource`).
struct CharacterVertex: Equatable {
    var position: SIMD2<Float>
    var uv: SIMD2<Float>
    var weights: SIMD4<Float>
    var bones: SIMD4<UInt16>
    var part: UInt32
    var pad: UInt32 = 0
}

/// A soft light of the character style (layout matches `Dot`).
struct CharacterDot {
    var geometry: SIMD4<Float>
    var color: SIMD4<Float>
    var extra: SIMD4<Float>

    init(_ dot: HudDot) {
        geometry = SIMD4(dot.x, dot.y, dot.rx, dot.ry)
        color = SIMD4(dot.r, dot.g, dot.b, dot.alpha)
        extra = SIMD4(dot.softness, 0, 0, 0)
    }

    init(x: Float, y: Float, rx: Float, ry: Float, color: SIMD4<Float>, softness: Float) {
        geometry = SIMD4(x, y, rx, ry)
        self.color = color
        extra = SIMD4(softness, 0, 0, 0)
    }
}

/// An atlas sprite (layout matches `Sprite`): a petal.
struct CharacterSprite {
    var geometry: SIMD4<Float>
    var uv: SIMD4<Float>
    /// Rotation (radians), opacity.
    var extra: SIMD4<Float>
}

struct CharacterSpriteBatch {
    let atlas: MTLTexture
    let start: Int
    let count: Int
}

/// A character to draw this frame: its pose geometry, atlas, the slices of the scene's bone and opacity arrays, and
/// the placement on the canvas (the idle feet on screen, points per pose pixel, opacity; the pose's feet).
struct CharacterMeshDraw {
    let pose: CharacterPoseGPU
    let atlas: MTLTexture
    let firstBone: Int
    let boneCount: Int
    let firstOpacity: Int
    let opacityCount: Int
    var placement: SIMD4<Float>
    let feet: SIMD2<Float>
}

/// A pose's static geometry on the GPU: vertices (rest positions, atlas coordinates, up to four bones and weights,
/// the part) and the triangles of every part in draw order, so one indexed draw paints the character back to front.
/// Immutable once made.
final class CharacterPoseGPU: @unchecked Sendable {
    let vertices: MTLBuffer
    let indices: MTLBuffer
    let indexCount: Int
    let partCount: Int

    init?(rig: CharacterRig, device: MTLDevice) {
        let packed = CharacterMeshPacking.pack(rig)
        guard !packed.indices.isEmpty,
              let vertices = packed.vertices.withUnsafeBytes({ device.makeBuffer(bytes: $0.baseAddress!, length: $0.count) }),
              let indices = packed.indices.withUnsafeBytes({ device.makeBuffer(bytes: $0.baseAddress!, length: $0.count) })
        else { return nil }
        self.vertices = vertices
        self.indices = indices
        indexCount = packed.indices.count
        partCount = rig.parts.count
    }
}

/// Flattens a rig's parts into one vertex array and one index array (draw order kept).
enum CharacterMeshPacking {
    static func pack(_ rig: CharacterRig) -> (vertices: [CharacterVertex], indices: [UInt32]) {
        var vertices: [CharacterVertex] = []
        var indices: [UInt32] = []
        vertices.reserveCapacity(rig.parts.reduce(0) { $0 + $1.vertexCount })
        indices.reserveCapacity(rig.parts.reduce(0) { $0 + $1.triangles.count })
        let w = Float(max(1, rig.atlasSize.width)), h = Float(max(1, rig.atlasSize.height))
        for (partIndex, part) in rig.parts.enumerated() {
            let base = UInt32(vertices.count)
            let k = max(1, part.influences)
            for v in 0..<part.vertexCount {
                var bones = SIMD4<UInt16>(repeating: 0)
                var weights = SIMD4<Float>(repeating: 0)
                for i in 0..<min(4, k) {
                    bones[i] = part.bones[v * k + i]
                    weights[i] = Float(part.weights[v * k + i])
                }
                vertices.append(CharacterVertex(
                    position: SIMD2(Float(part.vertices[2 * v]), Float(part.vertices[2 * v + 1])),
                    uv: SIMD2(Float(part.uv[2 * v]) / w, Float(part.uv[2 * v + 1]) / h),
                    weights: weights, bones: bones, part: UInt32(partIndex)))
            }
            for t in part.triangles { indices.append(base + t) }
        }
        return (vertices, indices)
    }
}

/// The character style's pipelines (DESIGN 2a.6), hung off `OrbGPU`; nil if its shaders fail, which only hides the
/// character.
final class CharacterLayerGPU: @unchecked Sendable {
    let device: MTLDevice
    let mesh: MTLRenderPipelineState
    let dotsOver: MTLRenderPipelineState
    let dotsAdditive: MTLRenderPipelineState
    let sprites: MTLRenderPipelineState
    let sampler: MTLSamplerState

    init?(device: MTLDevice, pixelFormat: MTLPixelFormat) {
        let logger = Logger(subsystem: "io.github.zoefix.agentspeed", category: "character")
        do {
            let library = try device.makeLibrary(source: CharacterShaderSource.source, options: nil)
            func function(_ name: String) throws -> MTLFunction {
                guard let f = library.makeFunction(name: name) else { throw CharacterGPUError.missing(name) }
                return f
            }
            mesh = try Self.pipeline(device, try function("character_mesh_vertex"), try function("character_mesh_fragment"),
                                     pixelFormat, additive: false)
            dotsOver = try Self.pipeline(device, try function("character_dot_vertex"), try function("character_dot_fragment"),
                                         pixelFormat, additive: false)
            dotsAdditive = try Self.pipeline(device, try function("character_dot_vertex"),
                                             try function("character_dot_fragment"), pixelFormat, additive: true)
            sprites = try Self.pipeline(device, try function("character_sprite_vertex"),
                                        try function("character_sprite_fragment"), pixelFormat, additive: false)
        } catch {
            logger.error("Character shaders failed: \(String(describing: error), privacy: .public)")
            return nil
        }
        let descriptor = MTLSamplerDescriptor()
        descriptor.minFilter = .linear
        descriptor.magFilter = .linear
        descriptor.mipFilter = .linear
        descriptor.sAddressMode = .clampToEdge
        descriptor.tAddressMode = .clampToEdge
        descriptor.maxAnisotropy = 4
        guard let sampler = device.makeSamplerState(descriptor: descriptor) else { return nil }
        self.device = device
        self.sampler = sampler
    }

    private enum CharacterGPUError: Error {
        case missing(String)
    }

    private static func pipeline(_ device: MTLDevice, _ vertex: MTLFunction, _ fragment: MTLFunction,
                                 _ pixelFormat: MTLPixelFormat, additive: Bool) throws -> MTLRenderPipelineState {
        let descriptor = MTLRenderPipelineDescriptor()
        descriptor.vertexFunction = vertex
        descriptor.fragmentFunction = fragment
        let attachment = descriptor.colorAttachments[0]!
        attachment.pixelFormat = pixelFormat
        attachment.isBlendingEnabled = true
        attachment.rgbBlendOperation = .add
        attachment.alphaBlendOperation = .add
        attachment.sourceRGBBlendFactor = .one
        attachment.destinationRGBBlendFactor = additive ? .one : .oneMinusSourceAlpha
        attachment.sourceAlphaBlendFactor = .one
        attachment.destinationAlphaBlendFactor = .oneMinusSourceAlpha
        return try device.makeRenderPipelineState(descriptor: descriptor)
    }
}

/// Encodes the character passes of scenes. Per-frame data (bones, opacities, lights, sprites) goes into a ring of
/// grow-only buffers advanced once per encoded frame, in step with `OrbEncoder`'s ring.
final class CharacterLayerEncoder {
    private static let ringSize = 3
    private let gpu: CharacterLayerGPU
    private var buffers: [MTLBuffer?] = Array(repeating: nil, count: ringSize)
    private var index = 0
    private var cursor = 0

    /// Byte offsets of one scene's per-frame data in the current buffer.
    struct Offsets {
        var bones = 0
        var opacities = 0
        var dots = 0
        var sprites = 0
    }

    init(gpu: CharacterLayerGPU) {
        self.gpu = gpu
    }

    static func bytes(of scene: OrbScene) -> Int {
        guard scene.hasCharacterLayers else { return 0 }
        return align(scene.characterBones.count * 16) + align(scene.characterOpacities.count * 4)
            + align(scene.characterDots.count * MemoryLayout<CharacterDot>.stride)
            + align(scene.characterSprites.count * MemoryLayout<CharacterSprite>.stride)
    }

    private static func align(_ n: Int) -> Int { (max(n, 16) + 255) / 256 * 256 }

    /// Starts a frame needing `total` bytes; false if no buffer could be made.
    func beginFrame(total: Int) -> Bool {
        index = (index + 1) % Self.ringSize
        cursor = 0
        if let buffer = buffers[index], buffer.length >= total { return true }
        buffers[index] = gpu.device.makeBuffer(length: max(total, 256 * 1024), options: .storageModeShared)
        return buffers[index] != nil
    }

    func upload(_ scene: OrbScene) -> Offsets? {
        guard scene.hasCharacterLayers, let buffer = buffers[index] else { return nil }
        var offsets = Offsets()
        func copy<T>(_ array: ContiguousArray<T>) -> Int {
            let start = cursor
            let length = array.count * MemoryLayout<T>.stride
            array.withUnsafeBytes { source in
                if let base = source.baseAddress, length > 0 { (buffer.contents() + start).copyMemory(from: base, byteCount: length) }
            }
            cursor += Self.align(length)
            return start
        }
        offsets.bones = copy(scene.characterBones)
        offsets.opacities = copy(scene.characterOpacities)
        offsets.dots = copy(scene.characterDots)
        offsets.sprites = copy(scene.characterSprites)
        return offsets
    }

    struct MeshParams {
        var placement: SIMD4<Float>
        var feet: SIMD4<Float>
    }

    func draw(_ pass: OrbScene.Pass, batch: OrbScene.Batch, scene: OrbScene, offsets: Offsets,
              uniforms: UnsafeRawBufferPointer, into encoder: MTLRenderCommandEncoder) {
        guard let buffer = buffers[index], let base = uniforms.baseAddress else { return }
        switch pass {
        case .characterMesh(let i):
            let draw = scene.characterMeshes[i]
            var params = MeshParams(placement: draw.placement, feet: SIMD4(draw.feet.x, draw.feet.y, 0, 0))
            encoder.setRenderPipelineState(gpu.mesh)
            encoder.setVertexBuffer(draw.pose.vertices, offset: 0, index: 0)
            encoder.setVertexBuffer(buffer, offset: offsets.bones + draw.firstBone * 16, index: 1)
            encoder.setVertexBuffer(buffer, offset: offsets.opacities + draw.firstOpacity * 4, index: 2)
            encoder.setVertexBytes(&params, length: MemoryLayout<MeshParams>.stride, index: 3)
            encoder.setVertexBytes(base, length: uniforms.count, index: 4)
            encoder.setFragmentTexture(draw.atlas, index: 0)
            encoder.setFragmentSamplerState(gpu.sampler, index: 0)
            encoder.drawIndexedPrimitives(type: .triangle, indexCount: draw.pose.indexCount, indexType: .uint32,
                                          indexBuffer: draw.pose.indices, indexBufferOffset: 0)
        case .characterDots(let blend):
            guard batch.count > 0 else { return }
            encoder.setRenderPipelineState(blend == .additive ? gpu.dotsAdditive : gpu.dotsOver)
            encoder.setVertexBuffer(buffer, offset: offsets.dots + batch.start * MemoryLayout<CharacterDot>.stride, index: 0)
            encoder.setVertexBytes(base, length: uniforms.count, index: 1)
            encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6, instanceCount: batch.count)
        case .characterSprites(let i):
            let sprites = scene.characterSpriteBatches[i]
            encoder.setRenderPipelineState(gpu.sprites)
            encoder.setVertexBuffer(buffer, offset: offsets.sprites + sprites.start * MemoryLayout<CharacterSprite>.stride,
                                    index: 0)
            encoder.setVertexBytes(base, length: uniforms.count, index: 1)
            encoder.setFragmentTexture(sprites.atlas, index: 0)
            encoder.setFragmentSamplerState(gpu.sampler, index: 0)
            encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6, instanceCount: sprites.count)
        default:
            break
        }
    }
}
