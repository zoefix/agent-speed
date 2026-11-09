import Metal
import simd

/// Encodes an `OrbScene` into a render pass. Keeps a small ring of grow-only instance buffers so the CPU never waits on
/// the GPU and a warm encoder never allocates buffers.
final class OrbEncoder {
    private static let ringSize = 3
    private let gpu: OrbGPU
    private var buffers: [MTLBuffer?]
    private var index = 0
    private let semaphore = DispatchSemaphore(value: ringSize)
    private lazy var heroEncoder: HeroLayerEncoder? = gpu.hero.map(HeroLayerEncoder.init)
    private lazy var characterEncoder: CharacterLayerEncoder? = gpu.character.map(CharacterLayerEncoder.init)
    /// The `led` silk drawn at half the backing resolution before the frame's pass (one per encoder, reused).
    private var silkTexture: MTLTexture?
    private let silkPass: MTLRenderPassDescriptor = {
        let pass = MTLRenderPassDescriptor()
        pass.colorAttachments[0].loadAction = .clear
        pass.colorAttachments[0].storeAction = .store
        pass.colorAttachments[0].clearColor = MTLClearColor(red: 0, green: 0, blue: 0, alpha: 0)
        return pass
    }()
    /// Side (points) of the silk layer of the frame being encoded, 0 when the silk is drawn directly.
    private var silkSide: Float = 0

    private struct Uniforms {
        var viewport: SIMD4<Float>
        var transform: SIMD4<Float>
    }

    init(gpu: OrbGPU) {
        self.gpu = gpu
        buffers = Array(repeating: nil, count: Self.ringSize)
    }

    /// GPU time of completed frames, for benchmarks.
    let gpuTime = OrbGPUTimer()

    /// Draws the scene into `pass` and commits. `scale` is pixels per point; `size` is in points.
    func render(_ scene: OrbScene, size: CGSize, scale: CGFloat, pass: MTLRenderPassDescriptor,
                present drawable: MTLDrawable?, waitUntilCompleted: Bool = false) {
        render(count: 1, size: size, scale: scale, pass: pass, present: drawable, waitUntilCompleted: waitUntilCompleted) { _ in
            (scene, CGRect(origin: .zero, size: size), nil)
        }
    }

    /// Draws `count` scenes into one pass and commits once (one drawable for several small orbs, e.g. the Settings
    /// style cards). Scene `i` is a canvas of `frame.size` points drawn at `frame` (points from the top-left of the
    /// `size` drawable) and clipped to `clip`; a nil clip draws the scene over the whole drawable as `render` does.
    func render(count: Int, size: CGSize, scale: CGFloat, pass: MTLRenderPassDescriptor, present drawable: MTLDrawable?,
                waitUntilCompleted: Bool = false, scene: (Int) -> (scene: OrbScene, frame: CGRect, clip: CGRect?)) {
        guard let commandBuffer = beginFrame() else { return }
        encode(count: count, size: size, scale: scale, pass: pass, present: drawable, into: commandBuffer, scene: scene)
        commandBuffer.commit()
        if waitUntilCompleted { commandBuffer.waitUntilCompleted() }
    }

    /// A command buffer for this encoder's next frame (waits for a free instance buffer; records its GPU time).
    func beginFrame() -> MTLCommandBuffer? {
        semaphore.wait()
        guard let commandBuffer = gpu.queue.makeCommandBuffer() else {
            semaphore.signal()
            return nil
        }
        let semaphore = semaphore
        let timer = gpuTime
        commandBuffer.addCompletedHandler { buffer in
            semaphore.signal()
            timer.record(buffer.gpuEndTime - buffer.gpuStartTime)
        }
        return commandBuffer
    }

    /// Draws `scene` into `pass` inside another encoder's frame (`commandBuffer`, committed by its owner), so two
    /// surfaces drawn on the same tick cost one submission. The GPU time is the owner's.
    func render(_ scene: OrbScene, size: CGSize, scale: CGFloat, pass: MTLRenderPassDescriptor, present drawable: MTLDrawable?,
                joining commandBuffer: MTLCommandBuffer) {
        semaphore.wait()
        let semaphore = semaphore
        commandBuffer.addCompletedHandler { _ in semaphore.signal() }
        encode(count: 1, size: size, scale: scale, pass: pass, present: drawable, into: commandBuffer) { _ in
            (scene, CGRect(origin: .zero, size: size), nil)
        }
    }

    /// Encodes the scenes into `pass` and presents `drawable` within `commandBuffer`.
    func encode(count: Int, size: CGSize, scale: CGFloat, pass: MTLRenderPassDescriptor, present drawable: MTLDrawable?,
                into commandBuffer: MTLCommandBuffer, scene: (Int) -> (scene: OrbScene, frame: CGRect, clip: CGRect?)) {
        let stride = MemoryLayout<OrbInstance>.stride
        let pointStride = MemoryLayout<SIMD2<Float>>.stride
        var total = 0
        var totalPoints = 0
        var hasVehicle = false
        var characterBytes = 0
        for index in 0..<count {
            let item = scene(index).scene
            total += item.instanceCount
            totalPoints += item.points.count
            hasVehicle = hasVehicle || item.vehicle != nil
            characterBytes += CharacterLayerEncoder.bytes(of: item)
        }
        let characters = characterBytes > 0 && (characterEncoder?.beginFrame(total: characterBytes) ?? false)
        // Polyline points follow the instances in the same buffer.
        let pointsBase = (total * stride + 255) / 256 * 256
        var buffer: MTLBuffer?
        if total > 0, let instances = instanceBuffer(minimumLength: pointsBase + max(1, totalPoints) * pointStride) {
            buffer = instances
            var offset = 0
            var pointOffset = 0
            for index in 0..<count {
                let item = scene(index).scene
                let itemCount = item.instanceCount
                guard itemCount > 0 else { continue }
                item.instances.withUnsafeBufferPointer { source in
                    guard let base = source.baseAddress else { return }
                    (instances.contents() + offset * stride).copyMemory(from: base, byteCount: itemCount * stride)
                }
                item.points.withUnsafeBufferPointer { source in
                    guard let base = source.baseAddress, !source.isEmpty else { return }
                    (instances.contents() + pointsBase + pointOffset * pointStride)
                        .copyMemory(from: base, byteCount: source.count * pointStride)
                }
                offset += itemCount
                pointOffset += item.points.count
            }
        }

        if hasVehicle { gpu.hero?.configure(pass) }
        silkSide = 0
        if count == 1 { encodeSilkLayer(scene(0).scene, scale: scale, into: commandBuffer) }
        if let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: pass) {
            var first = 0
            var firstPoint = 0
            let pixels = CGSize(width: (size.width * scale).rounded(), height: (size.height * scale).rounded())
            for index in 0..<count {
                let item = scene(index)
                if let clip = item.clip {
                    encoder.setViewport(MTLViewport(originX: Double(item.frame.minX * scale), originY: Double(item.frame.minY * scale),
                                                    width: Double(item.frame.width * scale), height: Double(item.frame.height * scale),
                                                    znear: 0, zfar: 1))
                    let rect = CGRect(x: clip.minX * scale, y: clip.minY * scale, width: clip.width * scale, height: clip.height * scale)
                        .integral.intersection(CGRect(origin: .zero, size: pixels))
                    guard !rect.isNull, rect.width >= 1, rect.height >= 1 else {
                        first += item.scene.instanceCount
                        firstPoint += item.scene.points.count
                        continue
                    }
                    encoder.setScissorRect(MTLScissorRect(x: Int(rect.minX), y: Int(rect.minY), width: Int(rect.width), height: Int(rect.height)))
                }
                let offsets = characters ? characterEncoder?.upload(item.scene) : nil
                encode(item.scene, size: item.frame.size, scale: scale, buffer: buffer, first: first,
                       points: pointsBase + firstPoint * pointStride, characterOffsets: offsets, into: encoder)
                first += item.scene.instanceCount
                firstPoint += item.scene.points.count
            }
            encoder.endEncoding()
        }
        if let drawable { commandBuffer.present(drawable) }
    }

    /// Encodes one scene whose instances start at `first` in `buffer` and whose polyline points start at byte `points`.
    /// Overlay batches are drawn after the texts.
    private func encode(_ scene: OrbScene, size: CGSize, scale: CGFloat, buffer: MTLBuffer?, first: Int, points: Int,
                        characterOffsets: CharacterLayerEncoder.Offsets?, into encoder: MTLRenderCommandEncoder) {
        let stride = MemoryLayout<OrbInstance>.stride
        if let vehicle = scene.vehicle {
            heroEncoder?.encode(vehicle.list, origin: vehicle.origin, unit: vehicle.unit, viewport: size, scale: scale,
                                alpha: vehicle.alpha, into: encoder)
        }
        var uniforms = Uniforms(
            viewport: SIMD4(Float(size.width), Float(size.height), Float(scale), 0),
            transform: SIMD4(Float(scene.contentScale), Float(scene.center.x), Float(scene.center.y), 0)
        )
        var liquid = scene.liquid
        var led = scene.ledField
        encoder.setFragmentTexture(gpu.palette, index: 0)
        func draw(_ batch: OrbScene.Batch) {
            uniforms.viewport.w = batch.clip
            uniforms.transform.w = batch.feather
            switch batch.pass {
            case .shapes(let blend):
                guard let buffer, batch.count > 0 else { return }
                encoder.setRenderPipelineState(gpu.pipeline(for: blend))
                encoder.setVertexBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
                encoder.setFragmentBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
                encoder.setVertexBuffer(buffer, offset: (first + batch.start) * stride, index: 0)
                encoder.setFragmentBuffer(buffer, offset: (first + batch.start) * stride, index: 0)
                encoder.setFragmentBuffer(buffer, offset: points, index: 2)
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6, instanceCount: batch.count)
            case .liquid(let mode):
                var mode = mode
                encoder.setRenderPipelineState(gpu.liquid)
                encoder.setVertexBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
                encoder.setFragmentBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
                encoder.setVertexBytes(&liquid, length: MemoryLayout<OrbLiquidUniforms>.stride, index: 2)
                encoder.setFragmentBytes(&liquid, length: MemoryLayout<OrbLiquidUniforms>.stride, index: 2)
                encoder.setFragmentBytes(&mode, length: MemoryLayout<Int32>.stride, index: 3)
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6)
            case .ledField(0, _) where silkSide > 0:
                var mode: Int32 = 0
                var layer = SIMD4<Float>(silkSide, 0, 0, 0)
                encoder.setRenderPipelineState(gpu.ledSilk)
                encoder.setVertexBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
                encoder.setVertexBytes(&led, length: MemoryLayout<OrbLedFieldUniforms>.stride, index: 2)
                encoder.setVertexBytes(&mode, length: MemoryLayout<Int32>.stride, index: 3)
                encoder.setFragmentBytes(&led, length: MemoryLayout<OrbLedFieldUniforms>.stride, index: 2)
                encoder.setFragmentBytes(&layer, length: MemoryLayout<SIMD4<Float>>.stride, index: 4)
                encoder.setFragmentTexture(silkTexture, index: 1)
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: OrbLedShaderSource.vertexCount(mode: 0))
            case .ledField(let mode, let blend):
                var mode = mode
                encoder.setRenderPipelineState(gpu.ledPipeline(for: blend))
                encoder.setVertexBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
                encoder.setFragmentBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
                encoder.setVertexBytes(&led, length: MemoryLayout<OrbLedFieldUniforms>.stride, index: 2)
                encoder.setFragmentBytes(&led, length: MemoryLayout<OrbLedFieldUniforms>.stride, index: 2)
                encoder.setVertexBytes(&mode, length: MemoryLayout<Int32>.stride, index: 3)
                encoder.setFragmentBytes(&mode, length: MemoryLayout<Int32>.stride, index: 3)
                scene.ledCoronaBody.withUnsafeBytes { bytes in
                    guard let base = bytes.baseAddress else { return }
                    encoder.setFragmentBytes(base, length: bytes.count, index: 4)
                }
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: OrbLedShaderSource.vertexCount(mode: mode))
            case .characterMesh, .characterDots, .characterSprites:
                guard let characterEncoder, let characterOffsets else { return }
                uniforms.viewport.w = 0
                uniforms.transform.w = 0
                withUnsafeBytes(of: &uniforms) { bytes in
                    characterEncoder.draw(batch.pass, batch: batch, scene: scene, offsets: characterOffsets, uniforms: bytes,
                                          into: encoder)
                }
                encoder.setFragmentTexture(gpu.palette, index: 0)
            }
        }
        func drawTexts(layer: Int) {
            guard scene.texts.contains(where: { $0.layer == layer }) else { return }
            uniforms.viewport.w = 0
            uniforms.transform.w = 0
            encoder.setRenderPipelineState(gpu.text)
            encoder.setVertexBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
            encoder.setFragmentBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
            encoder.setFragmentBytes(&liquid, length: MemoryLayout<OrbLiquidUniforms>.stride, index: 2)
            for draw in scene.texts where draw.layer == layer {
                var instance = draw.instance
                encoder.setVertexBytes(&instance, length: MemoryLayout<OrbTextInstance>.stride, index: 0)
                encoder.setFragmentBytes(&instance, length: MemoryLayout<OrbTextInstance>.stride, index: 0)
                encoder.setFragmentTexture(draw.texture.texture, index: 0)
                encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: 6)
            }
            encoder.setFragmentTexture(gpu.palette, index: 0)
        }
        // Layer by layer: shapes, texts, overlay shapes. Every style but `character` uses layer 0 only.
        for layer in 0...scene.maxLayer {
            for batch in scene.batches where !batch.overlay && batch.layer == layer { draw(batch) }
            drawTexts(layer: layer)
            for batch in scene.batches where batch.overlay && batch.layer == layer { draw(batch) }
        }
    }

    /// Renders the scene's silk (DESIGN 2.1) at half the backing resolution into `silkTexture`, so its four height samples
    /// per pixel run on a quarter of the pixels; the frame's pass then lays it over the disc. Soft folds lose nothing.
    private func encodeSilkLayer(_ scene: OrbScene, scale: CGFloat, into commandBuffer: MTLCommandBuffer) {
        guard scene.batches.contains(where: { $0.pass == .ledField(0, .over) }) else { return }
        let layerScale = max(1, scale / 2)
        guard layerScale < scale else { return }
        let s = CGFloat(scene.ledField.frame.z)
        let pixels = Int((0.96 * s * layerScale).rounded(.up)) + 2
        let side = CGFloat(pixels) / layerScale
        if silkTexture.map({ $0.width != pixels }) ?? true {
            let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: OrbGPU.pixelFormat, width: pixels, height: pixels,
                                                                      mipmapped: false)
            descriptor.usage = [.renderTarget, .shaderRead]
            descriptor.storageMode = .private
            silkTexture = gpu.device.makeTexture(descriptor: descriptor)
        }
        guard let texture = silkTexture else { return }
        silkPass.colorAttachments[0].texture = texture
        defer { silkPass.colorAttachments[0].texture = nil }
        guard let encoder = commandBuffer.makeRenderCommandEncoder(descriptor: silkPass) else { return }
        var uniforms = Uniforms(viewport: SIMD4(Float(side), Float(side), Float(layerScale), 0),
                                transform: SIMD4(1, Float(side / 2), Float(side / 2), 0))
        var led = scene.ledField
        led.frame.x = Float(side / 2)
        led.frame.y = Float(side / 2)
        var mode: Int32 = 0
        encoder.setRenderPipelineState(gpu.ledOver)
        encoder.setFragmentTexture(gpu.palette, index: 0)
        encoder.setVertexBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
        encoder.setFragmentBytes(&uniforms, length: MemoryLayout<Uniforms>.stride, index: 1)
        encoder.setVertexBytes(&led, length: MemoryLayout<OrbLedFieldUniforms>.stride, index: 2)
        encoder.setFragmentBytes(&led, length: MemoryLayout<OrbLedFieldUniforms>.stride, index: 2)
        encoder.setVertexBytes(&mode, length: MemoryLayout<Int32>.stride, index: 3)
        encoder.setFragmentBytes(&mode, length: MemoryLayout<Int32>.stride, index: 3)
        scene.ledCoronaBody.withUnsafeBytes { bytes in
            guard let base = bytes.baseAddress else { return }
            encoder.setFragmentBytes(base, length: bytes.count, index: 4)
        }
        encoder.drawPrimitives(type: .triangle, vertexStart: 0, vertexCount: OrbLedShaderSource.vertexCount(mode: 0))
        encoder.endEncoding()
        silkSide = Float(side)
    }

    private func instanceBuffer(minimumLength: Int) -> MTLBuffer? {
        index = (index + 1) % Self.ringSize
        if let buffer = buffers[index], buffer.length >= minimumLength { return buffer }
        let length = max(minimumLength, 64 * 1024)
        let buffer = gpu.device.makeBuffer(length: length, options: .storageModeShared)
        buffers[index] = buffer
        return buffer
    }
}

/// Sums the GPU time of completed command buffers (written from Metal's completion queue).
final class OrbGPUTimer: @unchecked Sendable {
    private let lock = NSLock()
    private var total = 0.0
    private var frames = 0

    func record(_ seconds: Double) {
        guard seconds > 0, seconds < 1 else { return }
        lock.lock()
        total += seconds
        frames += 1
        lock.unlock()
    }

    /// Total GPU seconds and frames since the last call.
    func drain() -> (seconds: Double, frames: Int) {
        lock.lock()
        defer {
            total = 0
            frames = 0
            lock.unlock()
        }
        return (total, frames)
    }
}
