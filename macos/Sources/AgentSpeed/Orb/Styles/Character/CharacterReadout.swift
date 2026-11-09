import AgentSpeedCharacter
import AppKit
import simd

/// Sizes of the `character` readouts for a disc of `D` points (DESIGN 2a.6), with floors so they stay legible on small
/// orbs. `q` is points per reference unit of the speed body (the reference draws its digits 92 units tall).
struct CharacterReadoutMetrics {
    let value: CGFloat
    let unit: CGFloat
    let title: CGFloat
    let model: CGFloat
    let info: CGFloat
    let peak: CGFloat
    var q: CGFloat { value / 92 }

    init(disc d: CGFloat) {
        value = max(11, 0.068 * d)
        unit = max(7.5, 0.022 * d)
        title = max(7.5, 0.021 * d)
        model = max(8, 0.024 * d)
        info = max(7.5, 0.02 * d)
        peak = max(8, 0.022 * d)
    }

    /// Room for the outline and the flash glow around each digit.
    var digitStyle: OrbTextStyle {
        OrbTextStyle(size: value, weight: .bold, monospacedDigits: true, tracking: 0, padding: (0.2 * value).rounded(.up) + 1)
    }
    var unitStyle: OrbTextStyle { OrbTextStyle(size: unit, weight: .medium, tracking: 0, padding: (0.25 * unit).rounded(.up) + 1) }
}

/// Draws one floating readout body: its pieces are laid out around the body center, scaled by the body's depth scale
/// and turned by its tumble, so glyphs rotate with the body (DESIGN 2a, Techniques).
@MainActor
struct CharacterBodyPainter {
    let frame: OrbFrame
    let center: CGPoint
    let scale: CGFloat
    let spin: Double
    let alpha: Double
    let metrics: CharacterReadoutMetrics
    let contrast: Bool

    var language: AppLanguage { frame.content.language }

    /// A local offset (unscaled, unrotated) to a canvas point.
    func point(_ ox: CGFloat, _ oy: CGFloat) -> CGPoint {
        let c = CGFloat(cos(spin)), s = CGFloat(sin(spin))
        let x = ox * scale, y = oy * scale
        return CGPoint(x: center.x + x * c - y * s, y: center.y + x * s + y * c)
    }

    func text(_ texture: OrbTextTexture, centerX ox: CGFloat, baseline oy: CGFloat, color: RGBColor,
              glow: RGBColor? = nil, glowRadius: CGFloat = 0, clip: OrbTextClip = .none) -> OrbTextDraw {
        var draw = OrbTextDraw(texture, x: center.x + ox - texture.advance / 2, baseline: center.y + oy, color: color,
                               glow: glow, glowRadius: glowRadius * scale, scale: scale, pivot: center, clip: clip)
        if spin != 0 { draw = draw.rotated(spin, about: center) }
        return draw
    }

    func texture(_ string: String, size: CGFloat, weight: NSFont.Weight, scene: OrbScene) -> OrbTextTexture? {
        OrbTextCache.shared.texture(string, style: OrbTextStyle(size: size, weight: weight, tracking: 0, padding: 1),
                                    language: language, scale: scene.backingScale * max(1, scale))
    }

    /// Half the size of `chip(_:size:...)` for `string` (no dot), unscaled; nil when it cannot be drawn.
    static func chipHalfSize(_ string: String, size: CGFloat, language: AppLanguage, scale: CGFloat) -> CGSize? {
        let style = OrbTextStyle(size: size, weight: .semibold, tracking: 0, padding: 1)
        guard let texture = OrbTextCache.shared.texture(string, style: style, language: language, scale: scale) else { return nil }
        return CGSize(width: texture.advance / 2 + 0.7 * size, height: OrbTextCache.shared.capHeight(style) / 2 + 0.55 * size)
    }

    /// A glass chip centered at the local offset: rim, fill, an optional leading dot, then the text.
    func chip(_ string: String, size: CGFloat, at oy: CGFloat, text color: RGBColor, rim: RGBColor, dot: RGBColor? = nil,
              into scene: inout OrbScene) -> CGFloat {
        guard let texture = texture(string, size: size, weight: .semibold, scene: scene) else { return 0 }
        let capHeight = OrbTextCache.shared.capHeight(OrbTextStyle(size: size, weight: .semibold, tracking: 0, padding: 1))
        let dotSpace = dot == nil ? 0 : size * 0.85
        let half = CGSize(width: (texture.advance + dotSpace) / 2 + 0.7 * size, height: capHeight / 2 + 0.55 * size)
        let fill = frame.dark ? RGBColor(red: 16 / 255, green: 18 / 255, blue: 26 / 255).opacity(contrast ? 0.9 : 0.6)
            : RGBColor(red: 1, green: 1, blue: 1).opacity(contrast ? 0.95 : 0.78)
        let c = point(0, oy)
        scene.add(.roundedRect(center: c, halfSize: CGSize(width: (half.width + 0.6) * scale, height: (half.height + 0.6) * scale),
                               rotation: spin, corner: (half.height + 0.6) * scale, color: rim.opacity(alpha)), blend: .over)
        scene.add(.roundedRect(center: c, halfSize: CGSize(width: half.width * scale, height: half.height * scale),
                               rotation: spin, corner: half.height * scale, color: fill.opacity(alpha)), blend: .over)
        var textX: CGFloat = 0
        if let dot {
            let dx = -half.width + 0.7 * size + size * 0.28
            scene.add(.circle(center: point(dx, oy), radius: size * 0.28 * scale, color: dot.opacity(alpha)), blend: .over)
            textX = dotSpace / 2
        }
        scene.add(text(texture, centerX: textX, baseline: oy + capHeight / 2, color: color.opacity(alpha)))
        return half.height * 2
    }
}

/// The ink of the floating speed (DESIGN 2a.6): digits with an outline in the opposite tone and a soft halo, so they
/// read over her dress and any wallpaper without a plate; a changed digit's halo flares electric with the lightning.
struct CharacterInk {
    let dark: Bool
    let contrast: Bool

    init(_ frame: OrbFrame, contrast: Bool) {
        dark = frame.dark
        self.contrast = contrast
    }

    var outline: RGBColor {
        dark ? RGBColor(red: 0.02, green: 0.025, blue: 0.045).opacity(contrast ? 1 : 0.9)
            : RGBColor(red: 1, green: 1, blue: 1).opacity(contrast ? 1 : 0.95)
    }

    var shadow: RGBColor { dark ? RGBColor(red: 0, green: 0, blue: 0.02) : RGBColor(red: 1, green: 1, blue: 1) }
    var shadowAlpha: Double { contrast ? 0.6 : 0.45 }

    /// Outline width for text of `size` points.
    func width(_ size: CGFloat) -> CGFloat { size * (contrast ? 0.085 : 0.06) }

    private var electric: RGBColor {
        dark ? RGBColor(red: 0.55, green: 0.82, blue: 1) : RGBColor(red: 0.25, green: 0.5, blue: 1)
    }

    /// The digit's halo: the soft shadow at rest, flaring to an electric glow (wider) while it flashes.
    func halo(flash: Double, heat: RGBColor) -> (color: RGBColor, alpha: Double, radius: CGFloat) {
        let k = min(max(flash, 0), 1)
        let e = electric.mix(heat, 0.2)
        let color = shadow.mix(e, k)
        return (color, max(shadowAlpha * (1 - k), 0.9 * k), 0.1 + 0.08 * CGFloat(k))
    }

    /// Bolt colors: an electric glow with a white-hot core on dark glass; deeper blues over light wallpapers.
    func bolt(heat: RGBColor) -> (glow: RGBColor, core: RGBColor) {
        dark ? (electric.mix(heat, 0.2), RGBColor(red: 0.95, green: 0.98, blue: 1))
            : (RGBColor(red: 0.3, green: 0.55, blue: 1), RGBColor(red: 0.12, green: 0.3, blue: 0.95))
    }
}
