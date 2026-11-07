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

    var digitStyle: OrbTextStyle { OrbTextStyle(size: value, weight: .bold, monospacedDigits: true, tracking: 0, padding: 2) }
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
              clip: OrbTextClip = .none) -> OrbTextDraw {
        var draw = OrbTextDraw(texture, x: center.x + ox - texture.advance / 2, baseline: center.y + oy, color: color,
                               scale: scale, pivot: center, clip: clip)
        if spin != 0 { draw = draw.rotated(spin, about: center) }
        return draw
    }

    func texture(_ string: String, size: CGFloat, weight: NSFont.Weight, scene: OrbScene) -> OrbTextTexture? {
        OrbTextCache.shared.texture(string, style: OrbTextStyle(size: size, weight: weight, tracking: 0, padding: 1),
                                    language: language, scale: scene.backingScale * max(1, scale))
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
