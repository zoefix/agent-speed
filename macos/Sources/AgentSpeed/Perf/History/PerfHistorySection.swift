import AgentSpeedCore
import SwiftUI

/// Usage History 3c, 性能表現 (DESIGN 12, *Usage History: performance*): the performance score of every source and model
/// over the window's range, the selected row's daily trend against 1000 and its usual line, and the table of pooled
/// values. It lays itself out by its content width: wide from 860, medium from 600, narrow below; nothing scrolls
/// sideways.
struct PerfHistorySection: View {
    let model: HistoryViewModel

    @State private var width: CGFloat = 836
    @FocusState private var rowsFocused: Bool

    var body: some View {
        let l10n = model.l10n
        let content = model.perf
        let analyzing = if case .analyzing = model.store.state { true } else { false }
        let mode = PerfHistoryMode(rows: content.rows.count, analyzing: analyzing)
        let layout = PerfHistoryWidth(width: width)
        VStack(alignment: .leading, spacing: 0) {
            PerfHistoryHeader(l10n: l10n)
            Group {
                switch mode {
                case .analyzing:
                    PerfHistoryPlaceholder(chartHeight: layout.chartHeight, l10n: l10n)
                        .padding(.top, 14)
                case .empty:
                    PerfHistoryEmptyState(l10n: l10n)
                case .rows:
                    rows(content, layout)
                }
            }
            .transition(.opacity)
            .id(mode)
        }
        .animation(PerfHistoryMotion.fade, value: mode)
        .padding(.horizontal, PerfHistoryLayout.horizontalPadding)
        .padding(.top, PerfHistoryLayout.topPadding)
        .padding(.bottom, PerfHistoryLayout.bottomPadding)
        // A zero minimum makes the measured width the offered one, never the content's.
        .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
        .historyCard()
        .onGeometryChange(for: CGFloat.self) { $0.size.width - PerfHistoryLayout.horizontalPadding * 2 } action: { measured in
            if abs(measured - width) > 0.5 { width = measured }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(l10n("history.perf.title"))
        .environment(\.locale, l10n.locale)
    }

    private func rows(_ content: PerfHistoryContent, _ layout: PerfHistoryWidth) -> some View {
        let l10n = model.l10n
        let reduceMotion = model.accessibility.reduceMotion
        let sorted = model.perfSort.sorted(content.rows)
        let selected = PerfHistoryContent.selected(model.perfSelection, in: sorted)
        return VStack(alignment: .leading, spacing: 0) {
            if let selected {
                PerfTrendHeader(row: selected, singleDay: content.singleDay, stacked: layout == .narrow, l10n: l10n)
                    .padding(.top, 12)
                ZStack {
                    PerfTrendChart(row: selected, days: content.trendDays, singleDay: content.singleDay,
                                   height: layout.chartHeight, l10n: l10n)
                        .id(TrendKey(row: selected.id, days: content.trendDays))
                        .transition(.opacity)
                }
                .padding(.top, 8)
            }
            Group {
                if layout == .narrow {
                    PerfHistoryList(rows: sorted, selected: selected?.id, l10n: l10n, reduceMotion: reduceMotion) {
                        select($0)
                    }
                    .padding(.top, 10)
                } else {
                    PerfHistoryTable(rows: sorted, width: layout, sort: model.perfSort, selected: selected?.id, l10n: l10n,
                                     reduceMotion: reduceMotion, onSort: { model.setPerfSort(model.perfSort.toggled($0)) },
                                     onSelect: { select($0) })
                    .padding(.top, 12)
                }
            }
            .focusable()
            .focused($rowsFocused)
            .focusEffectDisabled()
            .onKeyPress(keys: [.upArrow, .downArrow]) { press in
                guard rowsFocused, !sorted.isEmpty else { return .ignored }
                let current = selected.flatMap { key in sorted.firstIndex { $0.id == key.id } } ?? 0
                let next = min(max(current + (press.key == .upArrow ? -1 : 1), 0), sorted.count - 1)
                model.selectPerf(sorted[next].id)
                return .handled
            }
            .onCopyCommand {
                guard rowsFocused, let selected else { return [] }
                return [NSItemProvider(object: PerfHistoryText.copyText([selected], l10n) as NSString)]
            }
            PerfHistoryFootnote(l10n: l10n)
                .padding(.top, 12)
        }
        .animation(PerfHistoryMotion.fade, value: TrendKey(row: selected?.id, days: content.trendDays))
    }

    private func select(_ key: PerfHistoryKey) {
        rowsFocused = true
        model.selectPerf(key)
    }
}

/// The series' identity: a new selection or range crossfades the chart.
private struct TrendKey: Hashable {
    var row: PerfHistoryKey?
    var days: DayRange
}

struct PerfHistoryFootnote: View {
    let l10n: Localizer

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        Text(l10n("history.perf.footnote"))
            .font(PerfHistoryFont.footnote)
            .foregroundStyle(PerfPalette.caption(dark: colorScheme == .dark, increasedContrast: contrast == .increased))
            .lineSpacing(1.5)
            .fixedSize(horizontal: false, vertical: true)
    }
}
