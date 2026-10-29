import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// The score: the eyebrow when it applies, the number (34 pt, full form) with the usual chip level with its digits'
/// cap-height centre, the provisional mark under a provisional number, the log scale 10 under the baseline, the summary
/// and the provisional hint.
struct PerfCardScoreBlock: View {
    let score: PerfCardScore
    let l10n: Localizer

    @Environment(\.perfReduceMotion) private var reduceMotion
    /// The value the scale draws, following the number's count.
    @State private var scaleValue: Double?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let eyebrow = PerfCardText.eyebrow(score, l10n) {
                Text(eyebrow)
                    .font(PerfCardFont.eyebrow)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                    .padding(.bottom, 2)
                    .transition(.opacity)
            }
            HStack(alignment: .center, spacing: 8) {
                PerfScoreNumber(value: score.shown, form: .full,
                                font: score.shown == nil ? PerfCardFont.numberAbsent : PerfCardFont.number)
                    .tracking(PerfCardFont.numberTracking)
                    .foregroundStyle(numberStyle)
                    .provisionalMark(score.provisional && score.shown != nil, textSize: PerfCardFont.numberSize,
                                     color: numberColor)
                    .animation(PerfScoreMotion.formAnimation, value: score.form)
                    .frame(maxWidth: .infinity, alignment: .leading)
                if score.shown != nil {
                    let usual = PerfCardText.usual(score, l10n)
                    PerfChip(title: usual.text, kind: usual.kind)
                        // The cap-height centre of 34 pt digits sits 2.6 pt above the line's centre.
                        .offset(y: 2.6)
                        .transition(.opacity)
                }
            }
            PerfScale(value: scaleValue, provisional: score.provisional, resting: score.resting)
                .padding(.top, 10)
            Text(PerfCardText.summary(score, l10n))
                .font(PerfCardFont.summary)
                .foregroundStyle(.secondary)
                .lineLimit(2)
                .lineSpacing(1.5)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.top, 8)
            if let hint = PerfCardText.provisionalHint(score, l10n) {
                Text(hint)
                    .font(PerfCardFont.hint)
                    .foregroundStyle(.tertiary)
                    .lineSpacing(1)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, 4)
                    .transition(.opacity)
            }
        }
        .onAppear { scaleValue = score.shown.map(Double.init) }
        .onChange(of: score.shown) { old, new in
            switch PerfMotion.change(from: old, to: new, reduceMotion: reduceMotion) {
            case .count(let seconds):
                withAnimation(PerfScoreMotion.countAnimation(seconds: seconds)) { scaleValue = new.map(Double.init) }
            case .crossfade(let seconds):
                withAnimation(.easeOut(duration: seconds)) { scaleValue = new.map(Double.init) }
            case .replace, .none:
                scaleValue = new.map(Double.init)
            }
        }
    }

    private var numberStyle: AnyShapeStyle {
        if score.shown == nil { return AnyShapeStyle(.tertiary) }
        return score.resting ? AnyShapeStyle(.secondary) : AnyShapeStyle(.primary)
    }

    private var numberColor: Color {
        Color(nsColor: score.resting ? .secondaryLabelColor : .labelColor)
    }
}
