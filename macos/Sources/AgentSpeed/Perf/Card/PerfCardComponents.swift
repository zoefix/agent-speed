import AgentSpeedCore
import SwiftUI

/// The components (DESIGN 12, *Score card* 3): two groups 14 apart under their titles, rows 9 apart, each a four-column
/// line — label, the measured value (or why it is absent), the mini meter, the sub-score — and a meaning 3 under it.
/// The value and sub-score columns are as wide as their widest shown text, at least 64 and 34.
struct PerfCardComponents: View {
    let score: PerfCardScore
    let l10n: Localizer
    var meanings = true

    var body: some View {
        let speed = PerfCardText.rows(score, PerfComponentId.speedGroup, l10n)
        let efficiency = PerfCardText.rows(score, PerfComponentId.efficiencyGroup, l10n)
        let all = speed + efficiency
        let valueWidth = max(PerfCardLayout.valueMinimum, all.map { Self.width($0.value, present: $0.present) }.max() ?? 0)
        let subWidth = max(PerfCardLayout.subScoreMinimum, all.map { Self.subWidth($0.subScore) }.max() ?? 0)
        VStack(alignment: .leading, spacing: PerfCardLayout.groupSpacing) {
            group(l10n("perf.group.speed"), speed, valueWidth: valueWidth, subWidth: subWidth)
            group(l10n("perf.group.efficiency"), efficiency, valueWidth: valueWidth, subWidth: subWidth)
        }
    }

    private func group(_ title: String, _ rows: [PerfCardText.Row], valueWidth: CGFloat, subWidth: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            PerfCardSectionTitle(title: title)
            VStack(alignment: .leading, spacing: PerfCardLayout.rowSpacing) {
                ForEach(rows) { row in
                    PerfCardComponentRow(row: row, meanings: meanings, valueWidth: valueWidth, subWidth: subWidth)
                }
            }
        }
    }

    private static func width(_ text: String, present: Bool) -> CGFloat {
        let font = present ? NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .medium) : NSFont.systemFont(ofSize: 11.5)
        return ceil((text as NSString).size(withAttributes: [.font: font]).width)
    }

    private static func subWidth(_ text: String) -> CGFloat {
        ceil((text as NSString).size(withAttributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 12, weight: .semibold)]).width)
    }
}

/// One component: line 1 and the meaning. An absent component shows its reason in the value column, a dotted meter, a
/// tertiary `–` and why in its meaning line; never a fake 0.
struct PerfCardComponentRow: View {
    let row: PerfCardText.Row
    let meanings: Bool
    let valueWidth: CGFloat
    let subWidth: CGFloat

    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .center, spacing: 0) {
                Text(row.label)
                    .font(PerfCardFont.componentLabel)
                    .lineLimit(1)
                    .truncationMode(.tail)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Text(row.value)
                    .font(row.present ? PerfCardFont.componentValue : PerfCardFont.componentReason)
                    .foregroundStyle(row.present ? AnyShapeStyle(.primary) : AnyShapeStyle(.tertiary))
                    .lineLimit(1)
                    .frame(width: valueWidth, alignment: .trailing)
                PerfMiniMeter(fraction: row.meter)
                    .padding(.leading, 10)
                Text(row.subScore)
                    .font(PerfCardFont.subScore)
                    .foregroundStyle(row.present ? AnyShapeStyle(.primary) : AnyShapeStyle(.tertiary))
                    .lineLimit(1)
                    .frame(width: subWidth, alignment: .trailing)
                    .padding(.leading, 8)
            }
            if meanings {
                Text(row.meaning)
                    .font(PerfCardFont.meaning)
                    .foregroundStyle(.perfCaption)
                    .lineLimit(2)
                    .lineSpacing(1)
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
    }
}

/// 行為: looping (the amber mark of section 4), repeating and the summary of recent actions.
struct PerfCardSignals: View {
    let lines: [PerfCardText.SignalLine]
    let l10n: Localizer

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            PerfCardSectionTitle(title: l10n("perf.signals.title"))
            VStack(alignment: .leading, spacing: 6) {
                ForEach(lines) { line in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Image(systemName: glyph(line.kind))
                            .font(PerfCardFont.signalGlyph)
                            .foregroundStyle(glyphStyle(line.kind))
                            .frame(width: 16, alignment: .center)
                        Text(line.text)
                            .font(PerfCardFont.signal)
                            .foregroundStyle(line.kind == .looping ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }
        }
    }

    private func glyph(_ kind: PerfCardText.SignalLine.Kind) -> String {
        switch kind {
        case .looping: "arrow.triangle.2.circlepath"
        case .repeating: "repeat"
        case .summary: "list.bullet"
        }
    }

    private func glyphStyle(_ kind: PerfCardText.SignalLine.Kind) -> AnyShapeStyle {
        guard kind == .looping else { return AnyShapeStyle(.secondary) }
        let dark = colorScheme == .dark
        return AnyShapeStyle(DashboardPalette.legible(DashboardPalette.amber(dark: dark), dark: dark,
                                                      minimum: contrast == .increased ? 4.5 : 3).color)
    }
}
