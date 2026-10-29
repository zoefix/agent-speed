import AgentSpeedCore
import SwiftUI

/// The trend's title line, `每日趨勢 · model · source` (the model in the label color, medium), and its legend: the 1000
/// sample with `perf.reference` and, while the usual line shows, the dashed sample with `history.perf.usual.line`. Narrow,
/// the legend takes a line of its own.
struct PerfTrendHeader: View {
    let row: PerfHistoryRow
    let singleDay: DayKey?
    let stacked: Bool
    let l10n: Localizer

    var body: some View {
        if stacked {
            VStack(alignment: .leading, spacing: 4) {
                title
                legend
            }
        } else {
            HStack(alignment: .firstTextBaseline, spacing: 14) {
                title
                Spacer(minLength: 0)
                legend
            }
        }
    }

    private var title: some View {
        let marker = "\u{1F}"
        let template = l10n("history.perf.trend", marker).components(separatedBy: marker)
        var text = Text(template.first ?? "")
            + Text(PerfHistoryText.model(row, l10n)).foregroundStyle(.primary).fontWeight(.medium)
            + Text(" · " + PerfHistoryText.source(row, l10n))
            + Text(template.count > 1 ? template[1] : "")
        if let singleDay {
            text = text + Text(" · " + l10n("history.perf.trend.days", HistoryText.date(singleDay, l10n)))
        }
        return text
            .font(PerfHistoryFont.trendTitle)
            .foregroundStyle(.historySecondary)
            .lineLimit(stacked ? 2 : 1)
            .truncationMode(.middle)
            .layoutPriority(1)
    }

    private var legend: some View {
        HStack(spacing: 14) {
            HStack(spacing: 5) {
                PerfLegendSample(dashed: false, source: nil)
                Text(verbatim: "1000 " + l10n("perf.reference"))
            }
            if let usual = row.usualShown {
                HStack(spacing: 5) {
                    PerfLegendSample(dashed: true, source: row.source)
                    Text(l10n("history.perf.usual.line", PerfFormat.full(usual)))
                }
                .transition(.opacity)
            }
        }
        .font(PerfHistoryFont.legend)
        .modifier(PerfTertiaryText())
        .lineLimit(1)
        .fixedSize()
        .accessibilityElement(children: .combine)
    }
}

/// A 12 pt line sample: the reference line, or the usual line dashed in the source color.
struct PerfLegendSample: View {
    let dashed: Bool
    let source: SourceKind?

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let increased = contrast == .increased
        let color = source.map { $0.tint(dark: colorScheme == .dark).opacity(increased ? 1 : 0.6) }
            ?? PerfHistoryPalette.reference(increasedContrast: increased)
        Canvas { context, size in
            var path = Path()
            path.move(to: CGPoint(x: 0, y: size.height / 2))
            path.addLine(to: CGPoint(x: size.width, y: size.height / 2))
            context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: dashed ? 1.25 : 1, dash: dashed ? [4, 3] : []))
        }
        .frame(width: 12, height: 3)
        .accessibilityHidden(true)
    }
}

/// The tertiary ink of the section, resolved for the appearance.
struct PerfTertiaryText: ViewModifier {
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    func body(content: Content) -> some View {
        content.foregroundStyle(PerfHistoryPalette.tertiary(dark: colorScheme == .dark, increasedContrast: contrast == .increased))
    }
}
