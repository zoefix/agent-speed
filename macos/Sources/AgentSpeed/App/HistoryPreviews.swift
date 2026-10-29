import AgentSpeedCore
import AppKit
import SwiftUI

/// Usage History renders for `--render-previews`: the real window (toolbar included) in both appearances, at the
/// default and the minimum size, for all sources and one source, empty and while analysing, plus the whole
/// scrolling column with the table.
@MainActor
enum HistoryPreviews {
    struct Case {
        let name: String
        var size = HistoryWindowController.defaultSize
        var source: SourceKind?
        var range: HistoryRangeKind = .days30
        var custom: (Int, Int)?
        var days: Days = .full
        var state: State = .ready
        var language: LanguagePreference = .zhHant
        var hoverDaysAgo: Int?
        var selectDaysAgo: [Int] = []
        var sort: HistoryColumn?
        /// Renders the full scrolling column instead of the window.
        var tall = false
        /// Scrolls the window to the end of the table, where its column titles stay at the top.
        var scrolled = false
        /// DESIGN 9: display currency, chart metric, sources marked as subscriptions and the window's choice.
        var currency = "USD"
        var metric: HistoryChartMetric = .output
        var marked: Set<SourceKind> = []
        var includeSubscriptions = false
        /// DESIGN 10: a source whose token usage is left out (it only used the network), and sources turned off.
        var tokenless: SourceKind?
        var disabled: Set<SourceKind> = []
        /// DESIGN 12: the performance records of 性能表現 (none while the first analysis of this version runs).
        var perf = true
    }

    enum Days { case full, none, recent(Int) }
    enum State { case ready, analyzing(Double, Int, Int), notLoaded }

    static let cases: [Case] = [
        Case(name: "all-wide"),
        Case(name: "all-min", size: HistoryWindowController.minimumSize),
        Case(name: "source-wide", source: .claudeCode, range: .days90),
        Case(name: "source-min", size: HistoryWindowController.minimumSize, source: .deepseek, range: .days7),
        Case(name: "source-omp", source: .omp, hoverDaysAgo: 3),
        Case(name: "source-antigravity", size: HistoryWindowController.minimumSize, source: .antigravity, language: .en),
        // Every source in the legend, both WorkBuddy editions included, in each language.
        Case(name: "legend-zh-hans", language: .zhHans),
        Case(name: "legend-ja", language: .ja),
        Case(name: "legend-en-min", size: HistoryWindowController.minimumSize, language: .en),
        Case(name: "legend-zh-hant-min", size: HistoryWindowController.minimumSize),
        Case(name: "source-workbuddy", source: .workbuddy, range: .days7),
        Case(name: "source-workbuddy-ai", size: HistoryWindowController.minimumSize, source: .workbuddyAi, language: .ja),
        Case(name: "today-workbuddy-en", source: .workbuddy, range: .today, language: .en),
        Case(name: "year", range: .year),
        Case(name: "all-range", range: .all),
        Case(name: "custom", range: .custom, custom: (52, 38)),
        Case(name: "hover", hoverDaysAgo: 6, selectDaysAgo: [2, 3]),
        Case(name: "hover-today", source: .codex, range: .days7, hoverDaysAgo: 0),
        Case(name: "empty", source: .gemini, days: .none),
        Case(name: "analyzing", days: .recent(9), state: .analyzing(0.42, 128, 304)),
        Case(name: "analyzing-empty", days: .none, state: .analyzing(0.18, 54, 304)),
        Case(name: "en", language: .en),
        Case(name: "ja", size: HistoryWindowController.minimumSize, language: .ja),
        Case(name: "zh-hans", source: .opencode, range: .days90, language: .zhHans),
        Case(name: "today", range: .today),
        Case(name: "yesterday", range: .yesterday),
        Case(name: "today-source", source: .claudeCode, range: .today),
        Case(name: "yesterday-source", source: .claudeDesktop, range: .yesterday),
        Case(name: "yesterday-en-min", size: HistoryWindowController.minimumSize, range: .yesterday, language: .en),
        Case(name: "today-ja-min", size: HistoryWindowController.minimumSize, source: .omp, range: .today, language: .ja),
        Case(name: "today-zh-hans-min", size: HistoryWindowController.minimumSize, range: .today, language: .zhHans),
        Case(name: "table", tall: true),
        // 性能表現 (DESIGN 12): one source chosen, the minimum width in English, a single day, and the first re-analysis.
        Case(name: "perf-source", source: .claudeCode, tall: true),
        Case(name: "perf-min-en", size: HistoryWindowController.minimumSize, language: .en, tall: true),
        Case(name: "perf-yesterday", range: .yesterday, tall: true),
        Case(name: "perf-analyzing", state: .analyzing(0.36, 112, 304), tall: true, perf: false),
        Case(name: "table-sorted", source: .claudeDesktop, sort: .speed, tall: true),
        // Scrolled into the table: the column titles stay pinned under the toolbar (issue #21 keeps them in place).
        Case(name: "table-scrolled", sort: .cost, scrolled: true),
        Case(name: "table-scrolled-hover", hoverDaysAgo: 3, selectDaysAgo: [5], sort: .date, scrolled: true),
        Case(name: "table-scrolled-min", size: HistoryWindowController.minimumSize, language: .en, scrolled: true),
        Case(name: "table-scrolled-year", range: .year, sort: .cost, scrolled: true, currency: "TWD"),
        // Cost (DESIGN 9).
        Case(name: "cost-en", language: .en, metric: .cost),
        Case(name: "cost-twd-hover", hoverDaysAgo: 4, currency: "TWD", metric: .cost),
        Case(name: "cost-output-hover-en", range: .days7, language: .en, hoverDaysAgo: 2, currency: "TWD"),
        Case(name: "cost-cny-min", size: HistoryWindowController.minimumSize, language: .zhHans, currency: "CNY", metric: .cost),
        Case(name: "cost-jpy-ja", source: .claudeCode, range: .days90, language: .ja, currency: "JPY", metric: .cost),
        Case(name: "cost-subscriptions-en", language: .en, metric: .cost, marked: [.claudeDesktop, .codex]),
        Case(name: "cost-subscriptions-min", size: HistoryWindowController.minimumSize, metric: .cost, marked: [.claudeDesktop]),
        Case(name: "cost-subscription-source", source: .claudeDesktop, currency: "TWD", metric: .cost, marked: [.claudeDesktop]),
        Case(name: "cost-breakdown", range: .yesterday, currency: "TWD", metric: .cost, marked: [.codex]),
        Case(name: "cost-breakdown-model-en", source: .claudeCode, range: .yesterday, language: .en, metric: .cost),
        Case(name: "cost-table", sort: .cost, tall: true, currency: "TWD"),
        Case(name: "cost-table-min", size: HistoryWindowController.minimumSize, language: .en, sort: .cost, tall: true,
             currency: "JPY", marked: [.codex]),
        Case(name: "cost-old-days", source: .claudeDesktop, range: .all, language: .en, sort: .date, tall: true, metric: .cost),
        // Traffic (DESIGN 10): mirrored bars, this computer's outline, days before measuring began.
        Case(name: "traffic", metric: .traffic),
        Case(name: "traffic-hover-en", language: .en, hoverDaysAgo: 3, metric: .traffic),
        Case(name: "traffic-not-recorded", range: .days90, metric: .traffic),
        Case(name: "traffic-source", source: .claudeCode, language: .en, hoverDaysAgo: 0, metric: .traffic),
        Case(name: "traffic-min-ja", size: HistoryWindowController.minimumSize, language: .ja, metric: .traffic),
        Case(name: "traffic-breakdown", range: .yesterday, metric: .traffic),
        Case(name: "traffic-table", sort: .traffic, tall: true, metric: .traffic),
        // A source that only used the network opens on traffic; one day of it is its one bar; a disabled source is gone.
        Case(name: "traffic-only-en", source: .antigravity, language: .en, tokenless: .antigravity),
        Case(name: "traffic-only-yesterday", source: .antigravity, range: .yesterday, tokenless: .antigravity),
        Case(name: "traffic-only-table-ja", source: .antigravity, language: .ja, tall: true, tokenless: .antigravity),
        Case(name: "traffic-disabled", metric: .traffic, disabled: [.claudeCode, .antigravity]),
    ]

    /// Deterministic rates for previews (the shape of an open.er-api.com response).
    static let previewRates = ExchangeRateSet(
        provider: "open.er-api.com",
        url: "https://open.er-api.com/v6/latest/USD",
        dateMs: 1_792_627_200_000,
        fetchedMs: 1_792_630_000_000,
        rates: ["USD": 1, "CNY": Decimal(string: "6.689327")!, "TWD": Decimal(string: "32.411205")!,
                "JPY": Decimal(string: "158.100039")!, "EUR": Decimal(string: "0.890292")!]
    )

    static func render(model: AppModel, into directory: URL) {
        let only = ProcessInfo.processInfo.environment["AGENT_SPEED_HISTORY_CASES"]?.split(separator: ",").map(String.init)
        let original = model.settings.language
        for item in cases where only?.contains(item.name) ?? true {
            let viewModel = prepare(item, model: model)
            for dark in [false, true] {
                let url = directory.appendingPathComponent("history-\(item.name)-\(dark ? "dark" : "light").png")
                if item.tall {
                    renderColumn(viewModel: viewModel, model: model, width: item.size.width, dark: dark, to: url)
                } else {
                    renderWindow(viewModel: viewModel, model: model, size: item.size, dark: dark, scrolled: item.scrolled, to: url)
                }
            }
        }
        if only?.contains("unpriced-popover") ?? true {
            renderUnpricedPopovers(model: model, into: directory)
        }
        if only?.contains("popover") ?? true {
            let viewModel = prepare(Case(name: "popover", range: .custom, custom: (40, 12)), model: model)
            let content = viewModel.content
            for dark in [false, true] {
                PreviewSnapshot.writeHosted(
                    CustomRangeView(bounds: content.selectableBounds, initial: content.range, l10n: model.l10n) { _, _ in }
                        .appEnvironment(model),
                    dark: dark,
                    to: directory.appendingPathComponent("history-custom-popover-\(dark ? "dark" : "light").png")
                )
            }
        }
        model.settings.language = original
        model.l10n.apply(original)
        model.settings.currency = "USD"
        model.settings.subscriptionSources = []
        model.settings.enabledSources = Set(SourceKind.allCases)
        model.settings.storage.removeObject(forKey: HistoryViewModel.includeSubscriptionsKey)
        model.rates.preview(rates: nil, currency: "USD")
        model.history.publish(state: .notLoaded, days: [])
    }

    /// The unknown-prices popover (DESIGN 9) over facts like this Mac's and the Windows box's: router aliases and an
    /// internal model (no public price), a model the table lacks, a Fast request without a Fast price, a response
    /// without a model, a long lookup key, more than 20 rows in one group and requests priced now; in every language
    /// and both appearances, with the copy button's states.
    private static func renderUnpricedPopovers(model: AppModel, into directory: URL) {
        let languages: [(LanguagePreference, String)] = [(.zhHant, "TWD"), (.zhHans, "CNY"), (.en, "USD"), (.ja, "JPY")]
        for (language, currency) in languages {
            let viewModel = prepare(Case(name: "unpriced-popover", range: .days90, language: language, currency: currency), model: model,
                                    unpriced: true)
            let view = viewModel.content.unpriced
            let states: [(String, CopyDiagnosticsButton.Outcome?)] = [("", nil), ("-copied", .copied), ("-failed", .failed)]
            for dark in [false, true] {
                for (suffix, state) in states {
                    PreviewSnapshot.writeHosted(
                        UnpricedPopover(view: view, l10n: model.l10n, copyState: state).appEnvironment(model),
                        dark: dark,
                        to: directory.appendingPathComponent(
                            "history-unpriced-popover-\(language.rawValue)\(suffix)-\(dark ? "dark" : "light").png")
                    )
                }
            }
            if language == .zhHant || language == .en {
                for dark in [false, true] {
                    PreviewSnapshot.writeHosted(
                        UnpricedPopover(view: view, l10n: model.l10n, unboundedGroups: true).appEnvironment(model),
                        dark: dark,
                        to: directory.appendingPathComponent("history-unpriced-popover-\(language.rawValue)-all-\(dark ? "dark" : "light").png")
                    )
                }
            }
            if language == .zhHant {
                let text = UnpricedText.render(view, app: .current(version: AppInfo.version))
                try? text.write(to: directory.appendingPathComponent("history-unpriced-copied.txt"), atomically: true, encoding: .utf8)
                renderWindow(viewModel: viewModel, model: model, size: HistoryWindowController.minimumSize, dark: false,
                             to: directory.appendingPathComponent("history-unpriced-window-light.png"))
            }
        }
    }

    /// Recent days carry the facts of the popover preview; their costs count them as unpriced.
    static func withUnpricedFacts(_ days: [DailyUsage], today: DayKey) -> [DailyUsage] {
        func fact(_ model: String, _ provider: String?, fast: Bool = false, _ requests: Int) -> UnpricedFact {
            UnpricedFact(key: UnpricedFactKey(model: model, provider: provider, fast: fast), requests: requests)
        }
        let aliases = ["ab", "f1097c63", "7", "routed-1", "routed-2", "q4", "zz", "k", "mini", "b2", "c3", "d4", "e5"]
        // Per source, the facts of its most recent days before today, one list per day.
        var pending: [SourceKind: [[UnpricedFact]]] = [
            .codex: [
                aliases.enumerated().map { fact("neko-model-\($1)", "neko-route", 70 - $0 * 4) }
                    + [fact("codex-auto-review", "openai", 27), fact("mimo-v2.5-pro", "custom", 115)],
                [fact("", "openai", 4), fact("gpt-6.1-sol-preview", "openai", 9), fact("o9-research", "azure", 3),
                 fact("my-proxy/sonnet", "custom", 6), fact("internal-eval", "openai", 2), fact("glm-5.3-air", "zhipu", 5),
                 fact("qwen3.6-plus-0915", "dashscope", 8), fact("mimo-v2.5-flash", "custom", 12)],
            ],
            .claudeCode: [[fact("claude-sonnet-5-5", nil, fast: true, 12)]],
            .workbuddyAi: [[fact("Accounts/Fireworks/Models/Qwen3-Coder-480B-A35B-Instruct-Preview-Long-Context-2026", nil, 3),
                            fact("Kimi-K3-Thinking-Turbo", "custom", 2)]],
            .antigravity: [[fact("gemini-3.1-pro", nil, 6)]],
        ]
        var result = days
        for index in result.indices.reversed() {
            let day = result[index]
            guard let key = DayKey(day.day), key < today, day.cost != nil, let facts = pending[day.source]?.first else { continue }
            pending[day.source]?.removeFirst()
            let added = facts.reduce(0) { $0 + $1.requests }
            var tally: [UnpricedFactKey: Int] = [:]
            for fact in day.unpriced.facts + facts { tally[fact.key, default: 0] += fact.requests }
            result[index].totals.requests += added
            result[index].cost?.unpricedRequests += added
            result[index].unpriced = UnpricedReading(requests: day.unpriced.requests + added, facts: UnpricedFact.list(tally),
                                                     derived: day.unpriced.derived)
        }
        return result
    }

    /// Fills the store and builds a view model in the case's state.
    static func prepare(_ item: Case, model: AppModel, unpriced: Bool = false) -> HistoryViewModel {
        let calendar = HistoryDates.calendar
        let now = Date()
        let all = PreviewHistory.days(endingAt: now, count: 160, calendar: calendar)
        model.settings.language = item.language
        model.l10n.apply(item.language)
        let stored: [DailyUsage] = switch item.days {
        case .full: all
        case .none: []
        case .recent(let count):
            all.filter { DayKey($0.day).map { $0 >= DayKey(now, calendar: calendar).adding(days: -count) } ?? false }
        }
        let kept = stored.filter { $0.source != item.tokenless && !item.disabled.contains($0.source) }
        let days = unpriced ? withUnpricedFacts(kept, today: DayKey(now, calendar: calendar)) : kept
        model.settings.enabledSources = Set(SourceKind.allCases).subtracting(item.disabled)
        let state: HistoryState = switch item.state {
        case .ready: .ready(updatedAt: now.addingTimeInterval(-240))
        case .analyzing(let progress, let done, let total): .analyzing(progress: progress, filesDone: done, filesTotal: total)
        case .notLoaded: .notLoaded
        }
        model.history.publish(state: state, days: days)
        let perf = days.isEmpty || !item.perf ? [] : PerfPreviewData.historyRecords(today: DayKey(now, calendar: calendar))
        model.history.publish(perfRecords: perf.filter { !item.disabled.contains($0.source) })
        TrafficPreviewData.publish(into: model.traffic, now: now)
        model.settings.currency = item.currency
        model.rates.preview(rates: previewRates, currency: item.currency)
        model.settings.subscriptionSources = item.marked
        model.settings.storage.set(item.includeSubscriptions, forKey: HistoryViewModel.includeSubscriptionsKey)
        let viewModel = HistoryViewModel(store: model.history, l10n: model.l10n, accessibility: model.accessibility, cost: model.cost,
                                         traffic: model.traffic)
        viewModel.select(metric: item.metric)
        viewModel.select(source: item.source)
        if item.range == .custom, let (start, end) = item.custom {
            let today = DayKey(now, calendar: calendar)
            viewModel.applyCustom(from: today.adding(days: -start), to: today.adding(days: -end))
        } else {
            viewModel.select(range: item.range)
        }
        if let sort = item.sort { viewModel.toggleSort(sort) }
        let today = DayKey(now, calendar: calendar)
        if let ago = item.hoverDaysAgo { viewModel.interaction.hover(today.adding(days: -ago), from: .chart) }
        viewModel.interaction.selection = Set(item.selectDaysAgo.map { today.adding(days: -$0) })
        return viewModel
    }


    /// The real window with its toolbar, drawn off screen.
    private static func renderWindow(viewModel: HistoryViewModel, model: AppModel, size: NSSize, dark: Bool, scrolled: Bool = false,
                                     to url: URL) {
        let host = NSHostingController(rootView: HistoryView(model: viewModel).appEnvironment(model))
        host.sizingOptions = []
        let window = HistoryWindowController.makeWindow(content: host)
        let toolbar = HistoryToolbar(model: viewModel) {}
        window.toolbar = toolbar.toolbar
        window.title = model.l10n("history.title")
        window.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
        window.setContentSize(size)
        window.setFrameOrigin(NSPoint(x: -30_000, y: -30_000))
        window.orderFront(nil)
        RunLoop.main.run(until: Date().addingTimeInterval(0.6))
        defer { window.orderOut(nil) }
        if scrolled { scrollToEnd(window) }
        guard let frame = window.contentView?.superview else { return }
        frame.layoutSubtreeIfNeeded()
        frame.displayIfNeeded()
        guard let rep = frame.bitmapImageRepForCachingDisplay(in: frame.bounds) else { return }
        frame.cacheDisplay(in: frame.bounds, to: rep)
        if let image = rep.cgImage { PreviewSnapshot.write(image, to: url) }
        _ = toolbar
    }

    /// Scrolls to the end; a lazy table refines its estimated height as rows come in, so until the end stays put.
    private static func scrollToEnd(_ window: NSWindow) {
        guard let scroll = window.contentView.flatMap(firstScrollView) else { return }
        for _ in 0..<12 {
            guard let document = scroll.documentView else { return }
            let end = document.frame.height - scroll.contentView.bounds.height + scroll.contentInsets.bottom
            if abs(scroll.contentView.bounds.origin.y - end) < 0.5 { break }
            scroll.contentView.scroll(to: NSPoint(x: 0, y: end))
            scroll.reflectScrolledClipView(scroll.contentView)
            RunLoop.main.run(until: Date().addingTimeInterval(0.1))
        }
        RunLoop.main.run(until: Date().addingTimeInterval(0.3))
    }

    private static func firstScrollView(in view: NSView) -> NSScrollView? {
        if let scroll = view as? NSScrollView { return scroll }
        return view.subviews.lazy.compactMap(firstScrollView).first
    }

    /// The whole scrolling column at its natural height, to review the table.
    private static func renderColumn(viewModel: HistoryViewModel, model: AppModel, width: CGFloat, dark: Bool, to url: URL) {
        let rows = viewModel.content.rows.count
        let height = 460 + HistoryLayout.chartHeight + 520 + CGFloat(min(rows, 40)) * HistoryLayout.rowHeight + 120
        PreviewSnapshot.writeHosted(
            HistoryView(model: viewModel)
                .appEnvironment(model)
                .frame(width: width, height: height),
            dark: dark,
            to: url
        )
    }
}
