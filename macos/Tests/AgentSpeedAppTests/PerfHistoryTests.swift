import AgentSpeedCore
import CoreGraphics
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 12, *Usage History: performance (性能表現)*: the rows of a range, the single day's 14-day trend, the default
/// sort and selection, the widths, the column layout, the trend's log scale and the words.
@Suite("Usage History performance")
@MainActor
struct PerfHistoryTests {
    static let today = DayKey(year: 2026, month: 10, day: 29)!
    static let range = DayRange(today.adding(days: -29), today)
    static let records = PerfPreviewData.historyRecords(today: today)
    static let zh = Localizer(preference: .zhHant)
    static let en = Localizer(preference: .en)

    static func content(_ range: DayRange = range, source: SourceKind? = nil) -> PerfHistoryContent {
        PerfHistoryContent.build(records, range: range, source: source, today: today)
    }

    static func row(_ model: String, _ source: SourceKind, in content: PerfHistoryContent = content()) throws -> PerfHistoryRow {
        try #require(content.rows.first { $0.model == model && $0.source == source })
    }

    // MARK: Content

    @Test func everySourceAndModelWithATimedRequestHasARow() throws {
        let content = Self.content()
        #expect(content.rows.count == 6)
        #expect(content.trendDays == Self.range && content.singleDay == nil)
        let opus = try Self.row("claude-opus-5-5", .claudeCode)
        #expect(opus.trend.count == 30)
        #expect(opus.trend.filter { $0.score == nil }.count == 3, "days below 20 timed requests are gaps")
        #expect(opus.shown != nil && opus.usual != nil)
        let gemini = try Self.row("gemini-3-pro", .gemini)
        #expect(gemini.n == 12 && gemini.shown == nil, "below 20 timed requests in the range: no score")
        // The window's source picker narrows to one source's models.
        #expect(Self.content(source: .codex).rows.map(\.model).sorted() == ["gpt-6.1", "gpt-6.1-mini"])
        #expect(Self.content(DayRange(Self.today.adding(days: -400), Self.today.adding(days: -300))).rows.isEmpty)
    }

    @Test func aSingleDayShowsTheFourteenDaysEndingOnIt() throws {
        let yesterday = Self.today.adding(days: -1)
        let content = Self.content(.single(yesterday))
        #expect(content.singleDay == yesterday)
        #expect(content.trendDays == DayRange(yesterday.adding(days: -13), yesterday))
        let opus = try Self.row("claude-opus-5-5", .claudeCode, in: content)
        #expect(opus.trend.count == 14 && opus.trend.last?.day == yesterday.description)
        #expect(opus.n == opus.trend.last?.n, "the pooled values are the day's own")
    }

    @Test func theUsualValueIsTheFourteenDaysBeforeToday() throws {
        let opus = try Self.row("claude-opus-5-5", .claudeCode)
        let expected = PerfHistory.baseline(Self.records, source: .claudeCode, model: "claude-opus-5-5",
                                            today: Self.today.description)
        #expect(opus.usual == expected && expected != nil)
        #expect(try Self.row("gpt-6.1-mini", .pi).usual == nil, "fewer than 20 timed requests in those 14 days")
    }

    // MARK: Sort and selection

    @Test func theDefaultSortPutsScoresFirstThenTimedRequests() {
        let sorted = PerfHistorySort.standard.sorted(Self.content().rows)
        let scores = sorted.compactMap(\.pooled.score)
        #expect(scores == scores.sorted(by: >))
        #expect(sorted.last?.model == "gemini-3-pro", "rows without a score come after, by timed requests")
        #expect(sorted.prefix(scores.count).allSatisfy { $0.pooled.score != nil })
    }

    @Test func headersToggleTheSortAndTheWindowRemembersIt() {
        var sort = PerfHistorySort.standard
        sort = sort.toggled(.score)
        #expect(sort == PerfHistorySort(column: .score, descending: false))
        sort = sort.toggled(.model)
        #expect(sort == PerfHistorySort(column: .model, descending: false))
        sort = sort.toggled(.component(.L))
        #expect(sort == PerfHistorySort(column: .component(.L), descending: true))
        #expect(sort.toggled(.parts) == sort, "the parts column does not sort")
        #expect(PerfHistorySort(storageValue: sort.storageValue) == sort)
        #expect(PerfHistorySort(storageValue: "parts:desc") == .standard && PerfHistorySort(storageValue: nil) == .standard)
        // A column's missing values go last either way.
        let rows = Self.content().rows
        for descending in [true, false] {
            let byLatency = PerfHistorySort(column: .component(.L), descending: descending).sorted(rows)
            let missing = byLatency.drop { $0.measure(.L) != nil }
            #expect(missing.allSatisfy { $0.measure(.L) == nil } && !missing.isEmpty)
        }
    }

    @Test func theDefaultSelectionIsTheRowWithTheMostTimedRequests() throws {
        let sorted = PerfHistorySort.standard.sorted(Self.content().rows)
        let selected = try #require(PerfHistoryContent.selected(nil, in: sorted))
        #expect(selected.model == "claude-opus-5-5" && selected.n == sorted.map(\.n).max())
        let gpt = try Self.row("gpt-6.1", .codex)
        #expect(PerfHistoryContent.selected(gpt.id, in: sorted)?.id == gpt.id)
        // A choice outside the range falls back to the default.
        let gone = PerfHistoryKey(source: .kimi, model: "kimi-k3")
        #expect(PerfHistoryContent.selected(gone, in: sorted)?.id == selected.id)
        #expect(PerfHistoryContent.selected(nil, in: []) == nil)
    }

    // MARK: Layout

    @Test func theWidthsFollowTheSectionsContentWidth() {
        #expect(PerfHistoryWidth(width: 896) == .wide && PerfHistoryWidth(width: 860) == .wide)
        #expect(PerfHistoryWidth(width: 859) == .medium && PerfHistoryWidth(width: 600) == .medium)
        #expect(PerfHistoryWidth(width: 599) == .narrow && PerfHistoryWidth(width: 356) == .narrow)
        // The window's minimum (720, 24 pt margins, 18 pt padding) is medium.
        #expect(PerfHistoryWidth(width: 720 - 48 - 36) == .medium)
        #expect([PerfHistoryWidth.wide, .medium, .narrow].map(\.chartHeight) == [150, 132, 120])
        #expect(PerfHistoryColumn.visible(.wide).count == 11 && PerfHistoryColumn.visible(.medium).last == .parts)
    }

    @Test func columnsFitWithoutScrollingSideways() {
        for (width, available) in [(PerfHistoryWidth.wide, CGFloat(844)), (.wide, 1200), (.medium, 584), (.medium, 820)] {
            let columns = PerfHistoryColumn.visible(width)
            let layout = PerfHistoryColumnLayout.layout(columns, available: available)
            let total = layout.widths.reduce(0, +) + layout.gap * CGFloat(columns.count - 1)
            #expect(abs(total - available) < 0.5, "\(width) \(available)")
            #expect(layout.widths[0] >= PerfHistoryColumnLayout.minModel, "\(width) \(available)")
            #expect(layout.gap >= PerfHistoryColumnLayout.minGap)
        }
    }

    // MARK: Trend

    @Test func theTrendsScaleRunsFrom250ToAPowerOfTwoTimes250() {
        #expect(PerfTrendGeometry.topValue(nil) == 4000 && PerfTrendGeometry.topValue(3999) == 4000)
        #expect(PerfTrendGeometry.topValue(4000) == 4000 && PerfTrendGeometry.topValue(4001) == 8000)
        #expect(PerfTrendGeometry.topValue(12_840) == 16_000)
        let geometry = PerfTrendGeometry(size: CGSize(width: 836, height: 150), days: 30, maxScore: 1500)
        #expect(geometry.gridValues == [500, 1000, 2000, 4000])
        let plot = geometry.plot
        #expect(abs(geometry.y(1000) - (plot.maxY - plot.height / 2)) < 1e-9, "1000 halfway up to 4000")
        #expect(geometry.y(250) == plot.maxY && geometry.y(100) == plot.maxY && geometry.y(9000) == plot.minY)
        #expect(geometry.index(atX: geometry.x(0)) == 0 && geometry.index(atX: geometry.x(29)) == 29)
        #expect(geometry.index(atX: plot.maxX + 100) == nil)
        #expect(PerfTrendGeometry.runs([1, nil, 2, 3, nil, nil, 4]) == [0..<1, 2..<4, 6..<7])
    }

    // MARK: Words

    @Test func countsAreGroupedInEveryLanguage() {
        #expect(PerfHistoryText.requests(2412, Self.zh) == "2,412 個計時請求")
        #expect(PerfHistoryText.requests(2412, Self.en) == "2,412 timed")
        #expect(PerfHistoryText.sampleHint(12, Self.zh) == "只有 12 個有效計時請求；至少要 20 個才會算出分數。")
    }

    @Test func theChartIsLabelledWithItsLowestHighestAndLatestDay() throws {
        let opus = try Self.row("claude-opus-5-5", .claudeCode)
        let scores = opus.trend.compactMap(\.score).map(perfRoundHalfAway)
        let label = PerfHistoryText.chartLabel(opus, Self.zh)
        let expected = Self.zh("a11y.history.perf.chart", "claude-opus-5-5 · Claude Code", PerfFormat.full(scores.min()!),
                               PerfFormat.full(scores.max()!), PerfFormat.full(scores.last!))
        #expect(label == expected)
        let gemini = try Self.row("gemini-3-pro", .gemini)
        #expect(PerfHistoryText.chartLabel(gemini, Self.en) == "Daily performance of gemini-3-pro · Gemini CLI: no day in this period has enough data")
    }

    @Test func cellsSpeakTheirHintsAndTheBreakdownItsSubScores() throws {
        let gemini = try Self.row("gemini-3-pro", .gemini)
        #expect(PerfHistoryText.cell(gemini, .score, Self.zh) == "\u{2013}")
        #expect(PerfHistoryText.tooltip(gemini, .score, Self.zh) == PerfHistoryText.sampleHint(12, Self.zh))
        #expect(PerfHistoryText.tooltip(gemini, .component(.U), Self.zh) == Self.zh("perf.reason.notRecorded.hint", "Gemini CLI"))
        let opus = try Self.row("claude-opus-5-5", .claudeCode)
        #expect(PerfHistoryText.cell(opus, .timed, Self.zh) == PerfFormat.grouped(opus.n))
        let parts = PerfHistoryText.parts(opus, Self.zh)
        #expect(parts.hasPrefix("構成：輸出吞吐 ") && parts.contains("、"))
        let value = PerfHistoryText.accessibleValue(gemini, columns: PerfHistoryColumn.visible(.wide), Self.zh)
        #expect(value.hasPrefix("性能 " + PerfHistoryText.sampleHint(12, Self.zh)), "`–` speaks its tooltip")
        let copied = PerfHistoryText.copyText([opus], Self.en).split(separator: "\n")
        #expect(copied.count == 2 && copied[0].hasPrefix("Model\tPerf\tUsual\tTimed requests\tThroughput"))
        #expect(copied[1].hasPrefix("claude-opus-5-5 · Claude Code\t"))
    }
}
