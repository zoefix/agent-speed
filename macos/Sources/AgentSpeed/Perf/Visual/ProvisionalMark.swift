import AppKit
import SwiftUI

/// The provisional mark (DESIGN 12, *Provisional mark*): dots, the one visual language for "not settled yet". A dotted
/// underline under a provisional score, a dotted value arc with a hollow tip ring, hollow lit segments, a dotted scale
/// fill and dotted tracks.
enum ProvisionalMark {
    /// Dot centres sit this share of the text size below the baseline.
    static let textDepth: CGFloat = 0.22
    /// The menu bar block's two lines leave less room under line 1.
    static let barDepth: CGFloat = 0.17
    /// The underline's opacity over the number's color (100 % with Increase Contrast).
    static let opacity: Double = 0.7
    static let fadeSeconds: Double = 0.25

    /// `d = max(1 device px, 0.1 × the text size)`, rounded to the device pixel; Increase Contrast adds 0.5 pt.
    static func dotDiameter(textSize: CGFloat, scale: CGFloat, increasedContrast: Bool) -> CGFloat {
        let pixel = 1 / max(scale, 1)
        var diameter = max(pixel, ((0.1 * textSize) * scale).rounded() / scale)
        if increasedContrast { diameter = max(pixel, ((diameter + 0.5) * scale).rounded() / scale) }
        return diameter
    }

    /// `n = max(2, ⌊(w − d) / (2.4 d)⌋ + 1)` dots over an advance width `w`.
    static func dotCount(width: CGFloat, diameter: CGFloat) -> Int {
        guard diameter > 0, width > diameter else { return 2 }
        return max(2, Int(((width - diameter) / (2.4 * diameter)).rounded(.down)) + 1)
    }

    /// The dots' centres, spread evenly from the left edge to the right edge of `width`.
    static func dotCenters(width: CGFloat, diameter: CGFloat) -> [CGFloat] {
        let count = dotCount(width: width, diameter: diameter)
        let step = (width - diameter) / CGFloat(count - 1)
        return (0..<count).map { diameter / 2 + CGFloat($0) * step }
    }

    /// Draws the underline in a CoreGraphics context (orb readout, cached board rows, the menu bar image): `origin` is
    /// the number's left edge on its baseline; `yDown` for flipped contexts.
    static func drawUnderline(in context: CGContext, origin: CGPoint, width: CGFloat, textSize: CGFloat, color: CGColor,
                              scale: CGFloat, increasedContrast: Bool, depth: CGFloat = textDepth, yDown: Bool = true) {
        let diameter = dotDiameter(textSize: textSize, scale: scale, increasedContrast: increasedContrast)
        let centerY = origin.y + (yDown ? 1 : -1) * depth * textSize
        // Whole device pixels: a 1 px dot must not smear over two rows or columns.
        let device = max(scale, 1)
        func snapped(_ value: CGFloat) -> CGFloat { (value * device).rounded() / device }
        context.saveGState()
        context.setFillColor(increasedContrast ? color : color.copy(alpha: color.alpha * opacity) ?? color)
        let top = snapped(centerY - diameter / 2)
        for x in dotCenters(width: width, diameter: diameter) {
            context.fillEllipse(in: CGRect(x: snapped(origin.x + x - diameter / 2), y: top, width: diameter, height: diameter))
        }
        context.restoreGState()
    }

    // MARK: Shapes

    /// Glass's value arc while provisional: round dots (dash 0, gap 2 × the stroke).
    static func dottedArcStyle(lineWidth: CGFloat) -> StrokeStyle {
        StrokeStyle(lineWidth: lineWidth, lineCap: .round, dash: [0, 2 * lineWidth])
    }

    /// The same for CoreGraphics: call before stroking the arc.
    static func applyDottedArc(to context: CGContext, lineWidth: CGFloat) {
        context.setLineWidth(lineWidth)
        context.setLineCap(.round)
        context.setLineDash(phase: 0, lengths: [0, 2 * lineWidth])
    }

    /// The hollow ring at the tip of a dotted arc, sized from the arc's stroke (the prototype's 2.3 / 1.2 at 3 pt).
    static func tipRing(at center: CGPoint, lineWidth: CGFloat) -> (rect: CGRect, stroke: CGFloat) {
        let radius = 0.77 * lineWidth
        return (CGRect(x: center.x - radius, y: center.y - radius, width: 2 * radius, height: 2 * radius), 0.4 * lineWidth)
    }

    static func drawTipRing(in context: CGContext, at center: CGPoint, lineWidth: CGFloat, color: CGColor, hole: CGColor?) {
        let ring = tipRing(at: center, lineWidth: lineWidth)
        context.saveGState()
        context.setLineDash(phase: 0, lengths: [])
        if let hole {
            context.setFillColor(hole)
            context.fillEllipse(in: ring.rect)
        } else {
            context.setBlendMode(.clear)
            context.fillEllipse(in: ring.rect)
            context.setBlendMode(.normal)
        }
        context.setStrokeColor(color)
        context.setLineWidth(ring.stroke)
        context.strokeEllipse(in: ring.rect.insetBy(dx: ring.stroke / 2, dy: ring.stroke / 2))
        context.restoreGState()
    }

    /// Neon's lit segments while provisional: hollow, a 1 pt stroke inside the segment's outline.
    static func strokeHollow(_ path: CGPath, in context: CGContext, color: CGColor, lineWidth: CGFloat = 1) {
        context.saveGState()
        context.addPath(path)
        context.clip()
        context.addPath(path)
        context.setStrokeColor(color)
        context.setLineWidth(2 * lineWidth)
        context.strokePath()
        context.restoreGState()
    }
}

/// A row of round dots spread evenly over its width (DESIGN 12, *Provisional mark*): the underline's dots, the card's
/// dotted scale fill and the dotted tracks of absent components.
struct ProvisionalDots: View {
    var diameter: CGFloat
    /// Nil: the foreground style around it (the number's own, vibrancy included).
    var color: Color?
    /// Centre-to-centre distance as a multiple of the diameter (the underline's 2.4).
    var pitch: CGFloat = 2.4

    var body: some View {
        Canvas { context, size in
            guard size.width > 0, diameter > 0 else { return }
            let count = max(2, Int(((size.width - diameter) / (pitch * diameter)).rounded(.down)) + 1)
            let step = count > 1 ? (size.width - diameter) / CGFloat(count - 1) : 0
            let y = (size.height - diameter) / 2
            var path = Path()
            for index in 0..<count {
                path.addEllipse(in: CGRect(x: CGFloat(index) * step, y: y, width: diameter, height: diameter))
            }
            context.fill(path, with: color.map { .color($0) } ?? .foreground)
        }
        .accessibilityHidden(true)
        .allowsHitTesting(false)
    }
}

/// The dotted underline under a provisional number. It takes no width or height in layout, fades with the state
/// (0.25 s; Reduce Motion: at once) and is never read aloud: the score words say it.
private struct ProvisionalMarkModifier: ViewModifier {
    let shown: Bool
    let textSize: CGFloat
    let color: Color?
    let depth: CGFloat

    @Environment(\.displayScale) private var scale
    @Environment(\.colorSchemeContrast) private var contrast
    @Environment(\.perfReduceMotion) private var reduceMotion

    func body(content: Content) -> some View {
        let increased = contrast == .increased
        let diameter = ProvisionalMark.dotDiameter(textSize: textSize, scale: scale, increasedContrast: increased)
        content.overlay(alignment: Alignment(horizontal: .leading, vertical: .lastTextBaseline)) {
            ProvisionalDots(diameter: diameter, color: color)
                .frame(height: diameter)
                .alignmentGuide(.lastTextBaseline) { _ in diameter / 2 - depth * textSize }
                .opacity(shown ? (increased ? 1 : ProvisionalMark.opacity) : 0)
                .animation(reduceMotion ? nil : .easeOut(duration: ProvisionalMark.fadeSeconds), value: shown)
        }
    }
}

extension View {
    /// Draws the provisional mark under this number while `shown`, in the number's color (`color`, else its foreground
    /// style).
    func provisionalMark(_ shown: Bool, textSize: CGFloat, color: Color? = nil,
                         depth: CGFloat = ProvisionalMark.textDepth) -> some View {
        modifier(ProvisionalMarkModifier(shown: shown, textSize: textSize, color: color, depth: depth))
    }
}
