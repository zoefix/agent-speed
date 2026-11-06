import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 13 *Usage History window*, data side: the host filter combined with the source filter, the hosts with
/// history, the peak rule and the CSV `host` column.
@Suite("Usage History hosts")
@MainActor
struct HistoryHostFilterTests {
    static let today = DayKey(year: 2026, month: 11, day: 4)!
    static let devbox = "8C0D6B0A-1111-4222-8333-944455556666"
    static let gpu = "1F2E3D4C-5555-4666-8777-988899990000"
    static let names = [devbox: "devbox", gpu: "Alpha"]
    static let l10n = Localizer(preference: .en)

    static func usage(_ day: String, _ source: SourceKind, output: Int, host: String = RemoteHost.localId,
                      peak: Double? = 500) -> DailyUsage {
        DailyUsage(day: day, source: source, totals: TokenTotals(requests: 2, input: output * 10, output: output),
                   measuredOutput: output, measuredMs: 10_000, unfinalized: 0, agents: 1, peak: peak,
                   models: [ModelUsage(model: "gpt-5.5", requests: 2, output: output, measuredOutput: output, measuredMs: 10_000)],
                   cost: CostTally(costMicroUsd: Int64(output), unpricedRequests: 0), host: host)
    }

    static let local = [
        usage("2026-11-03", .codex, output: 1000),
        usage("2026-11-03", .claudeCode, output: 3000),
        usage("2026-11-04", .codex, output: 200),
    ]
    static let mixed = local + [
        usage("2026-11-03", .codex, output: 4000, host: devbox),
        usage("2026-11-04", .claudeCode, output: 600, host: gpu),
    ]

    static func content(_ usage: [DailyUsage], source: SourceKind? = nil, host: String? = nil) -> HistoryContent {
        HistoryContent.build(usage: usage, source: source, host: host, hostNames: names, rangeKind: .days7, customRange: nil,
                             sort: HistorySort(), today: today, l10n: l10n)
    }

    @Test func theHostFilterCombinesWithTheSourceFilter() {
        let all = HistoryAggregator.daily(Self.mixed, source: nil)
        #expect(all.map(\.output) == [8000, 800])
        #expect(HistoryAggregator.daily(Self.mixed, source: nil, host: RemoteHost.localId).map(\.output) == [4000, 200])
        #expect(HistoryAggregator.daily(Self.mixed, source: nil, host: Self.devbox).map(\.output) == [4000])
        #expect(HistoryAggregator.daily(Self.mixed, source: .codex, host: Self.devbox).map(\.output) == [4000])
        #expect(HistoryAggregator.daily(Self.mixed, source: .claudeCode, host: Self.devbox).isEmpty)
        #expect(Self.content(Self.mixed, source: .codex, host: RemoteHost.localId).days.map(\.output) == [1000, 200])
    }

    @Test func thePeakIsShownOnlyForAllHostsAndAllSources() {
        #expect(HistoryAggregator.daily(Self.mixed, source: nil).first?.peak == 500)
        #expect(HistoryAggregator.daily(Self.mixed, source: nil, host: RemoteHost.localId).allSatisfy { $0.peak == nil })
        #expect(HistoryAggregator.daily(Self.mixed, source: .codex).allSatisfy { $0.peak == nil })
        #expect(Self.content(Self.mixed, host: Self.devbox).days.allSatisfy { $0.peak == nil })
    }

    @Test func hostsWithHistoryListThisMacFirstThenByName() {
        #expect(HistoryHosts.withHistory(Self.local, names: Self.names).isEmpty)
        let hosts = HistoryHosts.withHistory(Self.mixed, names: Self.names)
        #expect(hosts.map(\.id) == [RemoteHost.localId, Self.gpu, Self.devbox])
        #expect(hosts.map(\.name) == [nil, "Alpha", "devbox"])
        // A removed host without a stored name still lists, by its id; a host without usage does not.
        let empty = DailyUsage(day: "2026-11-01", source: .codex, totals: .zero, measuredOutput: 0, measuredMs: 0,
                               unfinalized: 0, agents: 0, peak: nil, models: [], host: "X")
        let removed = HistoryHosts.withHistory(Self.mixed + [empty], names: [Self.gpu: "Alpha"])
        #expect(removed.map(\.id) == [RemoteHost.localId, Self.devbox, Self.gpu])
        #expect(removed[1].name == Self.devbox)
        #expect(Self.content(Self.mixed).hosts == hosts)
    }

    @Test func aRemoteHostHasNoAgentTraffic() {
        let traffic = HistoryTraffic(days: [TrafficDay(day: "2026-11-03", key: "codex", rx: 10, tx: 5, rxTools: 0, txTools: 0,
                                                       sampledMs: 1)], since: nil, includeTools: true, bits: false)
        func build(_ host: String?) -> HistoryContent {
            HistoryContent.build(usage: Self.mixed, source: nil, host: host, hostNames: Self.names, rangeKind: .days7,
                                 customRange: nil, sort: HistorySort(), today: Self.today, l10n: Self.l10n, traffic: traffic)
        }
        #expect(build(nil).columns.contains(.traffic) && build(RemoteHost.localId).columns.contains(.traffic))
        #expect(!build(Self.devbox).columns.contains(.traffic))
    }

    @Test func theCSVHasAHostColumnOnlyWhileHostHistoryExists() {
        let plain = Self.content(Self.local)
        #expect(plain.csvHost == nil)
        let csv = HistoryCSV.build(plain.rows, source: nil, host: plain.csvHost)
        #expect(csv == HistoryCSV.build(plain.rows, source: nil), "without host history the export is unchanged")
        #expect(!csv.components(separatedBy: "\r\n")[0].contains("host"))

        func lines(_ host: String?) -> [String] {
            let content = Self.content(Self.mixed, host: host)
            return HistoryCSV.build(content.rows, source: nil, host: content.csvHost).components(separatedBy: "\r\n")
                .filter { !$0.isEmpty }
        }
        #expect(lines(nil)[0].hasSuffix(",price_version,host"))
        #expect(lines(nil).dropFirst().allSatisfy { $0.hasSuffix(",all") })
        #expect(lines(RemoteHost.localId).dropFirst().allSatisfy { $0.hasSuffix(",local") })
        #expect(lines(Self.devbox).dropFirst().map { $0.hasSuffix(",devbox") } == [true])
    }

    @Test func machineTrafficRowsKeepTheirColumnsWithAHostColumn() {
        let traffic = HistoryTraffic(days: [
            TrafficDay(day: "2026-11-03", key: "codex", rx: 10, tx: 5, rxTools: 0, txTools: 0, sampledMs: 1),
            TrafficDay(day: "2026-11-03", key: TrafficDay.machineKey, rx: 70, tx: 30, rxTools: 0, txTools: 0, sampledMs: 1),
        ], since: nil, includeTools: true, bits: false)
        let content = HistoryContent.build(usage: Self.mixed, source: nil, hostNames: Self.names, rangeKind: .days7,
                                           customRange: nil, sort: HistorySort(), today: Self.today, l10n: Self.l10n,
                                           traffic: traffic)
        let rows = HistoryCSV.build(content.rows, source: nil, traffic: true, host: content.csvHost)
            .components(separatedBy: "\r\n").map { $0.components(separatedBy: ",") }
        let header = rows[0]
        let machine = rows.first { $0.count > 1 && $0[0] == "2026-11-03" && $0[1] == "machine" }
        #expect(machine?.count == header.count)
        #expect(machine.map { $0[header.count - 5] == "70" && $0[header.count - 4] == "30" && $0.last == "local" } == true)
    }

    @Test func aHostOpenedFromTheDashboardIsOfferedBeforeItHasHistory() {
        let fresh = "5E1A2B3C-0000-4111-8222-933344445555"
        let names = Self.names.merging([fresh: "fresh"]) { $1 }
        let content = HistoryContent.build(usage: Self.local, source: nil, host: fresh, hostNames: names, rangeKind: .days7,
                                           customRange: nil, sort: HistorySort(), today: Self.today, l10n: Self.l10n)
        #expect(content.hosts.map(\.id) == [RemoteHost.localId, fresh])
        #expect(content.host == fresh && content.days.isEmpty)
        #expect(content.hostName(Self.l10n) == "fresh")
        // This Mac alone is every host while no remote host has history: nothing about hosts shows.
        let local = Self.content(Self.local, host: RemoteHost.localId)
        #expect(local.hosts.isEmpty && local.host == nil && local.hostName(Self.l10n) == nil)
        #expect(local == Self.content(Self.local))
    }

    @Test func theHeaderNamesTheSelectedHost() {
        #expect(Self.content(Self.mixed).hostName(Self.l10n) == nil)
        #expect(Self.content(Self.mixed, host: RemoteHost.localId).hostName(Self.l10n) == "This Mac")
        #expect(Self.content(Self.mixed, source: .codex, host: Self.gpu).hostName(Self.l10n) == "Alpha")
    }

    @Test func withoutRemoteRowsTheContentIsUnchanged() {
        let before = HistoryContent.build(usage: Self.local, source: nil, rangeKind: .days7, customRange: nil,
                                          sort: HistorySort(), today: Self.today, l10n: Self.l10n)
        #expect(Self.content(Self.local) == before)
        #expect(before.hosts.isEmpty && before.host == nil)
        #expect(HistoryAggregator.daily(Self.local, source: nil) == HistoryAggregator.daily(Self.local, source: nil, host: nil))
    }
}
