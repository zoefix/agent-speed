import SwiftUI

/// A chip of the score card (DESIGN 12, *Chips*): 18 pt tall, fully rounded, horizontal padding 7, 11 pt semibold
/// tabular text. The usual chip is mint above usual and neutral otherwise (a slower day is never amber or red); without
/// a baseline, and the provisional chip with its ring of eight dots, it is neutral text in a dotted outline. Increase
/// Contrast trades fills for a solid 1 pt stroke in the text color. The small size (16 pt, 10 pt text) marks History rows.
struct PerfChip: View {
    enum Size {
        case regular, small
    }

    let title: String
    let kind: PerfPalette.ChipKind
    var ring = false
    var size = Size.regular

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    static let height: CGFloat = 18

    var body: some View {
        let increased = contrast == .increased
        let colors = PerfPalette.chip(kind, dark: colorScheme == .dark, increasedContrast: increased)
        HStack(spacing: 4) {
            if ring { DottedRingGlyph(color: colors.text).frame(width: 10, height: 10) }
            Text(title)
                .font(size == .small ? .system(size: 10, weight: .semibold) : PerfCardFont.chip)
                .lineLimit(1)
        }
        .foregroundStyle(colors.text)
        .padding(.horizontal, size == .small ? 6 : 7)
        .frame(height: size == .small ? 16 : Self.height)
        .background {
            if let fill = colors.fill, !increased { Capsule(style: .continuous).fill(fill) }
        }
        .overlay {
            switch colors.outline {
            case .none:
                EmptyView()
            case .solid:
                Capsule(style: .continuous).strokeBorder(colors.text, lineWidth: 1)
            case .dotted:
                Capsule(style: .continuous)
                    .strokeBorder(Color(nsColor: .labelColor).opacity(increased ? 1 : 0.35),
                                  style: StrokeStyle(lineWidth: 1, lineCap: .round, dash: [0, 2.5]))
            }
        }
        .fixedSize()
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(title)
    }
}

/// The provisional chip's glyph: a ring drawn as eight dots, the provisional mark's language in a 10 pt circle.
struct DottedRingGlyph: View {
    var color: Color
    var dots = 8

    var body: some View {
        Canvas { context, size in
            let diameter = min(size.width, size.height) * 0.2
            let radius = min(size.width, size.height) / 2 - diameter / 2 - 0.25
            let center = CGPoint(x: size.width / 2, y: size.height / 2)
            var path = Path()
            for index in 0..<dots {
                let angle = Double(index) / Double(dots) * 2 * .pi - .pi / 2
                let point = CGPoint(x: center.x + radius * cos(angle), y: center.y + radius * sin(angle))
                path.addEllipse(in: CGRect(x: point.x - diameter / 2, y: point.y - diameter / 2,
                                           width: diameter, height: diameter))
            }
            context.fill(path, with: .color(color))
        }
        .accessibilityHidden(true)
    }
}
