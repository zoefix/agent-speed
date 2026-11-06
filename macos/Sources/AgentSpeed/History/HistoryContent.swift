import AgentSpeedCore
import Foundation

/// One segment of a day's bar: a source's output (or cost, in display units) stacked on the ones below it.
struct ChartSegment: Identifiable, Equatable, Sendable {
    var id: String { "\(day)/\(source.rawValue)" }
    let day: DayKey
    let source: SourceKind
    let value: Double
    /// Cumulative value below and at the top of the segment.
    let lower: Double
    let upper: Double
    /// Value of the segment stacked on this one (0 for the top).
    let above: Double
    /// Traffic: an upload segment, stacked downward below the axis (its values are negative).
    var upload = false

    var isTop: Bool { above == 0 }
}

/// Everything the chart draws for one filter.
struct HistoryChartModel: Equatable, Sendable {
    let range: DayRange
    let today: DayKey
    /// Daily output with the speed line, or daily cost without it (DESIGN 9).
    var metric: HistoryChartMetric = .output
    /// Bottom first.
    let stack: [SourceKind]
    let isSingleSource: Bool
    let segments: [ChartSegment]
    /// One value per day of the range (nil where nothing was measured); all nil for the cost metric.
    let speeds: [Double?]
    /// The left axis: output tokens, or cost in the display currency.
    let outputTicks: AxisTicks
    let speedTicks: AxisTicks
    let days: [DayKey: DaySummary]
    /// Formats the cost metric's axis and tooltips.
    var money: HistoryMoney?
    /// Traffic (DESIGN 10): the axis below zero (upload), each day's traffic, this computer's totals for the outline
    /// bars (all sources only), and the last day before measuring began.
    var lowerTicks: AxisTicks?
    var traffic: [DayKey: DayTraffic] = [:]
    var notRecordedThrough: DayKey?

    /// The y domain: 0…max, or −upload…download for traffic.
    var yDomain: ClosedRange<Double> { -(lowerTicks?.max ?? 0)...outputTicks.max }

    /// Axis values on both sides of zero.
    var axisValues: [Double] {
        (lowerTicks.map { ticks in ticks.values.dropFirst().reversed().map { -$0 } } ?? []) + outputTicks.values
    }

    var speedRuns: [Range<Int>] { HistoryChartMath.runs(speeds) }

    func summary(at index: Int) -> DaySummary? {
        guard index >= 0, index < range.count else { return nil }
        return days[range.day(at: index)]
    }

    /// Speed mapped onto the output axis, so both share one plot.
    func scaledSpeed(_ speed: Double) -> Double {
        speedTicks.max > 0 ? speed / speedTicks.max * outputTicks.max : 0
    }

    /// The left axis label at `value`: output, cost in the display currency, or traffic.
    func leadingAxisLabel(_ value: Double) -> String {
        if metric == .traffic {
            // The outermost labels name their side: download above the axis, upload below.
            guard value != 0 else { return "0" }
            let text = HistoryText.axisBytes(value)
            if value == outputTicks.max { return "↓ " + text }
            if value == -(lowerTicks?.max ?? 0) { return "↑ " + text }
            return text
        }
        guard metric == .cost, let money else { return HistoryText.axisNumber(value) }
        return money.axisLabel(value, step: outputTicks.step)
    }

    /// The right axis label at the output axis' `value`: the speed it stands for (output only, DESIGN 9).
    func trailingAxisLabel(_ value: Double) -> String {
        HistoryText.axisNumber(outputTicks.max > 0 ? value / outputTicks.max * speedTicks.max : 0)
    }
}

/// Everything the window shows for one filter, computed once per change of the data or the filter.
struct HistoryContent: Equatable, Sendable {
    let today: DayKey
    let source: SourceKind?
    let rangeKind: HistoryRangeKind
    let range: DayRange
    /// First and last day with data for the selected source.
    let dataBounds: DayRange?
    /// Sources the picker offers: those with history, plus the selected one.
    let pickerSources: [SourceKind]
    /// Days with data in the range, oldest first.
    let days: [DaySummary]
    let cards: [SummaryCard]
    let chart: HistoryChartModel
    /// The single day split by source or model; nil for ranges of more than one day, which are charted instead.
    let breakdown: DayBreakdown?
    let columns: [HistoryColumn]
    let sort: HistorySort
    /// The table rows in the chosen order.
    let rows: [DaySummary]
    /// Money in the display currency and how costs count (DESIGN 9).
    var money: HistoryMoney
    var costOptions: HistoryCostOptions
    /// Why the range's unpriced requests have no price, with the current table (`history.unpricedNotice`, METRICS 9.10).
    var unpriced: UnpricedView = .empty
    /// The first day traffic was recorded, when the range starts before it (`history.traffic.since`).
    var trafficSince: DayKey?
    /// The range has token usage, and agent traffic, of the selection (DESIGN 10, Traffic without token usage).
    var hasTokenUsage = true
    var hasAgentTraffic = false
    /// The selected host (every host when nil) and the hosts the picker offers, empty while no remote host has
    /// history (DESIGN 13).
    var host: String?
    var hosts: [HistoryHost] = []

    var isEmpty: Bool { days.isEmpty }

    /// The name of the selected host (`remote.local` for this Mac); nil for every host.
    @MainActor
    func hostName(_ l10n: Localizer) -> String? {
        guard let host else { return nil }
        if host == RemoteHost.localId { return l10n("remote.local") }
        return hosts.first { $0.id == host }?.name ?? host
    }

    /// The CSV's trailing `host` value, only while host history exists (DESIGN 13).
    var csvHost: String? {
        hosts.isEmpty ? nil : HistoryHosts.csvValue(host, hosts: hosts)
    }

    /// Only traffic to show: a chart of output or cost would be empty (DESIGN 10, Opening on traffic).
    var showsOnlyTraffic: Bool { !hasTokenUsage && hasAgentTraffic }

    /// Requests of the range whose model has no known price.
    var unpricedRequests: Int { unpriced.unpriced }

    /// The range's displayed traffic (DESIGN 10: the traffic chart's header); nil when nothing of it was recorded.
    var trafficTotal: TrafficBytes? {
        guard chart.metric == .traffic, !chart.traffic.isEmpty else { return nil }
        return chart.traffic.values.reduce(TrafficBytes.zero) { $0 + $1.shown }
    }

    /// At least one source is marked as a subscription: the toolbar offers `history.includeSubscriptions`.
    var offersSubscriptionToggle: Bool { !costOptions.marked.isEmpty }

    /// The range of days the custom range pickers allow.
    var selectableBounds: DayRange {
        DayRange(dataBounds.map { min($0.start, today) } ?? today.adding(days: -29), today)
    }

    /// `host` filters the rows to one host (every host when nil), combined with `source`; `hostNames` names the
    /// remote hosts (`HistoryStore.hostNames` and the settings). This Mac alone is every host while no remote host has
    /// history.
    @MainActor
    static func build(
        usage all: [DailyUsage],
        source: SourceKind?,
        host selectedHost: String? = nil,
        hostNames: [String: String] = [:],
        rangeKind: HistoryRangeKind,
        customRange: DayRange?,
        sort: HistorySort,
        today: DayKey,
        l10n: Localizer,
        money: HistoryMoney = .usd,
        costOptions: HistoryCostOptions = HistoryCostOptions(),
        traffic: HistoryTraffic = .none,
        prices: PriceTable = PriceBook.bundled,
        now: Date = Date()
    ) -> HistoryContent {
        let hosts = HistoryHosts.withHistory(all, names: hostNames, selected: selectedHost)
        let host = hosts.isEmpty ? nil : selectedHost
        let usage = HistoryAggregator.records(all, host: host)
        // Agent traffic is this computer's own (REMOTE 7): a remote host has none.
        let remoteHost = host.map { $0 != RemoteHost.localId } ?? false
        let traffic = remoteHost ? HistoryTraffic.none : traffic
        // Recorded agent traffic is history too (DESIGN 10): its days count for the bounds, the rows and the picker.
        let trafficDays = traffic.agentDays(source: source)
        let bounds = union(HistoryAggregator.bounds(usage, source: source), trafficDays.first.map { DayRange($0, trafficDays[trafficDays.count - 1]) })
        let range = HistoryRanges.resolve(rangeKind, today: today, firstDataDay: bounds?.start, custom: customRange)
        let previousRange = HistoryRanges.previous(range)
        let sparkRange = HistoryRanges.sparkRange(range)
        let window = DayRange(min(previousRange.start, sparkRange.start), range.end)
        let excluded = costOptions.excluded(for: source)
        let loaded = HistoryAggregator.daily(usage, source: source, host: host, range: window, excluded: excluded)
        let calendar = HistoryDates.calendar
        let tokenDays = loaded.filter { range.contains($0.day) }
        let tokenKeys = Set(tokenDays.map(\.day))
        let trafficOnly = trafficDays.filter { range.contains($0) && !tokenKeys.contains($0) }.map(DaySummary.trafficOnly)
        let days = (tokenDays + trafficOnly).sorted { $0.day < $1.day }.map { summary -> DaySummary in
            var summary = summary
            summary.traffic = traffic.day(summary.day, source: source, today: today, now: now, calendar: calendar)
            return summary
        }
        let previous = loaded.filter { previousRange.contains($0.day) }
        let sparkDays = loaded.filter { sparkRange.contains($0.day) }

        let withHistory = Set(HistoryAggregator.sourcesWithHistory(all)).union(traffic.sourcesWithTraffic)
        var pickerSources = SourceKind.allCases.filter(withHistory.contains)
        if let source, !pickerSources.contains(source) {
            pickerSources = SourceKind.allCases.filter { pickerSources.contains($0) || $0 == source }
        }
        let columns = HistoryColumn.visible(allSources: source == nil, traffic: traffic.hasData || traffic.since != nil)
        let effectiveSort = columns.contains(sort.column) ? sort : HistorySort()
        let costTitleKey = source.map { costOptions.marked.contains($0) } == true ? "history.summary.costApi" : "history.summary.cost"
        return HistoryContent(
            today: today,
            source: source,
            rangeKind: rangeKind,
            range: range,
            dataBounds: bounds,
            pickerSources: pickerSources,
            days: days,
            cards: HistorySummary.build(days: days, previous: previous, sparkDays: sparkDays, range: range, today: today,
                                        money: money, costTitleKey: costTitleKey, l10n),
            chart: costOptions.metric == .traffic
                ? trafficChart(range: range, today: today, source: source, traffic: traffic, now: now, days: days)
                : chartModel(days: days, range: range, today: today, source: source, metric: costOptions.metric, money: money),
            breakdown: range.isSingleDay
                ? HistoryBreakdown.build(usage, day: range.start, source: source, metric: costOptions.metric, excluded: excluded,
                                         traffic: traffic.day(range.start, source: source, today: today, now: now, calendar: calendar))
                : nil,
            columns: columns,
            sort: effectiveSort,
            rows: HistoryTable.sort(days, by: effectiveSort),
            money: money,
            costOptions: costOptions,
            unpriced: HistoryAggregator.unpriced(usage, source: source, rangeKind: rangeKind, range: range, excluded: excluded,
                                                 table: prices),
            trafficSince: traffic.since.flatMap { $0 > range.start && $0 <= range.end ? $0 : nil },
            hasTokenUsage: !tokenDays.isEmpty,
            hasAgentTraffic: trafficDays.contains(where: range.contains),
            host: host,
            hosts: hosts
        )
    }

    private static func union(_ a: DayRange?, _ b: DayRange?) -> DayRange? {
        guard let a else { return b }
        guard let b else { return a }
        return DayRange(min(a.start, b.start), max(a.end, b.end))
    }

    /// The same content in another row order.
    @MainActor
    func sorted(by sort: HistorySort) -> HistoryContent {
        let sort = columns.contains(sort.column) ? sort : HistorySort()
        return HistoryContent(
            today: today, source: source, rangeKind: rangeKind, range: range, dataBounds: dataBounds,
            pickerSources: pickerSources, days: days, cards: cards, chart: chart, breakdown: breakdown, columns: columns, sort: sort,
            rows: HistoryTable.sort(days, by: sort), money: money, costOptions: costOptions, unpriced: unpriced,
            trafficSince: trafficSince, hasTokenUsage: hasTokenUsage, hasAgentTraffic: hasAgentTraffic, host: host, hosts: hosts
        )
    }

    /// Bars of output (with the speed line) or of cost in the display currency (without it), stacked by source.
    private static func chartModel(
        days: [DaySummary], range: DayRange, today: DayKey, source: SourceKind?, metric: HistoryChartMetric, money: HistoryMoney
    ) -> HistoryChartModel {
        let stack = source.map { [$0] } ?? HistoryChartMath.stackOrder(days, metric: metric)
        var segments: [ChartSegment] = []
        var maxValue = 0.0
        func value(_ day: DaySummary, _ kind: SourceKind) -> Double {
            switch metric {
            case .output: Double(source == nil ? day.output(of: kind) : day.output)
            case .cost: money.value(source == nil ? day.costMicroUsd(of: kind) : day.cost.microUsd)
            case .traffic: 0
            }
        }
        for day in days {
            var lower = 0.0
            let parts = stack.compactMap { kind -> (SourceKind, Double)? in
                let amount = value(day, kind)
                return amount > 0 ? (kind, amount) : nil
            }
            for (index, part) in parts.enumerated() {
                segments.append(ChartSegment(
                    day: day.day, source: part.0, value: part.1,
                    lower: lower, upper: lower + part.1, above: index + 1 < parts.count ? parts[index + 1].1 : 0
                ))
                lower += part.1
            }
            maxValue = max(maxValue, lower)
        }
        let speeds = metric == .output
            ? HistoryAggregator.series(days, range: range, missing: nil) { $0.averageSpeed }
            : Array(repeating: nil, count: max(range.count, 0))
        let outputTicks = HistoryChartMath.ticks(maxValue * 1.04, maxIntervals: 4)
        let maxSpeed = speeds.compactMap { $0 }.max() ?? 0
        return HistoryChartModel(
            range: range,
            today: today,
            metric: metric,
            stack: stack,
            isSingleSource: source != nil,
            segments: segments,
            speeds: speeds,
            outputTicks: outputTicks,
            speedTicks: HistoryChartMath.alignedTicks(maxSpeed * 1.04, intervals: outputTicks.intervals),
            days: Dictionary(uniqueKeysWithValues: days.map { ($0.day, $0) }),
            money: money
        )
    }
}

extension HistoryContent {
    /// Mirrored daily traffic (DESIGN 10, Usage history): download above the axis stacked by source, upload below it in
    /// the same colors; one scale for both sides; no speed line. Days before measuring began are marked, never 0.
    @MainActor
    fileprivate static func trafficChart(
        range: DayRange, today: DayKey, source: SourceKind?, traffic: HistoryTraffic, now: Date, days: [DaySummary]
    ) -> HistoryChartModel {
        let calendar = HistoryDates.calendar
        var perDay: [DayKey: DayTraffic] = [:]
        var totals: [SourceKind: Int64] = [:]
        for day in range.days {
            guard let value = traffic.day(day, source: source, today: today, now: now, calendar: calendar) else { continue }
            perDay[day] = value
            for (kind, bytes) in value.bySource { totals[kind, default: 0] += bytes.sum }
        }
        let stack = source.map { [$0] } ?? totals.filter { $0.value > 0 }.sorted { lhs, rhs in
            lhs.value != rhs.value ? lhs.value > rhs.value
                : (SourceKind.allCases.firstIndex(of: lhs.key) ?? 0) < (SourceKind.allCases.firstIndex(of: rhs.key) ?? 0)
        }.map(\.key)
        var segments: [ChartSegment] = []
        var down = 0.0
        var up = 0.0
        for day in range.days {
            guard let value = perDay[day] else { continue }
            let parts = stack.compactMap { kind -> (SourceKind, TrafficBytes)? in value.bySource[kind].map { (kind, $0) } }
            var lower = 0.0
            let downs = parts.filter { $0.1.rx > 0 }
            for (index, part) in downs.enumerated() {
                let amount = Double(part.1.rx)
                segments.append(ChartSegment(day: day, source: part.0, value: amount, lower: lower, upper: lower + amount,
                                             above: index + 1 < downs.count ? Double(downs[index + 1].1.rx) : 0))
                lower += amount
            }
            var depth = 0.0
            let ups = parts.filter { $0.1.tx > 0 }
            for (index, part) in ups.enumerated() {
                let amount = Double(part.1.tx)
                segments.append(ChartSegment(day: day, source: part.0, value: amount, lower: -(depth + amount), upper: -depth,
                                             above: index + 1 < ups.count ? Double(ups[index + 1].1.tx) : 0, upload: true))
                depth += amount
            }
            let machine = value.machine ?? .zero
            down = max(down, lower, Double(machine.rx))
            up = max(up, depth, Double(machine.tx))
        }
        let step = HistoryChartMath.niceStep(max(down + up, 1) * 1.04 / 4)
        let upper = AxisTicks(step: step, intervals: max(1, Int(ceil(down * 1.04 / step - 1e-9))))
        // Upload is usually far smaller than download: below one step the lower side takes the smallest fraction of a
        // step that holds it, so the chart does not give a quarter of its height to an empty band.
        let lowerIntervals = Int(ceil(up * 1.04 / step - 1e-9))
        let lowerStep = lowerIntervals <= 1 ? ([0.2, 0.25, 0.5, 1].map { $0 * step }.first { $0 >= up * 1.04 } ?? step) : step
        let firstRecorded = traffic.since
        let notRecorded = firstRecorded.map { $0.adding(days: -1) } ?? range.end
        return HistoryChartModel(
            range: range,
            today: today,
            metric: .traffic,
            stack: stack,
            isSingleSource: source != nil,
            segments: segments,
            speeds: Array(repeating: nil, count: max(range.count, 0)),
            outputTicks: upper,
            speedTicks: upper,
            days: Dictionary(uniqueKeysWithValues: days.map { ($0.day, $0) }),
            money: nil,
            lowerTicks: lowerIntervals > 0 ? AxisTicks(step: lowerStep, intervals: lowerIntervals) : nil,
            traffic: perDay,
            notRecordedThrough: notRecorded >= range.start ? min(notRecorded, range.end) : nil
        )
    }
}

extension HistoryMoney {
    /// US dollars in English, for checks and callers without a display context.
    static let usd: HistoryMoney = {
        let usd = CurrencyTable.bundled.currencyOrUSD(Currency.usdCode)
        return HistoryMoney(currency: usd, usd: usd, language: "en", rate: 1, currencyName: "US Dollar")
    }()
}
