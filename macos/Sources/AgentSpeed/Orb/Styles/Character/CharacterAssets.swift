import AgentSpeedCharacter
import AgentSpeedCore
import CoreGraphics
import Foundation
import ImageIO
import Metal

/// A loaded pack: its rigs and each pose's GPU geometry, plus the petal sprite's atlas rectangle per pose. Immutable
/// once made.
final class LoadedCharacter: @unchecked Sendable {
    let pack: CharacterPack
    let poses: [CharacterPoseGPU?]
    /// The headwear part's atlas rectangle (0...1) per pose, nil when the pose has none.
    let petals: [SIMD4<Float>?]

    init(pack: CharacterPack, poses: [CharacterPoseGPU?]) {
        self.pack = pack
        self.poses = poses
        petals = pack.poses.map { CharacterAssets.petalRect($0.rig) }
    }
}

/// Packs and atlases shared by every `character` orb (DESIGN 2a.6). Packs load off the main thread once per folder and
/// pack version. Atlases are decoded at the scale they are drawn at (`CharacterAtlasScale`), kept while some orb asks
/// for them each frame and released two seconds after the last request, so only the poses on screen stay resident.
@MainActor
final class CharacterAssets {
    static let shared = CharacterAssets()

    private struct PackKey: Hashable {
        let folder: String
        let version: Int
    }

    private enum PackState {
        case loading
        case ready(LoadedCharacter)
        case failed
    }

    private struct AtlasKey: Hashable {
        let file: String
        let step: Int
    }

    private final class AtlasEntry {
        var texture: MTLTexture?
        var lastUsed: TimeInterval
        var failed = false

        init(lastUsed: TimeInterval) {
            self.lastUsed = lastUsed
        }
    }

    private var packs: [PackKey: PackState] = [:]
    private var atlases: [AtlasKey: AtlasEntry] = [:]
    private var lastSweep: TimeInterval = 0
    /// Loads on the calling thread instead (offscreen recordings, which draw every frame synchronously).
    var synchronous = false
    /// Called on the main actor when a pack or an atlas finishes loading (to draw a frame).
    var onLoad: [UUID: @MainActor () -> Void] = [:]

    /// The pack in `installed`, or nil while it loads (the load starts on the first call) or when it is unreadable.
    func pack(_ installed: InstalledCharacter) -> LoadedCharacter? {
        let key = PackKey(folder: installed.folder.path, version: installed.manifest.packVersion)
        switch packs[key] {
        case .ready(let loaded): return loaded
        case .loading, .failed: return nil
        case nil: break
        }
        packs[key] = .loading
        let folder = installed.folder
        if synchronous {
            let pack = try? CharacterPack.load(from: folder)
            let loaded = pack.map { pack in
                LoadedCharacter(pack: pack, poses: pack.poses.map { pose in
                    OrbGPU.shared.flatMap { CharacterPoseGPU(rig: pose.rig, device: $0.device) }
                })
            }
            packs[key] = loaded.map { .ready($0) } ?? .failed
            return loaded
        }
        Task.detached(priority: .userInitiated) {
            let loaded: LoadedCharacter?
            if let pack = try? CharacterPack.load(from: folder) {
                let device = OrbGPU.shared?.device
                let poses = pack.poses.map { pose in device.flatMap { CharacterPoseGPU(rig: pose.rig, device: $0) } }
                loaded = LoadedCharacter(pack: pack, poses: poses)
            } else {
                loaded = nil
            }
            await MainActor.run {
                let assets = CharacterAssets.shared
                assets.packs[key] = loaded.map { .ready($0) } ?? .failed
                assets.notify()
            }
        }
        return nil
    }

    /// The atlas of `pose` decoded for `scale` (texture pixels per atlas pixel), or nil while it loads.
    func atlas(_ pose: CharacterPose, scale: Double, now: TimeInterval) -> MTLTexture? {
        let step = CharacterAtlasScale.step(for: scale)
        let key = AtlasKey(file: pose.atlasURL.path, step: step)
        if let entry = atlases[key] {
            entry.lastUsed = now
            return entry.texture
        }
        let entry = AtlasEntry(lastUsed: now)
        atlases[key] = entry
        let url = pose.atlasURL
        let factor = CharacterAtlasScale.factor(step: step)
        if synchronous {
            entry.texture = CharacterAtlasLoader.load(url, factor: factor)
            entry.failed = entry.texture == nil
            return entry.texture
        }
        Task.detached(priority: .userInitiated) {
            let texture = CharacterAtlasLoader.load(url, factor: factor).map(TextureBox.init)
            await MainActor.run {
                let assets = CharacterAssets.shared
                guard let entry = assets.atlases[key] else { return }
                entry.texture = texture?.texture
                entry.failed = texture == nil
                assets.notify()
            }
        }
        return nil
    }

    private struct TextureBox: @unchecked Sendable {
        let texture: MTLTexture
    }

    /// Releases atlases nobody asked for in the last two seconds.
    func sweep(now: TimeInterval) {
        guard now - lastSweep > 1 else { return }
        lastSweep = now
        atlases = atlases.filter { now - $0.value.lastUsed < 2 || ($0.value.texture == nil && !$0.value.failed) }
    }

    /// Bytes of the resident atlases (mipmaps included), for the memory budget.
    var residentBytes: Int {
        atlases.values.reduce(0) { total, entry in
            guard let t = entry.texture else { return total }
            return total + t.width * t.height * 4 * 4 / 3
        }
    }

    private func notify() {
        for handler in onLoad.values { handler() }
    }

    /// The headwear part's square atlas rectangle (the reference's petal sprite), 0...1.
    nonisolated static func petalRect(_ rig: CharacterRig) -> SIMD4<Float>? {
        guard let part = rig.parts.first(where: { $0.tag == "headwear" }), part.uv.count >= 2 else { return nil }
        var minX = Double.infinity, minY = Double.infinity, maxX = -Double.infinity
        var i = 0
        while i + 1 < part.uv.count {
            minX = min(minX, part.uv[i])
            maxX = max(maxX, part.uv[i])
            minY = min(minY, part.uv[i + 1])
            i += 2
        }
        let side = maxX - minX
        let w = Double(max(1, rig.atlasSize.width)), h = Double(max(1, rig.atlasSize.height))
        guard side > 1 else { return nil }
        return SIMD4(Float(minX / w), Float(minY / h), Float(maxX / w), Float((minY + side) / h))
    }
}

/// Atlas resolution steps: textures are decoded at √2 steps of the drawn scale (at most 1 ×, at least 1/16 ×), so a
/// small size change reuses the texture and a big one decodes a sharper or lighter one.
enum CharacterAtlasScale {
    /// The step for `scale` texture pixels per atlas pixel, rounded up (sharper).
    static func step(for scale: Double) -> Int {
        let clamped = min(1, max(1.0 / 16, scale))
        return Int((log2(clamped) * 2).rounded(.up))
    }

    static func factor(step: Int) -> Double {
        min(1, pow(2, Double(step) / 2))
    }

    /// Texture pixels per atlas pixel for a pose drawn at `pointsPerPixel` screen points per pose pixel on a
    /// `backingScale` display: the drawn resolution with a little headroom for hover and hops.
    static func needed(pointsPerPixel: Double, backingScale: Double) -> Double {
        pointsPerPixel * backingScale * 1.15
    }
}

/// Decodes a straight-alpha atlas PNG at a reduced size into a premultiplied, mipmapped texture.
enum CharacterAtlasLoader {
    static func load(_ url: URL, factor: Double) -> MTLTexture? {
        guard let gpu = OrbGPU.shared, let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let properties = CGImageSourceCopyPropertiesAtIndex(source, 0, nil) as? [CFString: Any],
              let width = properties[kCGImagePropertyPixelWidth] as? Int,
              let height = properties[kCGImagePropertyPixelHeight] as? Int else { return nil }
        let image: CGImage?
        if factor >= 0.999 {
            image = CGImageSourceCreateImageAtIndex(source, 0, nil)
        } else {
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceThumbnailMaxPixelSize: max(1, Int((Double(max(width, height)) * factor).rounded())),
                kCGImageSourceCreateThumbnailWithTransform: false,
                kCGImageSourceShouldCacheImmediately: true,
            ]
            image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary)
        }
        guard let image else { return nil }
        let w = image.width, h = image.height
        guard let context = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                      space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue),
              let data = context.data else { return nil }
        context.interpolationQuality = .high
        context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: w, height: h, mipmapped: true)
        descriptor.usage = [.shaderRead]
        guard let texture = gpu.device.makeTexture(descriptor: descriptor) else { return nil }
        texture.replace(region: MTLRegionMake2D(0, 0, w, h), mipmapLevel: 0, withBytes: data, bytesPerRow: w * 4)
        guard let commands = gpu.queue.makeCommandBuffer(), let blit = commands.makeBlitCommandEncoder() else { return nil }
        blit.generateMipmaps(for: texture)
        blit.endEncoding()
        commands.commit()
        commands.waitUntilCompleted()
        return texture
    }
}
