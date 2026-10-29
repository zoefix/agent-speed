import AgentSpeedCore
import AppKit
import Observation
import SwiftUI

/// The filter and derived content of the Usage History window. The content is rebuilt once per change of the stored
/// days, the filter, the language or the date, never per frame.
@MainActor
@Observable
final class HistoryViewModel {
    /// The window's own `history.includeSubscriptions` choice (DESIGN 9); absent until the user changes it.
    static let includeSubscriptionsKey = "historyIncludeSubscriptions"
    /// The sort of 性能表現 (DESIGN 12), as `column:asc|desc`.
    static let perfSortKey = "historyPerfSort"

    let store: HistoryStore
    let l10n: Localizer
    let accessibility: AccessibilityState
    /// Currency, rates and subscription marks (DESIGN 9).
    let cost: CostDisplay
    /// Stored traffic days (METRICS 10.9).
    let traffic: TrafficStore
    /// Pointer, keyboard and selection state, kept apart so hovering never rebuilds the content.
    let interaction = HistoryInteraction()

    private(set) var source: SourceKind?
    private(set) var rangeKind: HistoryRangeKind = .days30
    private(set) var customRange: DayRange?
    private(set) var sort = HistorySort()
    private(set) var chartMetric: HistoryChartMetric = .output
    /// Whether subscription sources count at their API-equivalent cost in this window.
    private(set) var includeSubscriptions: Bool
    /// The remembered sort of 性能表現.
    private(set) var perfSort: PerfHistorySort
    /// The row whose trend 性能表現 charts; kept while the window is open, nil for the default.
    private(set) var perfSelection: PerfHistoryKey?
    private(set) var content: HistoryContent
    private(set) var perf: PerfHistoryContent
    /// Whether the window is open. A closed window keeps its filter but neither rebuilds its content nor shows its
    /// views: the data it follows changes every few seconds, and none of it is laid out until the window opens again.
    var isOpen = true {
        didSet {
            if !isOpen { perfSelection = nil }
            if isOpen, needsRebuild { rebuild() }
        }
    }

    @ObservationIgnored private var needsRebuild = false
    @ObservationIgnored private var dataLoop: ObservationLoop?
    @ObservationIgnored private var observers: [NSObjectProtocol] = []

    init(store: HistoryStore, l10n: Localizer, accessibility: AccessibilityState, cost: CostDisplay, traffic: TrafficStore) {
        self.store = store
        self.l10n = l10n
        self.accessibility = accessibility
        self.cost = cost
        self.traffic = traffic
        let stored = cost.settings.storage.object(forKey: Self.includeSubscriptionsKey) as? Bool
        let include = stored ?? cost.settings.includeSubscriptions
        includeSubscriptions = include
        perfSort = PerfHistorySort(storageValue: cost.settings.storage.string(forKey: Self.perfSortKey))
        let today = DayKey(Date(), calendar: HistoryDates.calendar)
        let initial = HistoryContent.build(
            usage: store.days, source: nil, rangeKind: .days30, customRange: nil, sort: HistorySort(), today: today, l10n: l10n,
            money: HistoryMoney(cost), costOptions: HistoryCostOptions(marked: cost.settings.subscriptionSources,
                                                                        includeSubscriptions: include),
            traffic: Self.historyTraffic(traffic, settings: cost.settings), prices: cost.prices.book.current
        )
        content = initial
        perf = PerfHistoryContent.build(store.perfRecords, range: initial.range, source: nil, today: today)
        dataLoop = ObservationLoop { [weak self] in
            guard let self else { return }
            _ = self.store.days
            _ = self.store.perfRecords
            _ = self.cost.prices.table
            _ = self.l10n.language
            _ = HistoryMoney(self.cost)
            _ = self.cost.settings.subscriptionSources
            _ = (self.traffic.days, self.traffic.since, self.cost.settings.trafficIncludeTools, self.cost.settings.trafficUnit)
            _ = self.cost.settings.enabledSources
            self.rebuild()
        }
        let center = NotificationCenter.default
        for name in [Notification.Name.NSCalendarDayChanged, .NSSystemTimeZoneDidChange, .NSSystemClockDidChange] {
            observers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.rebuild() }
            })
        }
    }

    var motion: Animation? { accessibility.reduceMotion ? nil : HistoryMotion.chart }

    /// Also when the source is already selected: opening the window applies the traffic rule below again.
    func select(source: SourceKind?) {
        if source != self.source {
            self.source = source
            interaction.reset()
            rebuild()
        }
        followTraffic()
    }

    func select(range kind: HistoryRangeKind) {
        guard kind != .custom, kind != rangeKind else { return }
        rangeKind = kind
        interaction.reset()
        rebuild()
        followTraffic()
        // Today is still being written: bring in what the logs hold now, the view follows as the store publishes.
        if kind == .today { refreshToday() }
    }

    func applyCustom(from: DayKey, to: DayKey) {
        customRange = HistoryRanges.clamp(from: from, to: to, within: content.selectableBounds)
        rangeKind = .custom
        interaction.reset()
        rebuild()
        followTraffic()
    }

    /// DESIGN 10, Opening on traffic: a range with agent traffic but no token usage would chart nothing as output or
    /// cost, so the chart switches to traffic; it never switches back by itself.
    private func followTraffic() {
        guard chartMetric != .traffic, offersTraffic, content.showsOnlyTraffic else { return }
        select(metric: .traffic)
    }

    /// The chart's (and a single day's breakdown's) metric: output or cost.
    func select(metric: HistoryChartMetric) {
        guard metric != chartMetric else { return }
        chartMetric = metric
        interaction.hover(nil, from: .chart)
        rebuild()
    }

    /// A 性能表現 header click; the window remembers the sort.
    func setPerfSort(_ sort: PerfHistorySort) {
        guard sort != perfSort else { return }
        perfSort = sort
        cost.settings.storage.set(sort.storageValue, forKey: Self.perfSortKey)
    }

    /// The row whose trend 性能表現 charts.
    func selectPerf(_ key: PerfHistoryKey) {
        if key != perfSelection { perfSelection = key }
    }

    /// The toolbar checkbox; the window remembers its own choice.
    func setIncludeSubscriptions(_ include: Bool) {
        guard include != includeSubscriptions else { return }
        includeSubscriptions = include
        cost.settings.storage.set(include, forKey: Self.includeSubscriptionsKey)
        rebuild()
    }

    /// `以 2026/10/22 的匯率換算` for a non-USD currency (DESIGN 9 rate note).
    var rateNote: String? { cost.historyNote }

    func toggleSort(_ column: HistoryColumn) {
        sort = content.sort.toggled(column)
        content = content.sorted(by: sort)
    }

    func refresh() {
        store.refresh()
    }

    /// The range is the single day that is still being written (DESIGN 4b: today's figures update live).
    var showsLiveDay: Bool {
        content.range.isSingleDay && content.range.start == content.today
    }

    /// Re-reads today's logs without the analysing progress, so the live day updates in place.
    func refreshToday() {
        store.refresh(showsProgress: false)
    }

    private func rebuild() {
        guard isOpen else {
            needsRebuild = true
            return
        }
        needsRebuild = false
        let today = DayKey(Date(), calendar: HistoryDates.calendar)
        let options = HistoryCostOptions(
            marked: cost.settings.subscriptionSources, includeSubscriptions: includeSubscriptions, metric: chartMetric
        )
        let next = HistoryContent.build(
            usage: store.days, source: source, rangeKind: rangeKind, customRange: customRange, sort: sort,
            today: today, l10n: l10n, money: HistoryMoney(cost), costOptions: options,
            traffic: Self.historyTraffic(traffic, settings: cost.settings), prices: cost.prices.book.current
        )
        if next != content { content = next }
        let nextPerf = PerfHistoryContent.build(store.perfRecords, range: next.range, source: source, today: today)
        if nextPerf != perf { perf = nextPerf }
    }

    /// Disabled sources' rows are left out (METRICS 10.5, DESIGN 10); this computer's rows stay.
    private static func historyTraffic(_ store: TrafficStore, settings: AppSettings) -> HistoryTraffic {
        HistoryTraffic(days: TrafficVisibility.days(store.days, enabled: SourceSet(settings.enabledSources)), since: store.since,
                       includeTools: settings.trafficIncludeTools, bits: settings.trafficUnit == .bits)
    }

    /// The metric control offers traffic while measuring is on or traffic history exists (DESIGN 10).
    var offersTraffic: Bool { cost.settings.trafficEnabled || !traffic.days.isEmpty }
}

/// Hover, keyboard focus day, row selection and the reveal flash.
@MainActor
@Observable
final class HistoryInteraction {
    enum Origin: Equatable { case chart, table }

    private enum TableHoverTarget { case day(DayKey), outside }

    /// The day under the pointer, and where the pointer is.
    private(set) var hovered: (day: DayKey, origin: Origin)? {
        didSet { markHovered(hovered?.day) }
    }
    /// The day the chart's keyboard cursor sits on while the chart has focus.
    var keyboardDay: DayKey?
    var selection: Set<DayKey> = []
    /// Anchor of shift-extended selections.
    var anchor: DayKey?
    /// A request to scroll the table to a day and flash it; the id makes every request unique.
    private(set) var reveal: (day: DayKey, id: Int)?
    private(set) var flash: (day: DayKey, id: Int)?
    @ObservationIgnored private var revealCount = 0
    @ObservationIgnored private var pendingTableHover: TableHoverTarget?
    @ObservationIgnored private var tableHoverScheduled = false
    /// Per-row hover flags: rows read their own, so a hover change redraws two row backgrounds, never every row.
    @ObservationIgnored private var rowMarks: [DayKey: HistoryRowMark] = [:]
    @ObservationIgnored private var markedDay: DayKey?

    var hoveredDay: DayKey? { hovered?.day }

    func hover(_ day: DayKey?, from origin: Origin) {
        // An actual chart entry takes precedence over a queued table notification. A chart exit may arrive after
        // the pointer has entered a table row, so it must leave that queued entry intact.
        if day != nil { pendingTableHover = nil }
        if let day {
            if hovered?.day != day || hovered?.origin != origin { hovered = (day, origin) }
        } else if hovered?.origin == origin {
            hovered = nil
        }
    }

    /// SwiftUI may send a row's exit and re-entry while updating its help/hover responders. Publishing each edge
    /// synchronously invalidates the same lazy rows during that update and can keep the main thread relaying out.
    /// Apply only the final pointer target after the current update, preserving a newly entered row on a late exit.
    func trackTableHover(_ day: DayKey, inside: Bool) {
        if inside {
            pendingTableHover = .day(day)
        } else {
            let current: DayKey?
            switch pendingTableHover {
            case .day(let pending): current = pending
            case .outside: current = nil
            case nil: current = hovered?.origin == .table ? hovered?.day : nil
            }
            guard current == day else { return }
            pendingTableHover = .outside
        }
        guard !tableHoverScheduled else { return }
        tableHoverScheduled = true
        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            self.tableHoverScheduled = false
            let target = self.pendingTableHover
            self.pendingTableHover = nil
            switch target {
            case .day(let day): self.hover(day, from: .table)
            case .outside:
                if self.hovered?.origin == .table { self.hovered = nil }
            case nil: break
            }
        }
    }

    /// The hover flag of `day`'s row; reading it never subscribes a row to the shared hover state.
    func mark(for day: DayKey) -> HistoryRowMark {
        if let mark = rowMarks[day] { return mark }
        let mark = HistoryRowMark(hovered: markedDay == day)
        rowMarks[day] = mark
        return mark
    }

    private func markHovered(_ day: DayKey?) {
        guard day != markedDay else { return }
        if let previous = markedDay { rowMarks[previous]?.hovered = false }
        if let day { rowMarks[day]?.hovered = true }
        markedDay = day
    }

    func revealInTable(_ day: DayKey) {
        revealCount += 1
        reveal = (day, revealCount)
        let id = revealCount
        // The flash starts once the scroll has brought the row on screen.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.32) { [weak self] in
            guard let self, self.reveal?.id == id else { return }
            self.flash = (day, id)
        }
    }

    func reset() {
        pendingTableHover = nil
        hovered = nil
        keyboardDay = nil
        selection = []
        anchor = nil
    }
}

/// Whether one table row is hovered, from the table or from the chart.
@MainActor
@Observable
final class HistoryRowMark {
    fileprivate(set) var hovered: Bool

    init(hovered: Bool) {
        self.hovered = hovered
    }
}

enum HistoryMotion {
    /// Bar heights between ranges and sources (DESIGN 4b: spring, at most 0.45 s).
    static let chart = Animation.spring(response: 0.42, dampingFraction: 0.86)
    static let fade = Animation.easeOut(duration: 0.2)
    static let scroll = Animation.easeInOut(duration: 0.35)
}
