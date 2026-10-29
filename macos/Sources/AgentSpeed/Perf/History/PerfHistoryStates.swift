import SwiftUI

/// Re-analysing with no record yet: the chart's gridlines and four table rows as static placeholders in the track
/// color, with `history.perf.analyzing` under them. Nothing moves (DESIGN 12, *States*).
struct PerfHistoryPlaceholder: View {
    let chartHeight: CGFloat
    let l10n: Localizer

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let fill = PerfPalette.track(dark: colorScheme == .dark, increasedContrast: contrast == .increased)
        VStack(alignment: .leading, spacing: 0) {
            bar(fill, width: 220, height: 9)
                .padding(.bottom, 10)
            Canvas { context, size in
                let left = PerfTrendGeometry.insets.left
                let top = PerfTrendGeometry.insets.top
                let plot = size.height - top - PerfTrendGeometry.insets.bottom
                for step in 0..<4 {
                    let y = (top + CGFloat(step) / 4 * plot).rounded() + 0.5
                    var line = Path()
                    line.move(to: CGPoint(x: left, y: y))
                    line.addLine(to: CGPoint(x: size.width - PerfTrendGeometry.insets.right, y: y))
                    context.stroke(line, with: .color(fill), lineWidth: 1)
                    context.fill(Path(roundedRect: CGRect(x: 4, y: y - 3, width: left - 12, height: 6), cornerRadius: 3),
                                 with: .color(fill))
                }
            }
            .frame(height: chartHeight)
            VStack(spacing: 0) {
                ForEach(0..<4, id: \.self) { index in
                    HStack(spacing: 10) {
                        bar(fill, width: [148, 120, 168, 104][index])
                        Spacer(minLength: 0)
                        bar(fill, width: 44)
                        bar(fill, width: 36)
                        bar(fill, width: 40)
                    }
                    .frame(height: PerfHistoryLayout.rowHeight)
                    .padding(.horizontal, PerfHistoryLayout.rowInset)
                }
            }
            .padding(.top, 12)
            Text(l10n("history.perf.analyzing"))
                .font(PerfHistoryFont.footnote)
                .foregroundStyle(.historySecondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity)
                .padding(.top, 12)
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(l10n("history.perf.analyzing"))
    }

    private func bar(_ fill: Color, width: CGFloat, height: CGFloat = 8) -> some View {
        RoundedRectangle(cornerRadius: height / 2, style: .continuous)
            .fill(fill)
            .frame(width: width, height: height)
            .accessibilityHidden(true)
    }
}

/// No timed request in the range for any source: the muted gauge, `history.perf.empty` and its hint. The section stays,
/// so the feature can be found.
struct PerfHistoryEmptyState: View {
    let l10n: Localizer

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        VStack(spacing: 6) {
            RestingGauge(lineWidth: 1.75)
                .frame(width: 24, height: 24)
                .padding(.bottom, 4)
            Text(l10n("history.perf.empty"))
                .font(.system(size: 13, weight: .semibold))
                .foregroundStyle(.historySecondary)
            Text(l10n("history.perf.empty.hint"))
                .font(.system(size: 11.5))
                .foregroundStyle(PerfPalette.caption(dark: colorScheme == .dark, increasedContrast: contrast == .increased))
                .lineSpacing(1.5)
                .frame(maxWidth: 360)
                .fixedSize(horizontal: false, vertical: true)
        }
        .multilineTextAlignment(.center)
        .frame(maxWidth: .infinity)
        .padding(.top, 26)
        .padding(.bottom, 20)
        .accessibilityElement(children: .combine)
    }
}
