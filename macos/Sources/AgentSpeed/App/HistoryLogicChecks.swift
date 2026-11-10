import AgentSpeedCore
import Foundation

/// Checks of the history window's pure logic, run by `--render-previews` (the app has no test target). The results
/// land in `history-checks.txt` next to the PNGs; failures are also printed to standard error.
@MainActor
enum HistoryLogicChecks {
    static func run(l10n: Localizer, into directory: URL) {
        var results = ExecutionModeLogicChecks.run(l10n: l10n) + UnpricedLogicChecks.run(l10n: l10n)
        func check(_ name: String, _ condition: Bool) { results.append((name, condition)) }

        // Day arithmetic.
        let today = DayKey(year: 2026, month: 10, day: 21)!
        check("parse round trip", DayKey("2026-10-21") == today && today.description == "2026-10-21")
        check("reject malformed day", DayKey("2026-13-01") == nil && DayKey("2026-2-30") == nil && DayKey("20261021") == nil)
        check("ordinal round trip", DayKey(ordinal: today.ordinal) == today && DayKey(ordinal: 0).description == "1970-01-01")
        check("leap day", DayKey("2028-02-29") != nil && DayKey("2026-02-29") == nil)
        check("weekday", today.weekday == 4 && DayKey("1970-01-01")!.weekday == 5)
        check("month boundary", DayKey("2026-02-28")!.adding(days: 1) == DayKey("2026-03-01"))

        // Ranges.
        let first = DayKey("2026-03-14")!
        check("7 days", HistoryRanges.resolve(.days7, today: today, firstDataDay: first) == DayRange(DayKey("2026-10-15")!, today))
        check("30 days count", HistoryRanges.resolve(.days30, today: today, firstDataDay: first).count == 30)
        check("this year", HistoryRanges.resolve(.year, today: today, firstDataDay: first).start == DayKey("2026-01-01"))
        check("all starts at first data", HistoryRanges.resolve(.all, today: today, firstDataDay: first).start == first)
        check("all without data", HistoryRanges.resolve(.all, today: today, firstDataDay: nil) == .single(today))
        let custom = DayRange(DayKey("2026-09-01")!, DayKey("2026-09-10")!)
        check("custom", HistoryRanges.resolve(.custom, today: today, firstDataDay: first, custom: custom) == custom)
        check("previous period", HistoryRanges.previous(custom) == DayRange(DayKey("2026-08-22")!, DayKey("2026-08-31")!))
        let bounds = DayRange(first, today)
        let clamped = HistoryRanges.clamp(from: DayKey("2026-01-01")!, to: DayKey("2026-02-01")!, within: bounds)
        check("custom clamps to data, end never before start", clamped == .single(first))

        // Single days: 今天, 昨天 and one-day custom ranges compare with the day before over a week of sparkline.
        let yesterday = today.adding(days: -1)
        check("today is a single day", HistoryRanges.resolve(.today, today: today, firstDataDay: first) == .single(today))
        check("yesterday is the day before", HistoryRanges.resolve(.yesterday, today: today, firstDataDay: first) == .single(yesterday))
        check("one-day custom range is a single day",
              HistoryRanges.resolve(.custom, today: today, firstDataDay: first, custom: .single(first)).isSingleDay
              && !HistoryRanges.resolve(.days7, today: today, firstDataDay: first).isSingleDay)
        check("single day compares with the day before", HistoryRanges.previous(.single(today)) == .single(yesterday))
        check("single-day sparkline is the week ending on the day",
              HistoryRanges.sparkRange(.single(yesterday)) == DayRange(today.adding(days: -7), yesterday))
        check("longer ranges keep their own sparkline", HistoryRanges.sparkRange(custom) == custom)

        // Aggregation: speed is Σ measured output ÷ Σ measured time; peaks combine with max, all sources only.
        func usage(_ day: String, _ source: SourceKind, output: Int, ms: Int64, peak: Double?) -> DailyUsage {
            DailyUsage(
                day: day, source: source,
                totals: TokenTotals(requests: 1, input: output * 10, cachedInput: output * 9, cacheWriteInput: 0, output: output, reasoning: 0),
                measuredOutput: ms > 0 ? output : 0, measuredMs: ms, unfinalized: 0, agents: 1, peak: peak,
                models: [ModelUsage(model: "m-\(source.rawValue)", requests: 1, output: output)]
            )
        }
        let sample = [
            usage("2026-10-20", .codex, output: 1000, ms: 10_000, peak: 300),
            usage("2026-10-20", .claudeCode, output: 3000, ms: 10_000, peak: 300),
            usage("2026-10-21", .codex, output: 500, ms: 0, peak: nil),
            usage("bad-day", .codex, output: 1, ms: 1, peak: nil),
        ]
        let all = HistoryAggregator.daily(sample, source: nil)
        check("days grouped and bad days dropped", all.count == 2)
        check("speed of sums", all.first?.averageSpeed == 200)
        check("peak is max, not sum", all.first?.peak == 300)
        check("peak hidden for one source", HistoryAggregator.daily(sample, source: .codex).first?.peak == nil)
        check("no speed without measurement", all.last?.averageSpeed == nil)
        check("range totals speed", HistoryAggregator.totals(all).averageSpeed == 200)
        check("cache hit", all.first?.cacheHitRatio == 0.9)
        check("stack order largest first", HistoryChartMath.stackOrder(all) == [.claudeCode, .codex])
        check("change", HistoryAggregator.change(120, from: 100).map { abs($0 - 0.2) < 1e-9 } == true)
        check("no change without a previous period", HistoryAggregator.change(120, from: 0) == nil)

        // Single-day cards: every card against the day before, sparklines over seven days with the day highlighted.
        let week = (0..<7).map { usage(today.adding(days: -$0).description, .codex, output: 1000 + $0 * 100, ms: 10_000, peak: nil) }
        let todayContent = HistoryContent.build(
            usage: week, source: nil, rangeKind: .today, customRange: nil, sort: HistorySort(), today: today, l10n: l10n
        )
        let outputCard = todayContent.cards.first { $0.metric == .output }
        check("single day: output against the day before",
              outputCard?.change.map { abs($0 - (1000.0 - 1100) / 1100) < 1e-9 } == true
              && outputCard?.secondary == HistoryText.versusPreviousDay((1000.0 - 1100) / 1100, l10n))
        check("single day: seven-day sparkline ending on the day",
              todayContent.cards.allSatisfy { $0.spark.count == 7 && $0.sparkHighlight == 6 }
              && outputCard?.spark.last == 1000 && outputCard?.spark.first == 1600)
        check("ranges of several days have no highlighted point", HistoryContent.build(
            usage: week, source: nil, rangeKind: .days7, customRange: nil, sort: HistorySort(), today: today, l10n: l10n
        ).cards.allSatisfy { $0.sparkHighlight == nil })
        check("single day: breakdown instead of the chart, one table row",
              todayContent.breakdown?.day == today && todayContent.rows.map(\.day) == [today])
        let dayCSV = HistoryCSV.build(todayContent.rows, source: nil).components(separatedBy: "\r\n").filter { !$0.isEmpty }
        check("single day: CSV holds that day only", dayCSV.count == 2 && dayCSV[1].hasPrefix("2026-10-21,all,"))

        // Breakdown of one day.
        // Models: name, requests, output, measured output, measured time.
        func record(_ source: SourceKind, output: Int, requests: Int, measured: Int, ms: Int64,
                    models: [(String, Int, Int, Int, Int64)]) -> DailyUsage {
            DailyUsage(
                day: "2026-10-21", source: source,
                totals: TokenTotals(requests: requests, input: output * 20, output: output),
                measuredOutput: measured, measuredMs: ms, unfinalized: 0, agents: 1, peak: nil,
                models: models.map { ModelUsage(model: $0.0, requests: $0.1, output: $0.2, measuredOutput: $0.3, measuredMs: $0.4) }
            )
        }
        let dayRecords = [
            record(.codex, output: 3000, requests: 6, measured: 3000, ms: 30_000,
                   models: [("gpt-a", 4, 2000, 2000, 10_000), ("gpt-b", 2, 1000, 1000, 20_000)]),
            record(.claudeCode, output: 5000, requests: 5, measured: 4000, ms: 20_000, models: [("opus", 5, 5000, 4000, 20_000)]),
            record(.omp, output: 2000, requests: 2, measured: 1000, ms: 4000, models: [("opus", 1, 1500, 0, 0)]),
            record(.codex, output: 999, requests: 1, measured: 999, ms: 1000, models: []).with(day: "2026-10-20"),
        ]
        let bySource = HistoryBreakdown.build(dayRecords, day: today, source: nil)
        check("breakdown by source, most output first", bySource.kind == .bySource
              && bySource.items.map(\.subject) == [.source(.claudeCode), .source(.codex), .source(.omp)])
        check("breakdown shares add up to 1", abs(bySource.items.reduce(0) { $0 + $1.share } - 1) < 1e-9
              && abs(bySource.items[0].share - 0.5) < 1e-9 && bySource.items[0].scale == 1)
        check("breakdown speed is measured output ÷ measured time", bySource.items[0].averageSpeed == 200
              && bySource.items[1].averageSpeed == 100 && bySource.items[2].averageSpeed == 250)
        let byModel = HistoryBreakdown.build(dayRecords, day: today, source: .codex)
        check("breakdown by model, most output first", byModel.kind == .byModel
              && byModel.items.map(\.subject) == [.model("gpt-a"), .model("gpt-b")]
              && abs(byModel.items.reduce(0) { $0 + $1.share } - 1) < 1e-9)
        check("model speed is the model's measured output ÷ its measured time",
              byModel.items.map(\.averageSpeed) == [200, 50]
              && HistoryBreakdown.build(dayRecords, day: today, source: .claudeCode).items.first?.averageSpeed == 200)
        let merged = HistoryAggregator.daily(dayRecords, source: nil, range: DayRange(today, today)).first?.models.first
        check("a model's time adds up across sources", merged == ModelUsage(
            model: "opus", requests: 6, output: 6500, measuredOutput: 4000, measuredMs: 20_000)
              && HistoryText.modelWithSpeed(merged!, l10n).hasSuffix("tok/s") == true)
        let withRest = HistoryBreakdown.build(dayRecords, day: today, source: .omp)
        check("output without a model name is gathered last", withRest.items.map(\.subject) == [.model("opus"), .otherModels]
              && withRest.items.last?.output == 500 && abs(withRest.items.reduce(0) { $0 + $1.share } - 1) < 1e-9
              && withRest.items.first?.averageSpeed == nil && withRest.items.last?.averageSpeed == 250)
        check("breakdown of a day without usage is empty", HistoryBreakdown.build(dayRecords, day: today.adding(days: -5), source: nil).isEmpty
              && HistoryBreakdown.build(dayRecords, day: today, source: .gemini).isEmpty)

        // Chart math.
        check("nice ticks", HistoryChartMath.ticks(1_380_000) == AxisTicks(step: 500_000, intervals: 3))
        check("aligned ticks", HistoryChartMath.alignedTicks(130, intervals: 3) == AxisTicks(step: 50, intervals: 3))
        check("runs break on gaps", HistoryChartMath.runs([1, 2, nil, 4, nil, nil, 7]) == [0..<2, 3..<4, 6..<7])
        let marks = HistoryChartMath.marks(DayRange(DayKey("2026-09-22")!, today), slot: 6, weekStart: 1)
        check("month rule on the 1st", marks.rules == [9])
        check("weekly labels on week starts", marks.days.allSatisfy { DayRange(DayKey("2026-09-22")!, today).day(at: $0).weekday == 1 })

        // Text.
        check("axis numbers", HistoryText.axisNumber(1_500_000) == "1.5M" && HistoryText.axisNumber(500_000) == "500k"
            && HistoryText.axisNumber(1_000_000) == "1M" && HistoryText.axisNumber(150) == "150"
            && HistoryText.axisNumber(2_500_000_000) == "2.5B")
        check("billions", SpeedFormat.tokens(2_491_000_000) == "2.49B" && SpeedFormat.tokens(999_700_000) == "1.00B")
        check("signed change", HistoryText.change(0.123) == "+12%" && HistoryText.change(-0.004) == "\u{2212}0.4%"
            && HistoryText.change(0.0001) == "0%")
        check("percent", HistoryText.percent(0.004) == "<1%" && HistoryText.percent(0.904) == "90%")

        // Table.
        let sorted = HistoryTable.sort(all, by: HistorySort(column: .speed, descending: true))
        check("rows without a value sort last", sorted.last?.averageSpeed == nil)
        check("default order is newest first", HistoryTable.sort(all, by: HistorySort()).first?.day == today)
        check("sort toggles", HistorySort().toggled(.date) == HistorySort(column: .date, descending: false)
            && HistorySort().toggled(.models) == HistorySort(column: .models, descending: false))
        let copied = HistoryTable.copyText(all, columns: HistoryColumn.visible(allSources: true), today: today, money: .usd, l10n)
        check("copy has a header and one line per row", copied.split(separator: "\n").count == 3)

        // CSV.
        let csv = HistoryCSV.build(all, source: nil)
        let lines = csv.components(separatedBy: "\r\n")
        check("csv header", lines.first == HistoryCSV.columns.joined(separator: ","))
        check("csv row", lines[1].hasPrefix("2026-10-20,all,2,40000,36000,0,4000,0,90.0,4000,20000,200.0,300.0,2,0,"))
        check("csv bom", HistoryCSV.encode(csv).prefix(3) == Data([0xEF, 0xBB, 0xBF]))
        check("csv file name", HistoryCSV.fileName(custom, source: .claudeCode) == "agent-speed-history-claude-code-2026-09-01-2026-09-10.csv")
        let sourceIDs = SourceKind.allCases.map { HistoryCSV.sourceID($0) }
        check("csv source ids are distinct", Set(sourceIDs).count == sourceIDs.count && !sourceIDs.contains("all"))
        check("csv source ids of omp and Antigravity", HistoryCSV.sourceID(.omp) == "omp" && HistoryCSV.sourceID(.antigravity) == "antigravity")
        check("csv source ids of both WorkBuddy editions",
              HistoryCSV.sourceID(.workbuddy) == "workbuddy" && HistoryCSV.sourceID(.workbuddyAi) == "workbuddy-ai")
        check("csv source id of Pi", HistoryCSV.sourceID(.pi) == "pi")
        check("settings saved before Pi load with Pi on and its default folder", piSettingsUpgrade())
        check("csv source id of OpenClaw", HistoryCSV.sourceID(.openclaw) == "openclaw")
        check("settings saved before OpenClaw load with OpenClaw on and its state folders", openclawSettingsUpgrade())
        check("an OpenClaw database chosen as the folder stays a file", openclawDatabaseSetting())
        check("csv source id of Hermes Agent", HistoryCSV.sourceID(.hermes) == "hermes")
        check("settings saved before Hermes Agent load with it on and its home", hermesSettingsUpgrade())
        check("a Hermes logs folder added in Sources is read next to the home", hermesFolderSetting())
        check("csv source id of Factory Droid", HistoryCSV.sourceID(.droid) == "droid")
        check("csv source id of Devin", HistoryCSV.sourceID(.devin) == "devin")
        check("csv source ids of Qoder IDE and Qoder CLI",
              HistoryCSV.sourceID(.qoder) == "qoder" && HistoryCSV.sourceID(.qoderCli) == "qoder-cli")
        check("settings saved before Factory Droid load with it on and its sessions folder", droidSettingsUpgrade())
        check("a Factory Droid folder added in Sources is read next to ~/.factory/sessions", droidFolderSetting())
        let risky = DaySummary(
            day: today, requests: 0, input: 0, cachedInput: 0, cacheWriteInput: 0, output: 0, reasoning: 0,
            measuredOutput: 0, measuredMs: 0, unfinalized: 0, agents: 0, peak: nil,
            models: [ModelUsage(model: "=cmd,\"x\"", requests: 1, output: 1)], sources: []
        )
        check("csv neutralizes formulas and quotes", HistoryCSV.build([risky], source: nil).contains("\"'=cmd,\"\"x\"\"\""))

        // Cost (DESIGN 9, METRICS 9.4, 9.7, 9.8).
        func priced(_ day: String, _ source: SourceKind, requests: Int, cost: CostTally?, model: String = "m") -> DailyUsage {
            DailyUsage(
                day: day, source: source, totals: TokenTotals(requests: requests, input: 100, output: 10),
                measuredOutput: 0, measuredMs: 0, unfinalized: 0, agents: 1, peak: nil,
                models: [ModelUsage(model: model, requests: requests, output: 10, cost: cost)], cost: cost,
                priceVersion: cost == nil ? nil : 3
            )
        }
        let costRecords = [
            priced("2026-10-20", .codex, requests: 4, cost: CostTally(costMicroUsd: 4_000_000)),
            priced("2026-10-20", .claudeCode, requests: 2, cost: CostTally(costMicroUsd: 1_500_000, unpricedRequests: 1), model: "mystery"),
            priced("2026-10-21", .codex, requests: 3, cost: CostTally(costMicroUsd: 2_250_000)),
            priced("2026-10-21", .claudeCode, requests: 5, cost: nil),
            priced("2026-10-19", .codex, requests: 2, cost: CostTally(unpricedRequests: 2)),
            priced("2026-10-18", .claudeCode, requests: 1, cost: nil),
        ]
        let costDays = HistoryAggregator.daily(costRecords, source: nil)
        func day(_ text: String, _ days: [DaySummary]) -> DaySummary? { days.first { $0.day == DayKey(text) } }
        check("cost: a day mixing stored rows without cost counts their requests as unpriced",
              day("2026-10-21", costDays)?.cost.kind == .priced(microUsd: 2_250_000, partial: true)
              && day("2026-10-21", costDays)?.cost.unpriced == 5)
        check("cost: a day without cost data is unavailable, one with nothing priced is unknown",
              day("2026-10-18", costDays)?.cost.kind == .unavailable && day("2026-10-19", costDays)?.cost.kind == .unknown)
        let rangeTotals = HistoryAggregator.totals(costDays)
        check("cost: a range total is the sum of its days",
              rangeTotals.cost.microUsd == costDays.reduce(0) { $0 + $1.cost.microUsd } && rangeTotals.cost.microUsd == 7_750_000
              && rangeTotals.costDays == 3)
        let excludedDays = HistoryAggregator.daily(costRecords, source: nil, excluded: [.claudeCode])
        check("cost: subscription sources keep their tokens but add no cost",
              day("2026-10-20", excludedDays)?.cost.kind == .priced(microUsd: 4_000_000, partial: false)
              && day("2026-10-20", excludedDays)?.output == 20
              && day("2026-10-18", excludedDays)?.cost.kind == .subscription([.claudeCode])
              && day("2026-10-20", excludedDays)?.sources.first { $0.source == .claudeCode }?.cost.microUsd == 0)
        let options = HistoryCostOptions(marked: [.claudeCode], includeSubscriptions: false)
        check("cost: a subscription source selected directly shows its API-equivalent cost",
              options.excluded(for: .claudeCode).isEmpty && options.excluded(for: nil) == [.claudeCode]
              && HistoryCostOptions(marked: [.claudeCode], includeSubscriptions: true).excluded(for: nil).isEmpty)
        let twd = HistoryMoney(currency: CurrencyTable.bundled.currencyOrUSD("TWD"), usd: CurrencyTable.bundled.currencyOrUSD("USD"),
                               language: "zh-Hant", rate: Decimal(string: "32.4")!, currencyName: "新臺幣")
        let costContent = HistoryContent.build(
            usage: costRecords, source: nil, rangeKind: .days7, customRange: nil, sort: HistorySort(column: .cost, descending: true),
            today: today, l10n: l10n, money: twd, costOptions: HistoryCostOptions(marked: [.claudeCode], metric: .cost)
        )
        let costCard = costContent.cards.first { $0.metric == .cost }
        check("cost: fifth card, amount in the display currency", costContent.cards.count == 5
              && costCard?.value == "≥NT$203" && costCard?.titleKey == "history.summary.cost"
              && costCard?.help?.contains(l10n("history.costFrozen")) == true && costCard?.neutralChange == true)
        check("cost: the cost card of a subscription source reads API-equivalent", HistoryContent.build(
            usage: costRecords, source: .claudeCode, rangeKind: .days7, customRange: nil, sort: HistorySort(), today: today,
            l10n: l10n, money: twd, costOptions: HistoryCostOptions(marked: [.claudeCode])
        ).cards.last?.titleKey == "history.summary.costApi")
        check("cost: chart bars in the display currency without the speed line",
              costContent.chart.metric == .cost && costContent.chart.speeds.allSatisfy { $0 == nil }
              && abs((costContent.chart.segments.first { $0.day == DayKey("2026-10-20") }?.value ?? 0) - 129.6) < 1e-9
              && costContent.chart.stack == [.codex])
        check("cost: column after input, sorted by known cost with the rest last",
              HistoryColumn.visible(allSources: true).firstIndex(of: .cost) == HistoryColumn.visible(allSources: true).firstIndex(of: .input)! + 1
              && costContent.rows.first?.day == DayKey("2026-10-20")
              && costContent.rows.suffix(2).allSatisfy { $0.cost.sortValue == nil })
        check("cost: cells read ≥, – and the subscription tag",
              HistoryTable.cell(day("2026-10-21", costDays)!, .cost, today: today, money: .usd, l10n) == "≥$2.25"
              && HistoryTable.cell(day("2026-10-19", costDays)!, .cost, today: today, money: .usd, l10n) == "–"
              && HistoryTable.cell(day("2026-10-18", excludedDays)!, .cost, today: today, money: .usd, l10n) == l10n("cost.subscription.tag"))
        check("cost: the notice explains the stored unpriced requests (derived from the per-model counts)",
              costContent.unpriced.groups.map(\.source) == [.codex] && costContent.unpricedRequests == 2
              && costContent.unpriced.groups.first?.derived == 2 && costContent.unpriced.groups.first?.rows.first?.key.model == "m")
        check("cost: model cost text", HistoryText.modelCost(ModelCost(cost: CostTally(), requests: 3), money: .usd, l10n) == l10n("cost.free")
              && HistoryText.modelCost(ModelCost(cost: nil, requests: 3), money: .usd, l10n) == l10n("cost.unknown")
              && HistoryText.modelCost(ModelCost(cost: nil, requests: 0), money: .usd, l10n) == l10n("cost.subscription.tag")
              && HistoryText.modelCost(ModelCost(cost: CostTally(costMicroUsd: 1_234_000, unpricedRequests: 1), requests: 3), money: .usd, l10n) == "≥$1.23")
        let costCSV = HistoryCSV.build(costDays, source: nil, money: twd).components(separatedBy: "\r\n")
        check("cost: CSV columns", costCSV.first == (HistoryCSV.columns + ["cost_twd"]).joined(separator: ",")
              && HistoryCSV.columns.suffix(3) == ["cost_usd", "unpriced_requests", "price_version"])
        check("cost: CSV values are exact and ignore subscription marks",
              costCSV.contains { $0.hasPrefix("2026-10-20,all,") && $0.hasSuffix(",5.500000,1,3,178.20") }
              && costCSV.contains { $0.hasPrefix("2026-10-18,all,") && $0.hasSuffix(",,,,") })
        check("cost: amounts with US dollars beside another currency", twd.amountWithUSD(1_000_000) == "NT$32（US$1.00）"
              && HistoryMoney.usd.amountWithUSD(1_000_000) == "$1.00")
        check("cost: axis labels", HistoryMoney.usd.axisLabel(2.5, step: 2.5) == "$2.5" && HistoryMoney.usd.axisLabel(0.25, step: 0.25) == "$0.25"
              && HistoryMoney.usd.axisLabel(1500, step: 500) == "$1.5k" && HistoryMoney.usd.axisLabel(0, step: 1) == "$0")
        let costBreakdown = HistoryBreakdown.build(costRecords, day: DayKey("2026-10-20")!, source: nil, metric: .cost, excluded: [.claudeCode])
        check("cost: a breakdown by cost sorts and sizes by cost", costBreakdown.items.map(\.subject) == [.source(.codex), .source(.claudeCode)]
              && costBreakdown.items.first?.scale == 1 && costBreakdown.items.last?.cost.kind == .subscription([.claudeCode]))

        // Traffic (METRICS 10.9, DESIGN 10): not recorded before measuring began (never 0), partial days, tools per the
        // setting, mirrored chart, table cell, CSV columns and machine rows.
        let trafficRows = [
            TrafficDay(day: "2026-10-20", key: TrafficDay.machineKey, rx: 9_000_000, tx: 1_000_000, sampledMs: 86_400_000),
            TrafficDay(day: "2026-10-20", key: "claudeCode", rx: 1_000_000, tx: 100_000, rxTools: 4_000_000, txTools: 50_000,
                       sampledMs: 86_400_000),
            TrafficDay(day: "2026-10-21", key: TrafficDay.machineKey, rx: 2_000_000, tx: 200_000, sampledMs: 3_600_000),
            TrafficDay(day: "2026-10-21", key: "codex", rx: 300_000, tx: 30_000, sampledMs: 3_600_000),
        ]
        let traffic = HistoryTraffic(days: trafficRows, since: "2026-10-20", includeTools: true, bits: false)
        let noon = HistoryDates.calendar.date(byAdding: .hour, value: 12, to: today.date(in: HistoryDates.calendar))!
        let tuesday = traffic.day(DayKey("2026-10-20")!, source: nil, today: today, now: noon, calendar: HistoryDates.calendar)
        let wednesday = traffic.day(today, source: nil, today: today, now: noon, calendar: HistoryDates.calendar)
        check("traffic: not recorded before measuring began",
              traffic.day(DayKey("2026-10-19")!, source: nil, today: today, now: noon, calendar: HistoryDates.calendar) == nil)
        check("traffic: tools included and this computer's total", tuesday?.shown == TrafficBytes(rx: 5_000_000, tx: 150_000)
              && tuesday?.machine == TrafficBytes(rx: 9_000_000, tx: 1_000_000) && tuesday?.partial == false)
        check("traffic: a day recorded for 1 h of 12 is partial", wednesday?.partial == true && wednesday?.recordedMs == 3_600_000)
        let noTools = HistoryTraffic(days: trafficRows, since: "2026-10-20", includeTools: false, bits: false)
        check("traffic: without tools only the agents' own bytes",
              noTools.day(DayKey("2026-10-20")!, source: .claudeCode, today: today, now: noon, calendar: HistoryDates.calendar)?.shown
              == TrafficBytes(rx: 1_000_000, tx: 100_000))
        let trafficContent = HistoryContent.build(
            usage: PreviewHistory.days(endingAt: today.date(in: HistoryDates.calendar), count: 10), source: nil, rangeKind: .days7,
            customRange: nil, sort: HistorySort(), today: today, l10n: l10n,
            costOptions: HistoryCostOptions(metric: .traffic), traffic: traffic, now: noon
        )
        let chart = trafficContent.chart
        check("traffic: mirrored bars, download above and upload below",
              chart.segments.contains { !$0.upload && $0.upper > 0 } && chart.segments.contains { $0.upload && $0.lower < 0 }
              && chart.yDomain.lowerBound < 0 && chart.speeds.allSatisfy { $0 == nil })
        check("traffic: days before measuring are marked", chart.notRecordedThrough == DayKey("2026-10-19"))
        check("traffic: the table column and its not-recorded cell", trafficContent.columns.contains(.traffic)
              && trafficContent.rows.contains { $0.day == DayKey("2026-10-17") && HistoryTable.cell($0, .traffic, today: today, money: .usd, l10n) == "–" })
        let trafficCSV = HistoryCSV.build(trafficContent.rows, source: nil, traffic: true).components(separatedBy: "\r\n")
        check("traffic: CSV columns, own and tool bytes, a machine row per date",
              trafficCSV.first?.hasSuffix(HistoryCSV.trafficColumns.joined(separator: ",")) == true
              && trafficCSV.contains { $0.hasPrefix("2026-10-20,all,") && $0.hasSuffix(",1000000,100000,4000000,50000") }
              && trafficCSV.contains { $0.hasPrefix("2026-10-20,machine,") && $0.hasSuffix(",9000000,1000000,,") }
              && trafficCSV.contains { $0.hasPrefix("2026-10-17,all,") && $0.hasSuffix(",,,,") })
        check("traffic: byte axis labels", HistoryText.axisBytes(2_500_000_000) == "2.5\u{00A0}GB"
              && HistoryText.axisBytes(-500_000_000) == "500\u{00A0}MB" && HistoryText.axisBytes(1_000_000_000) == "1\u{00A0}GB")

        // Traffic without token usage (DESIGN 10): its days are history, the cards count tokens only.
        let codexUsage = [usage("2026-10-20", .codex, output: 1000, ms: 10_000, peak: nil)]
        let trafficOnly = HistoryContent.build(
            usage: codexUsage, source: .claudeCode, rangeKind: .days7, customRange: nil, sort: HistorySort(), today: today,
            l10n: l10n, traffic: traffic, now: noon
        )
        check("traffic only: a source with traffic but no tokens is not empty", !trafficOnly.isEmpty
              && trafficOnly.rows.map(\.day) == [DayKey("2026-10-20")!] && trafficOnly.rows.first?.requests == 0
              && trafficOnly.rows.first?.traffic?.shown == TrafficBytes(rx: 5_000_000, tx: 150_000))
        check("traffic only: the chart opens on traffic", trafficOnly.showsOnlyTraffic && !trafficOnly.hasTokenUsage)
        check("traffic only: the picker offers sources with traffic", trafficOnly.pickerSources == [.codex, .claudeCode])
        check("traffic only: no active days, no busiest day",
              trafficOnly.cards.first { $0.metric == .requests }?.secondary == "" && trafficOnly.cards.first { $0.metric == .output }?.secondary == "")
        let allTraffic = HistoryContent.build(
            usage: codexUsage, source: nil, rangeKind: .days7, customRange: nil, sort: HistorySort(), today: today,
            l10n: l10n, traffic: traffic, now: noon
        )
        check("traffic only: all sources list token and traffic days, tokens decide active days",
              allTraffic.rows.map(\.day) == [today, DayKey("2026-10-20")!] && allTraffic.hasTokenUsage && !allTraffic.showsOnlyTraffic
              && HistoryAggregator.totals(allTraffic.days).activeDays == 1)
        let machineOnly = HistoryTraffic(days: [trafficRows[0]], since: "2026-10-20", includeTools: true, bits: false)
        check("traffic only: this computer's own traffic is not a day with data", HistoryContent.build(
            usage: [], source: nil, rangeKind: .days7, customRange: nil, sort: HistorySort(), today: today, l10n: l10n,
            traffic: machineOnly, now: noon
        ).isEmpty)
        let allRange = HistoryContent.build(
            usage: [], source: .claudeCode, rangeKind: .all, customRange: nil, sort: HistorySort(), today: today, l10n: l10n,
            traffic: traffic, now: noon
        )
        check("traffic only: 全部 reaches back to the first traffic day", allRange.range.start == DayKey("2026-10-20"))
        let oneDay = HistoryBreakdown.build([], day: DayKey("2026-10-20")!, source: .claudeCode, metric: .traffic,
                                            traffic: traffic.day(DayKey("2026-10-20")!, source: .claudeCode, today: today, now: noon,
                                                                 calendar: HistoryDates.calendar))
        check("traffic only: one source's day breaks down into its one bar", oneDay.kind == .bySource
              && oneDay.items.map(\.subject) == [.source(.claudeCode)] && oneDay.items.first?.share == 1)

        // Durations and spoken traffic (DESIGN 10, Accessibility): shared strings, never "today" for another day.
        let zh = Localizer(preference: .zhHant)
        let en = Localizer(preference: .en)
        check("traffic: recorded time in whole minutes", HistoryText.duration(ms: 5_000, zh) == "1 分鐘"
              && HistoryText.duration(ms: 45 * 60_000 + 59_000, en) == "45 min" && HistoryText.duration(ms: 3 * 3_600_000, en) == "3 hr"
              && HistoryText.duration(ms: 3 * 3_600_000 + 20 * 60_000, zh) == "3 小時 20 分鐘")
        let spokenRow = HistoryText.accessible(trafficOnly.rows[0], today: today, traffic: true, en)
        check("traffic: a row reads its traffic without today", spokenRow.contains("5.00\u{00A0}MB downloaded and 150\u{00A0}KB uploaded")
              && !spokenRow.contains("today"))
        check("traffic: a chart day reads its traffic and notes",
              HistoryText.accessibleTraffic(DayKey("2026-10-20")!, traffic: tuesday, today: today, en).contains("This computer")
              && HistoryText.accessibleTraffic(DayKey("2026-10-19")!, traffic: nil, today: today, en).contains("wasn't recorded"))
        check("traffic: the cell tooltip only adds notes", HistoryTable.trafficTooltip(
            DayTraffic(own: TrafficBytes(rx: 1, tx: 1), tools: .zero, bySource: [:], machine: nil, recordedMs: 0, partial: false,
                       includeTools: true), en) == nil)

        // Column layout never goes below the minimums and fills the width.
        let specs = HistoryColumn.allCases.map(HistoryColumnLayout.spec)
        let wide = HistoryColumnLayout.widths(specs, width: 1200)
        check("columns fill wide tables", abs(wide.reduce(0, +) + HistoryColumnLayout.gap * Double(specs.count - 1) - 1200) < 0.5)
        let narrow = HistoryColumnLayout.widths(specs, width: 600)
        check("columns keep their minimums", zip(narrow, specs).allSatisfy { $0 >= $1.minimum - 0.001 })
        // The minimum window (720 pt) leaves about 637 pt for the table: the gaps tighten before anything clips.
        let tight = HistoryColumnLayout.layout(specs, width: 637)
        check("the table fits the minimum window", abs(tight.widths.reduce(0, +) + tight.gap * Double(specs.count - 1) - 637) < 0.5
              && tight.gap >= HistoryColumnLayout.minimumGap && tight.gap < HistoryColumnLayout.gap)
        let roomy = HistoryColumnLayout.layout(specs, width: 900)
        check("the full gap while the columns fit", roomy.gap == HistoryColumnLayout.gap)

        let report = results.map { "\($0.1 ? "PASS" : "FAIL")  \($0.0)" }.joined(separator: "\n")
        try? (report + "\n").write(to: directory.appendingPathComponent("history-checks.txt"), atomically: true, encoding: .utf8)
        for (name, passed) in results where !passed {
            FileHandle.standardError.write(Data("history check failed: \(name)\n".utf8))
        }
    }

    /// Settings saved by a version before Pi: Pi starts enabled, at its default folders, and an omp choice stays omp's.
    private static func piSettingsUpgrade() -> Bool {
        let suite = "AgentSpeedPiUpgradeCheck"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        guard let defaults = UserDefaults(suiteName: suite) else { return false }
        let before = SourceKind.allCases.filter { $0 != .pi }.map(\.rawValue)
        defaults.set(before.filter { $0 != "gemini" }, forKey: "enabledSources")
        defaults.set(before, forKey: "knownSources")
        defaults.set("/Users/dev/.omp/agent/sessions", forKey: "ompRoot")
        let settings = AppSettings(defaults: defaults)
        return settings.isEnabled(.pi) && !settings.isEnabled(.gemini) && settings.sourceLocations[.pi] == [.automatic]
            && settings.monitorConfiguration.locations[.pi] == [.automatic]
            && settings.sourceLocations[.omp] == [.path("/Users/dev/.omp/agent/sessions")]
            && TranscriptFolder.pi.entries(in: settings).map(\.urls) == [LiveMonitor.defaultPiRoots]
    }

    /// Settings saved by a version before OpenClaw: OpenClaw starts enabled and reads every state folder.
    private static func openclawSettingsUpgrade() -> Bool {
        let suite = "AgentSpeedOpenClawUpgradeCheck"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        guard let defaults = UserDefaults(suiteName: suite) else { return false }
        let before = SourceKind.allCases.filter { $0 != .openclaw && $0 != .hermes }.map(\.rawValue)
        defaults.set(before.filter { $0 != "omp" }, forKey: "enabledSources")
        defaults.set(before, forKey: "knownSources")
        let settings = AppSettings(defaults: defaults)
        return settings.isEnabled(.openclaw) && !settings.isEnabled(.omp) && settings.sourceLocations[.openclaw] == [.automatic]
            && TranscriptFolder.openclaw.entries(in: settings).map(\.urls) == [LiveMonitor.defaultOpenclawRoots]
    }

    /// Settings saved by a version before Hermes Agent: it starts enabled and reads the automatic home.
    private static func hermesSettingsUpgrade() -> Bool {
        let suite = "AgentSpeedHermesUpgradeCheck"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        guard let defaults = UserDefaults(suiteName: suite) else { return false }
        let before = SourceKind.allCases.filter { $0 != .hermes }.map(\.rawValue)
        defaults.set(before.filter { $0 != "pi" }, forKey: "enabledSources")
        defaults.set(before, forKey: "knownSources")
        let settings = AppSettings(defaults: defaults)
        return settings.isEnabled(.hermes) && !settings.isEnabled(.pi) && settings.sourceLocations[.hermes] == [.automatic]
            && TranscriptFolder.hermes.entries(in: settings).map(\.urls) == [[LiveMonitor.defaultHermesHome]]
            && SourceKind.hermes.transcriptFolder == .hermes
    }

    /// A Hermes `logs` folder added in Sources is stored next to the home and passed to the monitor; Reset returns to
    /// the home alone.
    private static func hermesFolderSetting() -> Bool {
        let suite = "AgentSpeedHermesFolderCheck"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        guard let defaults = UserDefaults(suiteName: suite) else { return false }
        let folder = URL(fileURLWithPath: "/Users/dev/.hermes/profiles/work/logs", isDirectory: true)
        let added = TranscriptFolder.hermes.add(folder, in: AppSettings(defaults: defaults)) == .added
        let reloaded = AppSettings(defaults: defaults)
        let chosen = reloaded.sourceLocations[.hermes] == [.automatic, .path(folder.path)]
            && reloaded.monitorConfiguration.locations[.hermes] == [.automatic, .path(folder.path)]
            && TranscriptFolder.hermes.entries(in: reloaded).last?.shownURLs.map(\.path) == [folder.path]
        TranscriptFolder.hermes.reset(in: reloaded)
        return added && chosen && AppSettings(defaults: defaults).sourceLocations[.hermes] == [.automatic]
            && !TranscriptFolder.hermes.acceptsDatabaseFile
    }

    /// Settings saved by a version before Factory Droid: it starts enabled and reads `~/.factory/sessions`.
    private static func droidSettingsUpgrade() -> Bool {
        let suite = "AgentSpeedDroidUpgradeCheck"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        guard let defaults = UserDefaults(suiteName: suite) else { return false }
        let before = SourceKind.allCases.filter { $0 != .droid }.map(\.rawValue)
        defaults.set(before.filter { $0 != "hermes" }, forKey: "enabledSources")
        defaults.set(before, forKey: "knownSources")
        let settings = AppSettings(defaults: defaults)
        return settings.isEnabled(.droid) && !settings.isEnabled(.hermes) && settings.sourceLocations[.droid] == [.automatic]
            && TranscriptFolder.droid.entries(in: settings).map(\.urls) == [[LiveMonitor.defaultDroidRoot]]
            && SourceKind.droid.transcriptFolder == .droid
    }

    /// A moved `.factory` folder added in Sources is stored and passed to the monitor; with the automatic entry removed,
    /// adding the automatic sessions folder brings it back; Reset returns to it alone.
    private static func droidFolderSetting() -> Bool {
        let suite = "AgentSpeedDroidFolderCheck"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        guard let defaults = UserDefaults(suiteName: suite) else { return false }
        let folder = URL(fileURLWithPath: "/Volumes/work/dev/.factory", isDirectory: true)
        let added = TranscriptFolder.droid.add(folder, in: AppSettings(defaults: defaults)) == .added
        let reloaded = AppSettings(defaults: defaults)
        let chosen = reloaded.monitorConfiguration.locations[.droid] == [.automatic, .path(folder.path)]
        let covered = TranscriptFolder.droid.add(LiveMonitor.defaultDroidRoot, in: reloaded) == .alreadyAutomatic
        TranscriptFolder.droid.remove(.automatic, in: reloaded)
        let alone = AppSettings(defaults: defaults).sourceLocations[.droid] == [.path(folder.path)]
        let restored = TranscriptFolder.droid.add(LiveMonitor.defaultDroidRoot, in: reloaded) == .restoredAutomatic
            && AppSettings(defaults: defaults).sourceLocations[.droid] == [.automatic, .path(folder.path)]
        TranscriptFolder.droid.reset(in: reloaded)
        return added && chosen && covered && alone && restored
            && AppSettings(defaults: defaults).sourceLocations[.droid] == [.automatic] && !TranscriptFolder.droid.acceptsDatabaseFile
    }

    /// `openclaw-agent.sqlite` stored by an earlier version as the folder becomes that file's location.
    private static func openclawDatabaseSetting() -> Bool {
        let suite = "AgentSpeedOpenClawDatabaseCheck"
        UserDefaults.standard.removePersistentDomain(forName: suite)
        defer { UserDefaults.standard.removePersistentDomain(forName: suite) }
        guard let defaults = UserDefaults(suiteName: suite) else { return false }
        let database = "/Users/dev/.openclaw/agents/main/agent/openclaw-agent.sqlite"
        defaults.set(database, forKey: "openclawRoot")
        let reloaded = AppSettings(defaults: defaults)
        return reloaded.sourceLocations[.openclaw] == [.path(database)]
            && reloaded.monitorConfiguration.locations[.openclaw] == [.path(database)]
            && TranscriptFolder.openclaw.acceptsDatabaseFile && !TranscriptFolder.pi.acceptsDatabaseFile
    }
}

private extension DailyUsage {
    func with(day: String) -> DailyUsage {
        var copy = self
        copy.day = day
        return copy
    }
}
