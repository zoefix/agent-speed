import AppKit

/// Draws the menu bar item with Core Graphics into a plain image, like native extras. The system snapshots the
/// status item on every change, and an image is far cheaper to snapshot than a hosted view tree.
enum StatusItemArtist {
    /// An image that blits a cached bitmap for whichever menu bar appearance draws it (each display's menu bar
    /// can differ), so the system's repeated snapshots never re-run the drawing code.
    static func image(for state: StatusItemState) -> NSImage {
        let size = NSSize(width: state.layout.length, height: state.layout.height)
        var cache: [Bool: CGImage] = [:]
        let image = NSImage(size: size, flipped: false) { rect in
            let match = NSAppearance.currentDrawing().bestMatch(from: [.darkAqua, .vibrantDark, .aqua, .vibrantLight])
            let dark = match == .darkAqua || match == .vibrantDark
            let bitmap = cache[dark] ?? render(state, dark: dark, size: size)
            cache[dark] = bitmap
            guard let bitmap, let context = NSGraphicsContext.current?.cgContext else { return false }
            context.interpolationQuality = .high
            context.draw(bitmap, in: rect)
            return true
        }
        image.isTemplate = false
        return image
    }

    /// The item's bitmap at `scale` device pixels per point (2 in the menu bar; previews also check 1).
    static func render(_ state: StatusItemState, dark: Bool, size: NSSize, scale: CGFloat = 2) -> CGImage? {
        guard let context = CGContext(
            data: nil,
            width: Int(size.width * scale),
            height: Int(size.height * scale),
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.translateBy(x: 0, y: size.height * scale)
        context.scaleBy(x: scale, y: -scale)
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: context, flipped: true)
        draw(state, dark: dark, in: CGRect(origin: .zero, size: size))
        NSGraphicsContext.restoreGraphicsState()
        return context.makeImage()
    }

    static func draw(_ state: StatusItemState, dark: Bool, in rect: CGRect) {
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        let palette = StatusItemPalette(isDark: dark, increasedContrast: state.increasedContrast)
        let layout = state.layout
        if layout.showsGlyph { drawGauge(state, palette: palette, layout: layout, in: context) }
        if layout.showsValue { drawText(state, palette: palette, layout: layout) }
        state.secondary?.draw(at: layout.secondaryX, height: layout.height, palette: palette)
        state.traffic?.draw(at: layout.readoutX, height: layout.height, palette: palette)
        state.intelligence?.draw(at: layout.intelligenceX, height: layout.height, palette: palette)
        state.quota?.draw(at: layout.quotaX, height: layout.height, palette: palette)
    }

    private static func drawGauge(_ state: StatusItemState, palette: StatusItemPalette, layout: StatusItemLayout, in context: CGContext) {
        let lineWidth = StatusItemLayout.glyphLineWidth
        let radius = (StatusItemLayout.glyphDiameter - lineWidth) / 2
        let center = layout.glyphCenter
        let start = 135 * CGFloat.pi / 180
        context.saveGState()
        defer { context.restoreGState() }
        context.setLineCap(.round)
        context.setLineWidth(lineWidth)

        context.addArc(center: center, radius: radius, startAngle: start, endAngle: start + 1.5 * .pi, clockwise: false)
        context.setStrokeColor(palette.track.cgColor)
        context.strokePath()

        let ring = state.ring
        let fraction = ring.fraction.clamped(to: 0...1)
        guard fraction > 0.004 else { return }
        let fill = state.colored ? palette.heat(fraction) : palette.primary
        let end = start + 1.5 * .pi * CGFloat(fraction)
        context.setAlpha(ring.fillOpacity)
        if ring.glow > 0.001 {
            let glowColor = fill.opacity(ring.glow * palette.glowOpacity)
            context.setShadow(offset: .zero, blur: 6 * context.ctm.a.magnitude, color: glowColor.cgColor)
        }
        context.addArc(center: center, radius: radius, startAngle: start, endAngle: end, clockwise: false)
        context.setStrokeColor(fill.cgColor)
        context.strokePath()
    }

    private static func drawText(_ state: StatusItemState, palette: StatusItemPalette, layout: StatusItemLayout) {
        let valueFont = StatusItemLayout.valueFont
        let value = NSAttributedString(string: state.value, attributes: [
            .font: valueFont,
            .foregroundColor: (state.idle ? palette.secondary : palette.primary).nsColor,
        ])
        let width = value.size().width
        value.draw(at: CGPoint(x: layout.valueX, y: layout.baseline - valueFont.ascender))

        guard let unit = layout.unit else { return }
        let unitFont = StatusItemLayout.unitFont
        let text = NSAttributedString(string: unit, attributes: [
            .font: unitFont,
            .foregroundColor: palette.secondary.nsColor,
        ])
        text.draw(at: CGPoint(x: layout.valueX + width + StatusItemLayout.unitGap, y: layout.baseline - unitFont.ascender))
    }
}
