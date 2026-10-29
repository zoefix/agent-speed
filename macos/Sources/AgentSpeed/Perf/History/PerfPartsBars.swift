import AgentSpeedCore
import SwiftUI

/// The medium layout's parts column (DESIGN 12, *Widths*): seven bars in the card's order, 5 wide, 2 apart, 20 tall,
/// each filled from the bottom to `f` of its sub-score in the score ink over a track, a dashed 1000 line across them at
/// mid-height; an absent component is a 1 pt dotted stub. Hovering a bar names the component, its value and sub-score.
struct PerfPartsBars: View {
    let row: PerfHistoryRow
    let l10n: Localizer
    var reduceMotion = false

    static let barWidth: CGFloat = 5
    static let spacing: CGFloat = 2
    static let height: CGFloat = 20
    static var width: CGFloat { barWidth * 7 + spacing * 6 }

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let dark = colorScheme == .dark
        let increased = contrast == .increased
        HStack(alignment: .bottom, spacing: Self.spacing) {
            ForEach(PerfComponentId.cardOrder, id: \.self) { id in
                bar(fraction(id), dark: dark, increased: increased)
                    .help(PerfHistoryText.partHint(row, id, l10n))
            }
        }
        .frame(width: Self.width, height: Self.height)
        .overlay {
            Canvas { context, size in
                var path = Path()
                path.move(to: CGPoint(x: -2, y: size.height / 2))
                path.addLine(to: CGPoint(x: size.width + 2, y: size.height / 2))
                context.stroke(path, with: .color(Color(nsColor: .labelColor).opacity(increased ? 0.8 : 0.3)),
                               style: StrokeStyle(lineWidth: 1, dash: [2, 2]))
            }
            .allowsHitTesting(false)
        }
        .animation(PerfHistoryMotion.meters(reduceMotion: reduceMotion), value: PerfComponentId.cardOrder.map(fraction))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(PerfHistoryText.parts(row, l10n))
    }

    private func fraction(_ id: PerfComponentId) -> Double? {
        guard let component = row.component(id), component.isPresent, let sub = component.subScore else { return nil }
        return PerfFormat.scale(sub)
    }

    @ViewBuilder
    private func bar(_ fraction: Double?, dark: Bool, increased: Bool) -> some View {
        let shape = RoundedRectangle(cornerRadius: 1.5, style: .continuous)
        if let fraction {
            ZStack(alignment: .bottom) {
                shape.fill(PerfPalette.track(dark: dark, increasedContrast: increased))
                shape.fill(PerfPalette.ink(dark: dark, increasedContrast: increased))
                    .frame(height: max(1.5, CGFloat(fraction) * Self.height))
            }
            .frame(width: Self.barWidth, height: Self.height)
            .contentShape(Rectangle())
        } else {
            VStack(spacing: 0) {
                Spacer(minLength: 0)
                Canvas { context, size in
                    var path = Path()
                    path.move(to: CGPoint(x: 0, y: size.height / 2))
                    path.addLine(to: CGPoint(x: size.width, y: size.height / 2))
                    context.stroke(path, with: .color(PerfHistoryPalette.tertiary(dark: dark, increasedContrast: increased)),
                                   style: StrokeStyle(lineWidth: 1, lineCap: .round, dash: [0.01, 2]))
                }
                .frame(height: 1)
            }
            .frame(width: Self.barWidth, height: Self.height)
            .contentShape(Rectangle())
        }
    }
}
