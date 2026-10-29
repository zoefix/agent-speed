import AgentSpeedCore
import SwiftUI

/// The score card (DESIGN 12, *Score card*): the breakdown behind the number. 320 pt wide, as tall as its content,
/// padding 14, blocks 12 apart, hairlines under the header and above the facts. It is a tooltip: never focusable, and
/// its content reaches VoiceOver as the row's extra content (`PerfCardText.accessibilityLines`).
struct PerfCardView: View {
    let model: PerfCardModel
    let l10n: Localizer
    var fit: PerfCardFit = .full

    var body: some View {
        let score = model.score
        VStack(alignment: .leading, spacing: 0) {
            PerfCardHeader(header: model.header, provisional: score.provisional && score.shown != nil, l10n: l10n)
            PerfCardHairline()
                .padding(.vertical, PerfCardLayout.blockSpacing)
            PerfCardScoreBlock(score: score, l10n: l10n)
            if score.shown != nil {
                PerfCardComponents(score: score, l10n: l10n, meanings: fit == .full)
                    .padding(.top, PerfCardLayout.blockSpacing + 2)
                    .transition(.opacity)
            }
            let signals = PerfCardText.signals(score.signals, l10n)
            if !signals.isEmpty {
                PerfCardSignals(lines: signals, l10n: l10n)
                    .padding(.top, PerfCardLayout.blockSpacing + 2)
                    .transition(.opacity)
            }
            PerfCardHairline()
                .padding(.top, PerfCardLayout.blockSpacing)
                .padding(.bottom, 9)
            VStack(alignment: .leading, spacing: 2) {
                ForEach(Array(PerfCardText.facts(model, l10n).enumerated()), id: \.offset) { _, fact in
                    Text(fact)
                        .font(PerfCardFont.fact)
                        .foregroundStyle(.secondary)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            if fit != .noMeaningsOrFootnote {
                Text(l10n("perf.card.footnote"))
                    .font(PerfCardFont.footnote)
                    .foregroundStyle(.perfCaption)
                    .lineSpacing(1.5)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 10)
            }
        }
        .padding(PerfCardLayout.padding)
        .frame(width: PerfCardLayout.width, alignment: .topLeading)
        .fixedSize(horizontal: false, vertical: true)
        .accessibilityHidden(true)
    }
}

/// DESIGN 12, *Fit*: a card taller than the visible screen minus 16 pt drops the components' meanings first, then the
/// footnote. Its accessibility content stays complete.
enum PerfCardFit: Int, CaseIterable, Sendable {
    case full, noMeanings, noMeaningsOrFootnote

    /// Room the card keeps from the visible frame's height.
    static let margin: CGFloat = 16
}

/// The card's surface where no system material can be drawn (offscreen previews) and under Reduce Transparency: the
/// solid fill of DESIGN 12, radius 12 and the 0.5 pt separator stroke.
struct PerfCardSolidSurface: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    func body(content: Content) -> some View {
        let shape = RoundedRectangle(cornerRadius: PerfCardLayout.radius, style: .continuous)
        content
            .background(shape.fill(PerfPalette.solidCard(dark: colorScheme == .dark)))
            .overlay(shape.strokeBorder(Color(nsColor: .separatorColor), lineWidth: contrast == .increased ? 1 : 0.5))
            .clipShape(shape)
    }
}
