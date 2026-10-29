import AppKit
import Metal

/// The provisional mark under a provisional score on the orb readout (DESIGN 12, *Provisional mark*): the dotted underline
/// rasterized once per width, size and contrast into a white texture that the readout tints like its digits, so it
/// shares their opacity, clip and shadow, takes no layout space and is drawn in the content layer.
@MainActor
final class OrbLineProvisionalMark {
    static let shared = OrbLineProvisionalMark()

    private struct Key: Hashable {
        let width: CGFloat
        let textSize: CGFloat
        let scale: CGFloat
        let increasedContrast: Bool
    }

    private var entries: [Key: OrbTextTexture] = [:]

    /// The dots' top under a number set at `textSize` on `baseline`: centred 0.22 × the size below it, on a device pixel
    /// so the small dots stay crisp.
    static func top(baseline: CGFloat, textSize: CGFloat, scale: CGFloat, increasedContrast: Bool) -> CGFloat {
        let diameter = ProvisionalMark.dotDiameter(textSize: textSize, scale: scale, increasedContrast: increasedContrast)
        return ((baseline + ProvisionalMark.textDepth * textSize - diameter / 2) * scale).rounded() / scale
    }

    /// The underline of a number `width` wide. Drawn with `OrbTextDraw(x: the number's left edge, baseline: top(…))`.
    func texture(width: CGFloat, textSize: CGFloat, scale: CGFloat, increasedContrast: Bool) -> OrbTextTexture? {
        let key = Key(width: (width * scale).rounded() / scale, textSize: textSize, scale: scale, increasedContrast: increasedContrast)
        if let cached = entries[key] { return cached }
        guard let device = OrbGPU.shared?.device,
              let texture = Self.rasterize(key.width, textSize: textSize, scale: scale, increasedContrast: increasedContrast,
                                           device: device) else { return nil }
        if entries.count >= 64 { entries.removeAll(keepingCapacity: true) }
        entries[key] = texture
        return texture
    }

    private static func rasterize(_ width: CGFloat, textSize: CGFloat, scale: CGFloat, increasedContrast: Bool,
                                  device: MTLDevice) -> OrbTextTexture? {
        let diameter = ProvisionalMark.dotDiameter(textSize: textSize, scale: scale, increasedContrast: increasedContrast)
        // The digits' padding, on whole pixels, leaves room for the liquid's shadow.
        let pad = ceil(max(1, 0.03 * textSize) * scale) / scale
        let pixelsWide = max(1, Int(ceil((width + 2 * pad) * scale)))
        let pixelsHigh = max(1, Int(ceil((diameter + 2 * pad) * scale)))
        guard let context = CGContext(
            data: nil, width: pixelsWide, height: pixelsHigh, bitsPerComponent: 8, bytesPerRow: pixelsWide * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.scaleBy(x: scale, y: scale)
        // Core Graphics is y-up: put the dots' centres `pad + d / 2` from the bottom.
        let origin = CGPoint(x: pad, y: pad + diameter / 2 + ProvisionalMark.textDepth * textSize)
        ProvisionalMark.drawUnderline(in: context, origin: origin, width: width, textSize: textSize, color: NSColor.white.cgColor,
                                scale: scale, increasedContrast: increasedContrast, yDown: false)

        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: pixelsWide, height: pixelsHigh,
                                                                  mipmapped: false)
        descriptor.usage = [.shaderRead]
        guard let texture = device.makeTexture(descriptor: descriptor), let data = context.data else { return nil }
        texture.replace(region: MTLRegionMake2D(0, 0, pixelsWide, pixelsHigh), mipmapLevel: 0, withBytes: data,
                        bytesPerRow: pixelsWide * 4)
        return OrbTextTexture(texture: texture, size: CGSize(width: CGFloat(pixelsWide) / scale, height: CGFloat(pixelsHigh) / scale),
                              baseline: pad, advance: width, padding: pad)
    }
}
