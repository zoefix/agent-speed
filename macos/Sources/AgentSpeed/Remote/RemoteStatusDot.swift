import AppKit
import SwiftUI

/// A host's 8 pt status dot (DESIGN 13). The in-progress dot breathes; under Reduce Motion it rests half lit.
struct RemoteStatusDot: View {
    let kind: RemoteDotKind
    var size: CGFloat = 8
    var reduceMotion = false

    @State private var dim = false
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        Group {
            switch kind {
            case .idle:
                Circle()
                    .strokeBorder(Color(nsColor: .tertiaryLabelColor), lineWidth: contrast == .increased ? 1.5 : 1.2)
            case .progress:
                Circle()
                    .fill(Color(nsColor: .secondaryLabelColor))
                    .opacity(reduceMotion ? 0.6 : (dim ? 0.3 : 0.95))
                    .animation(reduceMotion ? nil : .easeInOut(duration: 0.9).repeatForever(autoreverses: true), value: dim)
                    .onAppear { if !reduceMotion { dim = true } }
            default:
                Circle().fill(kind.color)
            }
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }
}

extension RemoteDotKind {
    var color: Color { Color(nsColor: nsColor) }

    var nsColor: NSColor {
        switch self {
        case .connected: .systemGreen
        case .progress: .secondaryLabelColor
        case .retrying: .systemOrange
        case .attention: .systemRed
        case .idle: .tertiaryLabelColor
        }
    }

    /// A 6 pt dot for menu items (hollow when off), drawn in the menu's appearance.
    var menuImage: NSImage {
        let kind = self
        let image = NSImage(size: NSSize(width: 6, height: 10), flipped: false) { _ in
            let rect = NSRect(x: 0, y: 2, width: 6, height: 6)
            let path = NSBezierPath(ovalIn: kind == .idle ? rect.insetBy(dx: 0.5, dy: 0.5) : rect)
            if kind == .idle {
                kind.nsColor.setStroke()
                path.lineWidth = 1
                path.stroke()
            } else {
                kind.nsColor.setFill()
                path.fill()
            }
            return true
        }
        image.accessibilityDescription = nil
        return image
    }
}
