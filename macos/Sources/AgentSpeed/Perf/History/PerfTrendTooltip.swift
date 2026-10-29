import AgentSpeedCore
import SwiftUI

/// The trend's tooltip card: the date with weekday, then the day's score in full form with its tier word and timed
/// requests, or for a gap day the sample hint with its count and 20.
struct PerfTrendTooltip: View {
    let point: PerfHistory.Point
    let l10n: Localizer

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let day = DayKey(point.day) {
                Text(HistoryText.longDate(day, l10n))
                    .font(PerfHistoryFont.tooltipTitle)
                    .lineLimit(1)
            }
            if let score = point.score {
                let shown = perfRoundHalfAway(score)
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(PerfFormat.full(shown))
                        .font(PerfHistoryFont.tooltipScore)
                    Text(l10n(PerfScoreText.tierKey(shown)))
                        .font(PerfHistoryFont.tooltip)
                        .foregroundStyle(.historySecondary)
                }
                Text(PerfHistoryText.requests(point.n, l10n))
                    .font(PerfHistoryFont.tooltip)
                    .foregroundStyle(.historySecondary)
            } else {
                Text(PerfHistoryText.sampleHint(point.n, l10n))
                    .font(PerfHistoryFont.tooltip)
                    .foregroundStyle(.historySecondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(width: 220, alignment: .leading)
            }
        }
        .padding(12)
        .modifier(TooltipSurface(dark: colorScheme == .dark, increasedContrast: contrast == .increased))
        .environment(\.locale, l10n.locale)
    }
}
