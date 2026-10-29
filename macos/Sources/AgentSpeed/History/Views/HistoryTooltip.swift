import AgentSpeedCore
import SwiftUI

/// The card next to the crosshair: date with weekday, the day's numbers and its top three models with share bars.
struct HistoryTooltip: View {
    let day: DayKey
    let summary: DaySummary?
    let chart: HistoryChartModel
    let l10n: Localizer
    let dark: Bool

    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(HistoryText.longDate(day, l10n))
                    .font(HistoryFont.tooltipTitle)
                    .lineLimit(1)
                if day == chart.today {
                    Text(l10n("history.inProgress"))
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(Color.accentColor)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 1.5)
                        .background(Capsule().fill(Color.accentColor.opacity(0.14)))
                }
            }
            .padding(.bottom, 8)
            if chart.metric == .traffic {
                traffic
            } else if let summary {
                rows(summary)
                if !summary.models.isEmpty {
                    Divider().padding(.vertical, 8)
                    models(summary)
                }
                if summary.unfinalized > 0 {
                    Text(l10n("history.unfinalized", summary.unfinalized))
                        .font(.system(size: 10.5))
                        .foregroundStyle(.historySecondary)
                        .padding(.top, 8)
                        .fixedSize(horizontal: false, vertical: true)
                }
            } else {
                row(l10n("history.table.output"), "0")
            }
        }
        .padding(12)
        .modifier(TooltipSurface(dark: dark, increasedContrast: contrast == .increased))
    }

    @ViewBuilder
    private func rows(_ summary: DaySummary) -> some View {
        let costFirst = chart.metric == .cost
        VStack(alignment: .leading, spacing: 4) {
            if costFirst { costRow(summary, emphasized: true) }
            row(l10n("history.table.output"), HistoryText.fullNumber(summary.output, l10n), emphasized: !costFirst)
            if !costFirst { shareBar(summary) }
            row(l10n("history.table.input"), HistoryText.fullNumber(summary.input, l10n))
            row(l10n("history.table.requests"), HistoryText.fullNumber(summary.requests, l10n))
            row(l10n("history.table.speed"), HistoryText.speed(summary.averageSpeed, l10n))
            if let peak = summary.peak {
                row(l10n("history.table.peak"), HistoryText.speed(peak, l10n))
            }
            if !costFirst { costRow(summary, emphasized: false) }
        }
    }

    /// The day's traffic (DESIGN 10, Usage history tooltip): the pair, the tools' part, this computer and how long the
    /// day was recorded when that is not the whole day.
    @ViewBuilder
    private var traffic: some View {
        let text = TrafficText(l10n: l10n, bits: false)
        if let traffic = chart.traffic[day] {
            VStack(alignment: .leading, spacing: 4) {
                row(l10n("history.table.traffic"), text.pair(traffic.shown), emphasized: true)
                if traffic.includeTools, !traffic.tools.isZero {
                    note(l10n("history.traffic.tools", text.pair(traffic.tools)))
                }
                if let machine = traffic.machine {
                    note(l10n("history.traffic.machine", text.pair(machine)))
                }
                if traffic.partial {
                    note(l10n("history.traffic.partial", HistoryText.duration(ms: traffic.recordedMs, l10n)))
                }
            }
        } else {
            note(l10n("history.traffic.notRecorded"))
        }
    }

    private func note(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 10.5).monospacedDigit())
            .foregroundStyle(.historySecondary)
            .fixedSize(horizontal: false, vertical: true)
    }

    @ViewBuilder
    private func shareBar(_ summary: DaySummary) -> some View {
        if !chart.isSingleSource, summary.sources.filter({ value($0) > 0 }).count > 1 {
            SourceShareBar(summary: summary, stack: chart.stack, dark: dark, value: value)
                .frame(height: 5)
                .padding(.bottom, 3)
        }
    }

    private func value(_ source: SourceOutput) -> Double {
        chart.metric == .cost ? Double(source.cost.microUsd) : Double(source.output)
    }

    /// The day's cost: always present (DESIGN 9), with `≥` and the unpriced count, `–` when nothing is priced.
    @ViewBuilder
    private func costRow(_ summary: DaySummary, emphasized: Bool) -> some View {
        let money = chart.money ?? .usd
        let cost = summary.cost
        let shown: String = {
            if case .priced(let micro, let partial) = cost.kind { return (partial ? "≥" : "") + money.amountWithUSD(micro) }
            return money.display(cost, subscriptionTag: l10n("cost.subscription.tag"))
        }()
        row(l10n("history.table.cost"), shown, emphasized: emphasized)
        if emphasized { shareBar(summary) }
        if let note = HistoryText.costNote(cost, l10n) {
            Text(note)
                .font(.system(size: 10.5))
                .foregroundStyle(.historySecondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func row(_ label: String, _ value: String, emphasized: Bool = false, swatch: Color? = nil) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            if let swatch {
                RoundedRectangle(cornerRadius: 2, style: .continuous)
                    .fill(swatch)
                    .frame(width: 7, height: 7)
            }
            Text(label)
                .foregroundStyle(.historySecondary)
                .lineLimit(1)
            Spacer(minLength: 10)
            Text(value)
                .fontWeight(emphasized ? .semibold : .medium)
                .lineLimit(1)
        }
        .font(HistoryFont.tooltip)
    }

    private func models(_ summary: DaySummary) -> some View {
        let tint = chart.isSingleSource ? (chart.stack.first?.tint(dark: dark) ?? Color.accentColor) : Color.accentColor
        return VStack(alignment: .leading, spacing: 5) {
            Text(l10n("history.table.models"))
                .font(.system(size: 10.5, weight: .semibold))
                .foregroundStyle(.historySecondary)
            ForEach(summary.models.prefix(3)) { model in
                let share = summary.output > 0 ? Double(model.output) / Double(summary.output) : 0
                VStack(alignment: .leading, spacing: 2) {
                    HStack(alignment: .firstTextBaseline) {
                        Text(model.model)
                            .lineLimit(1)
                            .truncationMode(.middle)
                        Spacer(minLength: 8)
                        if chart.metric == .output,
                           let speed = HistoryAggregator.speed(measuredOutput: model.measuredOutput, measuredMs: model.measuredMs) {
                            Text(HistoryText.speed(speed, l10n))
                                .foregroundStyle(.historySecondary)
                                .layoutPriority(1)
                        }
                        Text(HistoryText.percent(share))
                            .foregroundStyle(.historySecondary)
                            .layoutPriority(1)
                        Text(HistoryText.modelCost(summary.modelCosts[model.model], money: chart.money ?? .usd, l10n))
                            .fontWeight(.medium)
                            .layoutPriority(2)
                    }
                    .font(HistoryFont.tooltip)
                    GeometryReader { proxy in
                        ZStack(alignment: .leading) {
                            Capsule().fill(Color.primary.opacity(0.08))
                            Capsule().fill(tint).frame(width: max(3, proxy.size.width * share))
                        }
                    }
                    .frame(height: 3)
                }
            }
        }
    }
}

/// The day's output (or cost) split by source, in stacking order, as one thin bar.
private struct SourceShareBar: View {
    let summary: DaySummary
    let stack: [SourceKind]
    let dark: Bool
    let value: (SourceOutput) -> Double

    var body: some View {
        GeometryReader { proxy in
            let entries = Dictionary(uniqueKeysWithValues: summary.sources.map { ($0.source, value($0)) })
            let total = max(entries.values.reduce(0, +), 1)
            let gap: CGFloat = 1.5
            let parts = stack.map { ($0, entries[$0] ?? 0) }.filter { $0.1 > 0 }
            let usable = max(0, proxy.size.width - gap * CGFloat(max(parts.count - 1, 0)))
            HStack(spacing: gap) {
                ForEach(parts, id: \.0) { source, output in
                    Rectangle()
                        .fill(source.tint(dark: dark))
                        .frame(width: usable * output / total)
                }
            }
            .clipShape(Capsule())
        }
    }
}

/// The card's surface: Liquid Glass floating over the chart on macOS 26 (the system makes it frosted under Reduce
/// Transparency and outlines it under Increase Contrast), a solid card with a soft shadow before.
struct TooltipSurface: ViewModifier {
    let dark: Bool
    let increasedContrast: Bool

    func body(content: Content) -> some View {
        if #available(macOS 26, *) {
            content.glassEffect(.regular, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        } else {
            content.background {
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(dark ? Color(white: 0.19) : Color.white)
                    .shadow(color: .black.opacity(dark ? 0.45 : 0.16), radius: 12, y: 4)
                    .overlay(
                        RoundedRectangle(cornerRadius: 10, style: .continuous)
                            .strokeBorder(HistoryPalette.cardStroke(dark: dark, increasedContrast: increasedContrast), lineWidth: 1)
                    )
            }
        }
    }
}
