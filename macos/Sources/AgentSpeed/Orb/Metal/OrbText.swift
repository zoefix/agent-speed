import AppKit
import CoreText
import Metal

/// A string rasterized once (white, premultiplied) for drawing as a tinted quad.
final class OrbTextTexture {
    let texture: MTLTexture
    /// Quad size in points, including padding.
    let size: CGSize
    /// Distance from the top of the quad to the baseline, in points.
    let baseline: CGFloat
    /// Horizontal advance of the text itself, in points.
    let advance: CGFloat
    let padding: CGFloat

    init(texture: MTLTexture, size: CGSize, baseline: CGFloat, advance: CGFloat, padding: CGFloat) {
        self.texture = texture
        self.size = size
        self.baseline = baseline
        self.advance = advance
        self.padding = padding
    }
}

struct OrbTextStyle: Hashable {
    var size: CGFloat
    var weight: NSFont.Weight
    var monospacedDigits = false
    var tracking: CGFloat = 0
    /// Room around the glyphs for a glow, in points.
    var padding: CGFloat = 1

    func font() -> NSFont {
        monospacedDigits
            ? .monospacedDigitSystemFont(ofSize: size, weight: weight)
            : .systemFont(ofSize: size, weight: weight)
    }
}

/// Rasterizes strings with Core Text into textures and keeps them, shared by every orb so equal sizes share glyphs
/// (digits, the unit and the current titles are built once per size).
@MainActor
final class OrbTextCache {
    static let shared = OrbTextCache()

    private struct Key: Hashable {
        let text: String
        let style: OrbTextStyle
        let language: AppLanguage
        let scale: CGFloat
    }

    private static let limit = 512
    private var entries: [Key: OrbTextTexture] = [:]
    private var overflowLabels: [String] = (0...99).map { "+\($0)" }

    func texture(_ text: String, style: OrbTextStyle, language: AppLanguage, scale: CGFloat) -> OrbTextTexture? {
        let key = Key(text: text, style: style, language: language, scale: scale)
        if let cached = entries[key] { return cached }
        guard let gpu = OrbGPU.shared, let texture = Self.rasterize(text, style: style, language: language, scale: scale, device: gpu.device) else { return nil }
        if entries.count >= Self.limit { entries.removeAll(keepingCapacity: true) }
        entries[key] = texture
        return texture
    }

    func texture(_ character: Character, style: OrbTextStyle, language: AppLanguage, scale: CGFloat) -> OrbTextTexture? {
        texture(String(character), style: style, language: language, scale: scale)
    }

    /// Cap height of a style's font, measured once (creating a font allocates).
    func capHeight(_ style: OrbTextStyle) -> CGFloat {
        if let cached = capHeights[style] { return cached }
        let value = style.font().capHeight
        capHeights[style] = value
        return value
    }

    private var capHeights: [OrbTextStyle: CGFloat] = [:]

    /// The traffic readout's arrow (DESIGN 10): a vector glyph 0.50 t wide and 0.66 t tall on the baseline, stroke
    /// `max(0.9 pt, 0.11 t)` with round caps, pointing down (download) or up (upload).
    func arrowTexture(down: Bool, size t: CGFloat, scale: CGFloat) -> OrbTextTexture? {
        let key = ArrowKey(down: down, size: t, scale: scale)
        if let cached = arrows[key] { return cached }
        guard let gpu = OrbGPU.shared, let texture = Self.rasterizeArrow(down: down, size: t, scale: scale, device: gpu.device) else {
            return nil
        }
        if arrows.count >= 64 { arrows.removeAll(keepingCapacity: true) }
        arrows[key] = texture
        return texture
    }

    private struct ArrowKey: Hashable {
        let down: Bool
        let size: CGFloat
        let scale: CGFloat
    }

    private var arrows: [ArrowKey: OrbTextTexture] = [:]

    private static func rasterizeArrow(down: Bool, size t: CGFloat, scale: CGFloat, device: MTLDevice) -> OrbTextTexture? {
        let width = 0.50 * t
        let height = 0.66 * t
        let stroke = max(0.9, 0.11 * t)
        let pad = ceil(stroke)
        let size = CGSize(width: ceil(width + pad * 2), height: ceil(height + pad * 2))
        let pixelsWide = max(1, Int(ceil(size.width * scale)))
        let pixelsHigh = max(1, Int(ceil(size.height * scale)))
        guard let context = CGContext(
            data: nil, width: pixelsWide, height: pixelsHigh, bitsPerComponent: 8, bytesPerRow: pixelsWide * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.scaleBy(x: scale, y: scale)
        // Core Graphics is y-up: the glyph box spans pad...pad + height.
        let inset = stroke / 2
        let midX = pad + width / 2
        let bottom = pad + inset
        let top = pad + height - inset
        let head = min(width / 2 - inset, 0.26 * t)
        let path = CGMutablePath()
        path.move(to: CGPoint(x: midX, y: bottom))
        path.addLine(to: CGPoint(x: midX, y: top))
        let tip = down ? bottom : top
        let back = down ? tip + head : tip - head
        path.move(to: CGPoint(x: midX - head, y: back))
        path.addLine(to: CGPoint(x: midX, y: tip))
        path.addLine(to: CGPoint(x: midX + head, y: back))
        context.addPath(path)
        context.setStrokeColor(NSColor.white.cgColor)
        context.setLineWidth(stroke)
        context.setLineCap(.round)
        context.setLineJoin(.round)
        context.strokePath()

        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: pixelsWide, height: pixelsHigh, mipmapped: false)
        descriptor.usage = [.shaderRead]
        guard let texture = device.makeTexture(descriptor: descriptor), let data = context.data else { return nil }
        texture.replace(region: MTLRegionMake2D(0, 0, pixelsWide, pixelsHigh), mipmapLevel: 0, withBytes: data, bytesPerRow: pixelsWide * 4)
        return OrbTextTexture(
            texture: texture,
            size: CGSize(width: CGFloat(pixelsWide) / scale, height: CGFloat(pixelsHigh) / scale),
            baseline: CGFloat(pixelsHigh) / scale - pad,
            advance: width,
            padding: pad
        )
    }

    /// The `+n` label of the agent dots.
    func overflowTexture(_ count: Int, style: OrbTextStyle, language: AppLanguage, scale: CGFloat) -> OrbTextTexture? {
        texture(overflowLabels[min(max(count, 0), 99)], style: style, language: language, scale: scale)
    }

    private static func rasterize(_ text: String, style: OrbTextStyle, language: AppLanguage, scale: CGFloat, device: MTLDevice) -> OrbTextTexture? {
        let attributes: [NSAttributedString.Key: Any] = [
            .font: style.font(),
            .kern: style.tracking,
            .foregroundColor: NSColor.white,
            NSAttributedString.Key(kCTLanguageAttributeName as String): language.rawValue,
        ]
        let line = CTLineCreateWithAttributedString(NSAttributedString(string: text, attributes: attributes))
        var ascent: CGFloat = 0
        var descent: CGFloat = 0
        var leading: CGFloat = 0
        let width = CGFloat(CTLineGetTypographicBounds(line, &ascent, &descent, &leading))
        let pad = style.padding
        let size = CGSize(width: ceil(width + pad * 2), height: ceil(ascent + descent + pad * 2))
        let pixelsWide = max(1, Int(ceil(size.width * scale)))
        let pixelsHigh = max(1, Int(ceil(size.height * scale)))
        guard let context = CGContext(
            data: nil, width: pixelsWide, height: pixelsHigh, bitsPerComponent: 8, bytesPerRow: pixelsWide * 4,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.scaleBy(x: scale, y: scale)
        context.setShouldSmoothFonts(false)
        context.textPosition = CGPoint(x: pad, y: pad + descent)
        CTLineDraw(line, context)

        let descriptor = MTLTextureDescriptor.texture2DDescriptor(pixelFormat: .rgba8Unorm, width: pixelsWide, height: pixelsHigh, mipmapped: false)
        descriptor.usage = [.shaderRead]
        guard let texture = device.makeTexture(descriptor: descriptor), let data = context.data else { return nil }
        texture.replace(region: MTLRegionMake2D(0, 0, pixelsWide, pixelsHigh), mipmapLevel: 0, withBytes: data, bytesPerRow: pixelsWide * 4)
        return OrbTextTexture(
            texture: texture,
            size: CGSize(width: CGFloat(pixelsWide) / scale, height: CGFloat(pixelsHigh) / scale),
            baseline: pad + ascent,
            advance: width,
            padding: pad
        )
    }
}

/// One text quad (layout matches `TextInstance` in `OrbShaderSource`).
struct OrbTextInstance {
    /// x, y (top-left, points), width, height.
    var rect: SIMD4<Float>
    var color: SIMD4<Float>
    /// Glow color; alpha is the glow strength.
    var glow: SIMD4<Float>
    /// Shear factor, shear pivot y, glow radius (points), clip mode (`OrbTextClip`).
    var params: SIMD4<Float>
    /// Clip rect: min x, min y, max x, max y (points); a band: center x, center y, half width, slope.
    var clip: SIMD4<Float>
    /// Bottom color of a vertical gradient fill (when `gradient.z` is 1); the outline color (when it is 3).
    var fill2: SIMD4<Float> = .zero
    /// Gradient top y, bottom y (points), mode (1 gradient, 2 `led` backing, 3 outline), quad inset (points).
    var gradient: SIMD4<Float> = .zero
    /// Rotation (radians, clockwise on screen) about a pivot x, y (points); 0 for upright text. Clips stay unrotated.
    var rotation: SIMD4<Float> = .zero
}

/// Where a text quad may draw.
enum OrbTextClip: Equatable {
    case none
    case rect(CGRect)
    /// Only above (`below == false`) or below the liquid surface, inside `rect`.
    case liquid(below: Bool, rect: CGRect)
    /// Fading out linearly over `halfWidth` either side of a line through `center` with `slope` (dx per dy): a sheen
    /// or light band crossing the glyphs.
    case band(center: CGPoint, halfWidth: CGFloat, slope: CGFloat)

    var mode: Float {
        switch self {
        case .none: 0
        case .rect: 1
        case .liquid(let below, _): below ? 3 : 2
        case .band: 4
        }
    }

    var rect: SIMD4<Float> {
        switch self {
        case .none: SIMD4(-1e6, -1e6, 1e6, 1e6)
        case .rect(let rect), .liquid(_, let rect):
            SIMD4(Float(rect.minX), Float(rect.minY), Float(rect.maxX), Float(rect.maxY))
        case .band(let center, let halfWidth, let slope):
            SIMD4(Float(center.x), Float(center.y), Float(halfWidth), Float(slope))
        }
    }
}

struct OrbTextDraw {
    let texture: OrbTextTexture
    var instance: OrbTextInstance
    /// Depth layer in the scene (`OrbScene.layer`): texts draw after the shapes of their own layer.
    var layer = 0

    /// The same text turned by `angle` radians (clockwise on screen) about `pivot`.
    func rotated(_ angle: Double, about pivot: CGPoint) -> OrbTextDraw {
        var copy = self
        copy.instance.rotation = SIMD4(Float(angle), Float(pivot.x), Float(pivot.y), 0)
        return copy
    }

    /// Places the text with its baseline at `baseline` and its advance starting at `x`.
    init(_ texture: OrbTextTexture, x: CGFloat, baseline: CGFloat, color: RGBColor, glow: RGBColor? = nil,
         glowRadius: CGFloat = 0, shear: CGFloat = 0, shearPivot: CGFloat = 0, scale: CGFloat = 1, pivot: CGPoint? = nil,
         clip: OrbTextClip = .none) {
        self.texture = texture
        var origin = CGPoint(x: x - texture.padding, y: baseline - texture.baseline)
        var size = texture.size
        if scale != 1, let pivot {
            origin = CGPoint(x: pivot.x + (origin.x - pivot.x) * scale, y: pivot.y + (origin.y - pivot.y) * scale)
            size = CGSize(width: size.width * scale, height: size.height * scale)
        }
        instance = OrbTextInstance(
            rect: SIMD4(Float(origin.x), Float(origin.y), Float(size.width), Float(size.height)),
            color: color.simd,
            glow: glow?.simd ?? .zero,
            params: SIMD4(Float(shear), Float(shearPivot), Float(glowRadius), clip.mode),
            clip: clip.rect
        )
    }

    /// The `led` value's underlays in the glyph's own pass: a black backing blurred over `radius` points (sharing the
    /// glow's samples) at `alpha`, and the chromatic split (#FF3C8C left, #3CD2FF right) `offset` points either side
    /// at `chroma`.
    func ledUnderlays(backing alpha: Double, radius: CGFloat, chroma: Double, offset: CGFloat) -> OrbTextDraw {
        var copy = self
        let glowRadius = CGFloat(copy.instance.params.z)
        if copy.instance.glow.w <= 0 || glowRadius <= 0 {
            copy.instance.params.z = Float(radius)
            copy.instance.gradient.x = 1
        } else {
            // Shared taps sit on the glow's rings: weighting them for a blur of 1.4 × the radius matches a blur pass of
            // `radius` of its own.
            copy.instance.gradient.x = Float(min(1, 1.4 * radius / glowRadius))
        }
        copy.instance.gradient.z = 2
        copy.instance.fill2 = SIMD4(Float(offset), Float(chroma.clamped(to: 0...1)), 0, Float(alpha.clamped(to: 0...1)))
        return copy
    }

    /// An outline `width` points wide in `color` under the glyph and over its glow (the `character` speed). The texture
    /// needs a padding of at least the width plus the glow radius.
    func outlined(_ color: RGBColor, width: CGFloat) -> OrbTextDraw {
        var copy = self
        copy.instance.gradient.x = Float(max(0, width))
        copy.instance.gradient.z = 3
        copy.instance.fill2 = color.simd
        return copy
    }

    /// Skips the outer `points` of the quad (where a glow of a smaller radius than the padding draws nothing).
    func inset(_ points: CGFloat) -> OrbTextDraw {
        var copy = self
        copy.instance.gradient.w = Float(max(0, points))
        return copy
    }

    /// Centers the text horizontally on `centerX`.
    init(_ texture: OrbTextTexture, centerX: CGFloat, baseline: CGFloat, color: RGBColor, glow: RGBColor? = nil,
         glowRadius: CGFloat = 0, scale: CGFloat = 1, pivot: CGPoint? = nil, clip: OrbTextClip = .none) {
        self.init(texture, x: centerX - texture.advance / 2, baseline: baseline, color: color, glow: glow,
                  glowRadius: glowRadius, scale: scale, pivot: pivot ?? (scale == 1 ? nil : CGPoint(x: centerX, y: baseline)),
                  clip: clip)
    }
}
