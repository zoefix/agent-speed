import Foundation
import Testing
@testable import AgentSpeedCore

/// Orbs and boards over (host, source) cells (DESIGN 13, METRICS 6.8).
@Suite("Orb subsets with hosts")
struct OrbSubsetHostTests {
    static let local = RemoteHost.localId
    static let a = RemoteLive.a
    static let b = RemoteLive.b

    static func cell(_ host: String, _ source: SourceKind, available: Bool = true, live: Double = 0, estimated: Double = 0,
                     output: Int = 0, cost: Double = 0) -> HostSourceStatus {
        HostSourceStatus(cell: HostSource(host: host, source: source), available: available, sessionCount: 1,
                         live: Rate(output: live, total: live * 2), liveEstimated: Rate(output: estimated, total: estimated * 2),
                         liveCost: LiveCost(microUsdPerSecond: cost, knownOutput: live),
                         today: TokenTotals(requests: output > 0 ? 1 : 0, output: output),
                         todayCost: CostTally(costMicroUsd: Int64(output), unpricedRequests: 0))
    }

    static func agent(_ id: String, _ host: String, _ source: SourceKind, _ phase: AgentPhase, parent: String? = nil) -> AgentStatus {
        AgentStatus(id: id, source: source, project: "p", model: nil, phase: phase, isSubagent: parent != nil, live: .zero,
                    recentSpeed: nil, lastActivity: Date(timeIntervalSince1970: 0), parentId: parent, host: host)
    }

    static var snapshot: SpeedSnapshot {
        var snapshot = SpeedSnapshot.empty
        snapshot.state = .running
        let cells = [
            cell(local, .codex, live: 10, output: 100, cost: 1),
            cell(a, .codex, live: 20, estimated: 5, output: 200, cost: 2),
            cell(a, .claudeCode, live: 30, output: 300, cost: 3),
            cell(b, .codex, available: false, output: 50),
        ]
        snapshot.hostSources = cells
        snapshot.hosts = [
            HostStatus(id: local, name: local, isConnected: true),
            HostStatus(id: a, name: "devbox", remote: RemoteHostStatus(status: .connected), isConnected: true),
            HostStatus(id: b, name: "gpu", remote: RemoteHostStatus(status: .unreachable), isConnected: false),
        ]
        snapshot.sources = SourceKind.allCases.map { kind in
            let members = cells.filter { $0.source == kind }
            var today = TokenTotals()
            var todayCost = CostTally()
            var live = Rate.zero
            var estimated = Rate.zero
            var cost = LiveCost.zero
            for member in members {
                today.add(member.today)
                todayCost.add(member.todayCost)
                live.add(member.live)
                estimated.add(member.liveEstimated)
                cost.add(member.liveCost)
            }
            return SourceStatus(kind: kind, enabled: kind != .gemini, rootPath: "/", available: kind == .codex || kind == .claudeCode,
                                sessionCount: members.count, live: live, today: today, activeAgents: 0,
                                liveEstimated: estimated, liveCost: cost, todayCost: todayCost)
        }
        snapshot.live = Rate(output: 60, total: 120)
        snapshot.today = TokenTotals(requests: 4, output: 650)
        snapshot.agents = [
            agent("local-codex", local, .codex, .generating),
            agent("a-codex", a, .codex, .generating),
            agent("a-claude", a, .claudeCode, .tools, parent: "a-codex"),
            agent("b-codex", b, .codex, .idle),
        ]
        snapshot.subsetPeaks = [SubsetKey(SourceSet(.codex), hosts: [a]): 77]
        return snapshot
    }

    @Test func everyHostIsTheSourceOnlyReading() {
        let snapshot = Self.snapshot
        for sources in [nil, SourceSet(.codex), SourceSet([.codex, .gemini])] {
            #expect(OrbSubset.reading(snapshot, sources: sources, hosts: nil) == OrbSubset.reading(snapshot, sources: sources))
            #expect(OrbSubset.cost(snapshot, sources: sources, hosts: nil, billable: .everything)
                == OrbSubset.cost(snapshot, sources: sources, billable: .everything))
        }
    }

    @Test func oneHostCountsItsCellsAndAgents() {
        let all = OrbSubset.reading(Self.snapshot, sources: nil, hosts: [Self.a])
        #expect(all.live.output == 50)
        #expect(all.today.output == 500)
        #expect(all.agents.map(\.id) == ["a-codex", "a-claude"])
        #expect(all.agents.last?.parentId == "a-codex")
        #expect(all.generatingSources == [.codex])
        #expect(all.state == .available && all.unavailable.isEmpty)

        let codex = OrbSubset.reading(Self.snapshot, sources: SourceSet(.codex), hosts: [Self.a])
        #expect(codex.live == Rate(output: 20, total: 40))
        #expect(abs(codex.estimatedShare - 0.25) < 1e-12)
        #expect(codex.agents.map(\.id) == ["a-codex"])
        #expect(codex.measuredPeakOutput == 77)
        #expect(OrbSubset.reading(Self.snapshot, sources: SourceSet(.codex), hosts: [Self.local]).measuredPeakOutput == nil)

        let pair = OrbSubset.reading(Self.snapshot, sources: SourceSet(.codex), hosts: [Self.local, Self.a])
        #expect(pair.live.output == 30 && pair.today.output == 300)
        #expect(pair.agents.map(\.id) == ["local-codex", "a-codex"])
    }

    @Test func aSourceIsAvailableOnlyWhereItIsDetectedOrHasData() {
        let reading = OrbSubset.reading(Self.snapshot, sources: SourceSet([.claudeCode, .gemini]), hosts: [Self.local])
        #expect(reading.state == .allNotFound)
        #expect(reading.unavailable == [.claudeCode, .gemini])
        let remote = OrbSubset.reading(Self.snapshot, sources: SourceSet([.claudeCode, .gemini]), hosts: [Self.a])
        #expect(remote.state == .available && remote.unavailable == [.gemini])
    }

    @Test func onlyDisconnectedHostsAreOffline() {
        let offline = OrbSubset.reading(Self.snapshot, sources: SourceSet(.codex), hosts: [Self.b])
        #expect(offline.state == .hostsOffline)
        #expect(offline.unavailable == [.codex])
        #expect(offline.today.output == 50, "a disconnected host's responses keep counting")
        #expect(OrbSubset.reading(Self.snapshot, sources: nil, hosts: [Self.b]).state == .hostsOffline)
        #expect(OrbSubset.reading(Self.snapshot, sources: SourceSet(.codex), hosts: [Self.b, Self.local]).state == .available)
        #expect(OrbSubset.reading(Self.snapshot, sources: SourceSet(.codex), hosts: [Self.b, Self.a]).state == .available)
        #expect(OrbSubset.reading(Self.snapshot, sources: nil, hosts: ["removed"]).state == .hostsOffline)
    }

    @Test func eventsFollowHostsAndSources() {
        let summary = ResponseSummary(source: .codex, project: "p", model: nil, outputTokens: 1, totalTokens: 2, duration: nil,
                                      speed: nil, endedAt: Date(), host: Self.a)
        let event = SpeedEvent.responseCompleted(summary)
        #expect(OrbSubset.accepts(event, sources: nil, hosts: nil))
        #expect(OrbSubset.accepts(event, sources: SourceSet(.codex), hosts: [Self.a]))
        #expect(!OrbSubset.accepts(event, sources: nil, hosts: [Self.local]))
        #expect(!OrbSubset.accepts(event, sources: SourceSet(.claudeCode), hosts: [Self.a]))
        #expect(OrbSubset.accepts(.newPeak(50), sources: nil, hosts: nil))
        #expect(!OrbSubset.accepts(.newPeak(50), sources: nil, hosts: [Self.local, Self.a, Self.b]))
    }

    @Test func costCountsTheSelectedCells() {
        let reading = OrbSubset.cost(Self.snapshot, sources: nil, hosts: [Self.a], billable: .everything)
        #expect(reading.microUsdPerSecond == 5)
        #expect(reading.liveOutput == 50)
        #expect(reading.today.costMicroUsd == 500)
        let billable = OrbSubset.cost(Self.snapshot, sources: nil, hosts: [Self.a], billable: SourceSet(.codex))
        #expect(billable.microUsdPerSecond == 2 && billable.excluded == [.claudeCode])
    }

    @Test func withoutRemoteHostsOnlyThisComputerExists() {
        var snapshot = Self.snapshot
        snapshot.hosts = []
        snapshot.hostSources = []
        snapshot.subsetPeaks = [SubsetKey(SourceSet(.codex), hosts: [Self.local]): 12]
        var local = OrbSubset.reading(snapshot, sources: SourceSet(.codex), hosts: [Self.local])
        #expect(local.measuredPeakOutput == 12)
        local.measuredPeakOutput = snapshot.subsetPeaks[SourceSet(.codex)]
        #expect(local == OrbSubset.reading(snapshot, sources: SourceSet(.codex)))
        #expect(OrbSubset.reading(snapshot, sources: nil, hosts: [Self.local]).agents == snapshot.agents)
        #expect(OrbSubset.reading(snapshot, sources: nil, hosts: [Self.a]).state == .hostsOffline)
    }
}
