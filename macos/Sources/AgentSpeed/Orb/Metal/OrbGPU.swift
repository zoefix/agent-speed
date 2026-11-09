import Metal
import os

/// Shared Metal device and pipelines, created once on first use.
final class OrbGPU: Sendable {
    static let shared: OrbGPU? = OrbGPU()

    let device: MTLDevice
    let queue: MTLCommandQueue
    let over: MTLRenderPipelineState
    let additive: MTLRenderPipelineState
    let text: MTLRenderPipelineState
    let liquid: MTLRenderPipelineState
    /// The `led` silk core and corona fields (DESIGN 2.1), laid over (light, silk) or adding light (dark corona).
    let ledOver: MTLRenderPipelineState
    let ledAdditive: MTLRenderPipelineState
    /// Lays the half-resolution silk over the disc.
    let ledSilk: MTLRenderPipelineState
    /// The heat palette (DESIGN 1) as a 256 × 1 texture, for shapes that follow the spectrum.
    nonisolated(unsafe) let palette: MTLTexture
    /// The dashboard hero's vehicle layer (DESIGN 4c); nil if its shaders failed, which only hides the vehicle.
    let hero: HeroLayerGPU?
    /// The `character` style's mesh, lights and sprites (DESIGN 2a.6); nil if its shaders failed.
    let character: CharacterLayerGPU?
    static let pixelFormat = MTLPixelFormat.bgra8Unorm

    private init?() {
        guard let device = MTLCreateSystemDefaultDevice(), let queue = device.makeCommandQueue() else { return nil }
        let logger = Logger(subsystem: "io.github.zoefix.agentspeed", category: "orb")
        do {
            let library = try device.makeLibrary(source: OrbShaderSource.source, options: nil)
            guard let vertex = library.makeFunction(name: "orb_vertex"),
                  let fragment = library.makeFunction(name: "orb_fragment"),
                  let textVertex = library.makeFunction(name: "orb_text_vertex"),
                  let textFragment = library.makeFunction(name: "orb_text_fragment"),
                  let liquidVertex = library.makeFunction(name: "orb_liquid_vertex"),
                  let liquidFragment = library.makeFunction(name: "orb_liquid_fragment"),
                  let ledVertex = library.makeFunction(name: "orb_led_vertex"),
                  let ledFragment = library.makeFunction(name: "orb_led_fragment"),
                  let ledSilkFragment = library.makeFunction(name: "orb_led_silk_fragment") else {
                logger.error("Orb shader functions are missing")
                return nil
            }
            over = try Self.pipeline(device: device, vertex: vertex, fragment: fragment, additive: false)
            additive = try Self.pipeline(device: device, vertex: vertex, fragment: fragment, additive: true)
            text = try Self.pipeline(device: device, vertex: textVertex, fragment: textFragment, additive: false)
            liquid = try Self.pipeline(device: device, vertex: liquidVertex, fragment: liquidFragment, additive: false)
            ledOver = try Self.pipeline(device: device, vertex: ledVertex, fragment: ledFragment, additive: false)
            ledAdditive = try Self.pipeline(device: device, vertex: ledVertex, fragment: ledFragment, additive: true)
            ledSilk = try Self.pipeline(device: device, vertex: ledVertex, fragment: ledSilkFragment, additive: false)
        } catch {
            logger.error("Orb shaders failed to compile: \(String(describing: error), privacy: .public)")
            return nil
        }
        guard let palette = Self.paletteTexture(device: device) else { return nil }
        self.device = device
        self.queue = queue
        self.palette = palette
        hero = HeroLayerGPU(device: device)
        character = CharacterLayerGPU(device: device, pixelFormat: Self.pixelFormat)
    }

    func pipeline(for blend: OrbBlend) -> MTLRenderPipelineState {
        blend == .over ? over : additive
    }

    func ledPipeline(for blend: OrbBlend) -> MTLRenderPipelineState {
        blend == .over ? ledOver : ledAdditive
    }

    private static func paletteTexture(device: MTLDevice) -> MTLTexture? {
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: 256, height: 1, mipmapped: false)
        descriptor.usage = [.shaderRead]
        guard let texture = device.makeTexture(descriptor: descriptor) else { return nil }
        var bytes = [UInt8](repeating: 255, count: 256 * 4)
        for index in 0..<256 {
            let color = HeatPalette.color(Double(index) / 255)
            bytes[index * 4] = UInt8((color.red * 255).rounded().clamped(to: 0...255))
            bytes[index * 4 + 1] = UInt8((color.green * 255).rounded().clamped(to: 0...255))
            bytes[index * 4 + 2] = UInt8((color.blue * 255).rounded().clamped(to: 0...255))
        }
        texture.replace(region: MTLRegionMake2D(0, 0, 256, 1), mipmapLevel: 0, withBytes: bytes, bytesPerRow: 256 * 4)
        return texture
    }

    private static func pipeline(device: MTLDevice, vertex: MTLFunction, fragment: MTLFunction, additive: Bool) throws -> MTLRenderPipelineState {
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
