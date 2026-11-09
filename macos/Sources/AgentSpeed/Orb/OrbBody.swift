import AgentSpeedOrbModel
import SwiftUI

/// The static smoked-glass (or porcelain) disc: drop shadow, body gradient, inner shadow, lens and rim
/// (DESIGN section 2, layer 2), with the per-style tokens of 2a. Only redraws when its inputs change.
struct OrbBody: View, Equatable {
    let diameter: CGFloat
    let canvasSide: CGFloat
    let tokens: OrbTokens
    /// The user's opacity setting (0.4...1), applied to the body tint.
    let opacity: Double
    /// No desktop blur behind (Reduce Transparency, embedded, previews): draw an opaque body.
    let solid: Bool
    let increaseContrast: Bool
    var style: OrbStyleKind = .led
    /// False while the source bezel replaces the white rim highlight (DESIGN 2b).
    var rim = true
    /// Drawn at the hover scale (the floating orb's 1.03).
    var scale: CGFloat = 1

    nonisolated static func == (lhs: OrbBody, rhs: OrbBody) -> Bool {
        lhs.diameter == rhs.diameter && lhs.canvasSide == rhs.canvasSide && lhs.tokens.isDark == rhs.tokens.isDark
            && lhs.opacity == rhs.opacity && lhs.solid == rhs.solid && lhs.increaseContrast == rhs.increaseContrast
            && lhs.style == rhs.style && lhs.rim == rhs.rim && lhs.scale == rhs.scale
    }

    private var gradient: (RGBColor, RGBColor) {
        switch (style, tokens.isDark) {
        case (.liquid, true): (RGBColor(hex: 0x181D2B), RGBColor(hex: 0x07090D))
        case (.liquid, false): (RGBColor(hex: 0xFFFFFF), RGBColor(hex: 0xE4E8F0))
        case (.reactor, true): (RGBColor(hex: 0x141824), RGBColor(hex: 0x06070B))
        case (.reactor, false): (tokens.bodyTop, RGBColor(hex: 0xE5E9F1))
        default: (tokens.bodyTop, tokens.bodyBottom)
        }
    }

    private var hasLens: Bool { style == .led || style == .needle || style == .reactor }

    var body: some View {
        if style == .character {
            // The character stands on the desktop without a glass body (DESIGN 2a.6).
            Color.clear.frame(width: canvasSide, height: canvasSide)
        } else {
            disc
        }
    }

    private var disc: some View {
        let s = diameter
        let (top, bottom) = gradient
        let bodyOpacity = solid ? 1 : tokens.bodyOpacity * opacity
        return ZStack {
            shadow
            Circle()
                .fill(LinearGradient(colors: [top.color, bottom.color], startPoint: .top, endPoint: .bottom))
                .opacity(bodyOpacity)
                .frame(width: s, height: s)
            Circle()
                .strokeBorder(tokens.innerShadow.color, lineWidth: s * 0.05)
                .blur(radius: s * 0.03)
                .frame(width: s, height: s)
                .clipShape(Circle())
            if style == .liquid {
                // Volume shading: clear to 75 % of the radius around a point up-left of the center, then darker.
                Circle()
                    .fill(RadialGradient(
                        stops: [
                            .init(color: .clear, location: 0),
                            .init(color: .clear, location: 0.75),
                            .init(color: tokens.isDark ? Color.black.opacity(0.42) : RGBColor(hex: 0x1E283C, alpha: 0.12).color, location: 1),
                        ],
                        center: UnitPoint(x: 0.5 - 0.12, y: 0.5 - 0.16),
                        startRadius: 0,
                        endRadius: s * 0.5
                    ))
                    .frame(width: s, height: s)
                    .clipShape(Circle())
            }
            if hasLens {
                Circle()
                    .fill(RadialGradient(
                        colors: [tokens.lens.color, tokens.lens.color.opacity(0)],
                        center: UnitPoint(x: 0.3, y: 0.2),
                        startRadius: 0,
                        endRadius: s * 0.55
                    ))
                    .frame(width: s, height: s)
            }
            if rim {
                Circle()
                    .strokeBorder(
                        LinearGradient(
                            colors: [rimTop.color, rimTop.color.opacity(0)],
                            startPoint: .top,
                            endPoint: UnitPoint(x: 0.5, y: 0.75)
                        ),
                        lineWidth: 1
                    )
                    .frame(width: s, height: s)
            }
            if let stroke = tokens.outerStroke {
                Circle()
                    .stroke(stroke.opacity(increaseContrast ? 2.2 : 1).color, lineWidth: increaseContrast ? 1 : 0.5)
                    .frame(width: s, height: s)
            }
        }
        .frame(width: canvasSide, height: canvasSide)
        .scaleEffect(scale)
        .allowsHitTesting(false)
    }

    private var rimTop: RGBColor {
        increaseContrast ? tokens.rimTop.opacity(tokens.isDark ? 2.2 : 1) : tokens.rimTop
    }

    /// The drop shadow only outside the disc, so it never darkens the glass.
    private var shadow: some View {
        Circle()
            .fill(tokens.shadow.color)
            .frame(width: diameter, height: diameter)
            .blur(radius: diameter * tokens.shadowBlur / 2)
            .offset(y: diameter * tokens.shadowOffset)
            .frame(width: canvasSide, height: canvasSide)
            .mask {
                ZStack {
                    Rectangle()
                    Circle()
                        .frame(width: diameter - 1, height: diameter - 1)
                        .blendMode(.destinationOut)
                }
                .compositingGroup()
            }
    }
}
