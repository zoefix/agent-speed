import AgentSpeedCore
import AppKit
import SwiftUI

/// The Usage History window's content (DESIGN 4b): title and range, summary cards, chart and daily table in one
/// scrolling column, with the analysis state pinned at the bottom. The toolbar lives in AppKit.
struct HistoryView: View {
    let model: HistoryViewModel

    @State private var contentWidth: CGFloat = 872
    @FocusState private var tableFocused: Bool

    var body: some View {
        // A closed window keeps its model but none of its views, so nothing is laid out until it opens again.
        if model.isOpen {
            windowContent(model.content)
        }
    }

    private func windowContent(_ content: HistoryContent) -> some View {
        VStack(spacing: 0) {
            Group {
                if content.isEmpty {
                    emptyLayout(content)
                        .transition(.opacity)
                } else {
                    scrollingLayout(content)
                        .transition(.opacity)
                }
            }
            .animation(HistoryMotion.fade, value: content.isEmpty)
            HistoryFooter(store: model.store, l10n: model.l10n, unpriced: content.unpriced, rateNote: model.rateNote,
                          trafficSince: content.trafficSince.map {
                              model.l10n("history.traffic.since", HistoryText.shortDate($0, today: content.today, model.l10n))
                          },
                          onRefresh: model.refresh)
        }
        .frame(minWidth: 720, minHeight: 480)
    }

    private func emptyLayout(_ content: HistoryContent) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HistoryHeader(content: content, l10n: model.l10n)
            HistoryEmptyState(state: model.store.state, l10n: model.l10n)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .padding(HistoryLayout.outerPadding)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
    }

    private func scrollingLayout(_ content: HistoryContent) -> some View {
        let (widths, gap) = columnLayout(content.columns)
        return ScrollViewReader { proxy in
            ScrollView(.vertical) {
                // No pinned lazy sections (macOS issue #21): the sections above the table are eager, the rows lazy and
                // unpinned, and the column titles stick by a drawing offset that never feeds back into layout.
                VStack(alignment: .leading, spacing: 0) {
                    HistoryHeader(content: content, l10n: model.l10n)
                        .padding(.bottom, HistoryLayout.sectionSpacing)
                    SummaryCardsView(
                        cards: content.cards,
                        source: content.source,
                        l10n: model.l10n,
                        wide: contentWidth >= HistoryLayout.wideCards
                    )
                    .padding(.bottom, HistoryLayout.sectionSpacing)
                    Group {
                        if let breakdown = content.breakdown {
                            HistoryBreakdownCard(
                                breakdown: breakdown,
                                today: content.today,
                                source: content.source,
                                money: content.money,
                                l10n: model.l10n,
                                reduceMotion: model.accessibility.reduceMotion,
                                metric: Binding(get: { model.chartMetric }, set: { model.select(metric: $0) })
                            ) { source in
                                model.select(source: source)
                            }
                            .transition(.opacity)
                        } else {
                            HistoryChartCard(model: model)
                                .transition(.opacity)
                        }
                    }
                    .animation(HistoryMotion.fade, value: content.breakdown == nil)
                    .padding(.bottom, HistoryLayout.sectionSpacing + Spacing.xxs)
                    EffortHistorySection(model: model)
                        .padding(.bottom, HistoryLayout.sectionSpacing)
                    PerfHistorySection(model: model)
                        .padding(.bottom, HistoryLayout.sectionSpacing)
                    Text(model.l10n("mac.history.table"))
                        .font(HistoryFont.sectionTitle)
                        .accessibilityAddTraits(.isHeader)
                        .padding(.bottom, Spacing.xxs)
                    // The table takes the width it is offered: its columns follow the measured width, never set it, so
                    // an eager stack cannot keep the table wider than the window.
                    DailyTableHeader(columns: content.columns, widths: widths, gap: gap, sort: content.sort, l10n: model.l10n) {
                        model.toggleSort($0)
                    }
                    .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
                    .stickyInScrollView()
                    .zIndex(1)
                    LazyVStack(alignment: .leading, spacing: 0) {
                        ForEach(Array(content.rows.enumerated()), id: \.element.day) { index, row in
                            DailyTableRow(
                                row: row,
                                index: index,
                                columns: content.columns,
                                widths: widths,
                                gap: gap,
                                today: content.today,
                                money: content.money,
                                l10n: model.l10n,
                                interaction: model.interaction,
                                tableFocused: tableFocused,
                                onSelect: { select($0, in: content) },
                                onCopy: { copy(clicked: $0, in: content) }
                            )
                            .id(row.day)
                        }
                    }
                    .frame(minWidth: 0, maxWidth: .infinity, alignment: .leading)
                }
                .padding(.horizontal, HistoryLayout.outerPadding)
                .padding(.top, HistoryLayout.outerPadding)
                .padding(.bottom, HistoryLayout.outerPadding)
                .onGeometryChange(for: CGFloat.self) { $0.size.width - HistoryLayout.outerPadding * 2 } action: { width in
                    if abs(width - contentWidth) > 0.5 { contentWidth = width }
                }
            }
            .focusable()
            .focused($tableFocused)
            .focusEffectDisabled()
            .onKeyPress(keys: [.upArrow, .downArrow]) { press in
                guard tableFocused else { return .ignored }
                return moveSelection(by: press.key == .upArrow ? -1 : 1, extend: press.modifiers.contains(.shift), in: content, proxy: proxy)
            }
            .onKeyPress(.escape) {
                guard tableFocused, !model.interaction.selection.isEmpty else { return .ignored }
                model.interaction.selection = []
                return .handled
            }
            .onCopyCommand { copyItems(in: content) }
            .onChange(of: model.interaction.reveal?.id) { _, _ in
                guard let day = model.interaction.reveal?.day else { return }
                withAnimation(model.accessibility.reduceMotion ? nil : HistoryMotion.scroll) {
                    proxy.scrollTo(day, anchor: .center)
                }
            }
        }
    }

    private func columnLayout(_ columns: [HistoryColumn]) -> ([CGFloat], CGFloat) {
        let available = Double(contentWidth) - Double(DailyTableRow.inset * 2)
        let layout = HistoryColumnLayout.layout(columns.map(HistoryColumnLayout.spec), width: available)
        return (layout.widths.map { CGFloat($0) }, CGFloat(layout.gap))
    }

    // MARK: Selection

    private func select(_ day: DayKey, in content: HistoryContent) {
        let interaction = model.interaction
        let modifiers = NSEvent.modifierFlags
        tableFocused = true
        if modifiers.contains(.command) {
            if interaction.selection.contains(day) { interaction.selection.remove(day) } else { interaction.selection.insert(day) }
            interaction.anchor = day
        } else if modifiers.contains(.shift), let anchor = interaction.anchor,
                  let from = content.rows.firstIndex(where: { $0.day == anchor }),
                  let to = content.rows.firstIndex(where: { $0.day == day }) {
            interaction.selection = Set(content.rows[min(from, to)...max(from, to)].map(\.day))
        } else {
            interaction.selection = [day]
            interaction.anchor = day
        }
    }

    private func moveSelection(by delta: Int, extend: Bool, in content: HistoryContent, proxy: ScrollViewProxy) -> KeyPress.Result {
        let rows = content.rows
        guard !rows.isEmpty else { return .ignored }
        let interaction = model.interaction
        let current = interaction.anchor.flatMap { anchor in rows.firstIndex { $0.day == anchor } }
        let next = current.map { ($0 + delta).clamped(to: 0...(rows.count - 1)) } ?? (delta > 0 ? 0 : rows.count - 1)
        let day = rows[next].day
        if extend, let current {
            interaction.selection.insert(rows[current].day)
            interaction.selection.insert(day)
        } else {
            interaction.selection = [day]
        }
        interaction.anchor = day
        proxy.scrollTo(day)
        return .handled
    }

    // MARK: Copy

    private func copyItems(in content: HistoryContent) -> [NSItemProvider] {
        let selected = content.rows.filter { model.interaction.selection.contains($0.day) }
        guard !selected.isEmpty else { return [] }
        let text = HistoryTable.copyText(selected, columns: content.columns, today: content.today, money: content.money, model.l10n)
        return [NSItemProvider(object: text as NSString)]
    }

    private func copy(clicked day: DayKey, in content: HistoryContent) {
        let selection = model.interaction.selection
        let days = selection.contains(day) ? selection : [day]
        let rows = content.rows.filter { days.contains($0.day) }
        let text = HistoryTable.copyText(rows, columns: content.columns, today: content.today, money: content.money, model.l10n)
        let pasteboard = NSPasteboard.general
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
    }
}

/// `用量歷史` with the source and the resolved range spelled out under it.
struct HistoryHeader: View {
    let content: HistoryContent
    let l10n: Localizer

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            Text(l10n("history.title"))
                .font(HistoryFont.title)
                .accessibilityAddTraits(.isHeader)
            HStack(spacing: 6) {
                if let source = content.source {
                    Circle()
                        .fill(source.tint(dark: colorScheme == .dark))
                        .frame(width: 8, height: 8)
                        .accessibilityHidden(true)
                    Text(l10n(source.titleKey))
                        .foregroundStyle(.primary)
                    Text(verbatim: "·")
                        .accessibilityHidden(true)
                }
                Text(HistoryText.rangeSubtitle(content.range, l10n))
                    .contentTransition(.numericText())
                if content.range.isSingleDay, content.range.start == content.today {
                    InProgressBadge(l10n: l10n)
                }
            }
            .font(HistoryFont.subtitle)
            .foregroundStyle(.historySecondary)
            .lineLimit(1)
            .accessibilityElement(children: .combine)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}
