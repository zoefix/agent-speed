import Foundation
import Testing
@testable import AgentSpeedCore

/// The engine's host dimension (REMOTE 7, DESIGN 13): cross-host deduplication, (host, source) cells and host
/// subset peaks.
@Suite("Remote engine")
struct RemoteEngineTests {
    typealias L = RemoteLive
    static let local = RemoteHost.localId

    @Test func theFirstArrivalOfAKeyWinsAcrossHosts() {
        var engine = SpeedEngine()
        let localFirst = L.record("k1", host: Self.local, start: L.now - 5000, end: L.now - 3000, output: 100)
        engine.upsert(localFirst, project: "p", isCompletion: true, emitsEvents: false)
        let copy = L.record("k1", host: L.a, start: L.now - 5000, end: L.now - 3000, output: 999)
        #expect(engine.upsert(copy, project: "p", isCompletion: true, emitsEvents: true, nowMs: L.now) == nil)

        let remoteFirst = L.record("k2", host: L.a, start: L.now - 4000, end: L.now - 2000, output: 50)
        engine.upsert(remoteFirst, project: "p", isCompletion: true, emitsEvents: false)
        let localCopy = L.record("k2", host: Self.local, start: L.now - 4000, end: L.now - 2000, output: 777)
        #expect(engine.upsert(localCopy, project: "p", isCompletion: true, emitsEvents: true, nowMs: L.now) == nil)
        let otherHost = L.record("k2", host: L.b, start: L.now - 4000, end: L.now - 2000, output: 555)
        #expect(engine.upsert(otherHost, project: "p", isCompletion: true, emitsEvents: true, nowMs: L.now) == nil)

        // The holder's own updates still apply.
        var grown = localFirst
        grown.usage.output = 120
        engine.upsert(grown, project: "p", isCompletion: false, emitsEvents: true, nowMs: L.now)
        let output = engine.advance(nowMs: L.now, todayStartMs: L.day, emitsEvents: false)
        #expect(output.today.output == 170)
        #expect(output.today.requests == 2)
        #expect(Set(engine.storedResponses.map(\.host)) == [Self.local, L.a])
        #expect(engine.storedResponses.first { $0.key == "k2" }?.host == L.a)
    }

    static func busyEngine(tracksCells: Bool) -> SpeedEngine {
        var engine = SpeedEngine()
        engine.tracksCells = tracksCells
        let records = [
            L.record("l1", host: Self.local, start: L.now - 2500, end: L.now - 500, output: 400, cost: 3000),
            L.record("l2", host: Self.local, agent: "c", source: .claudeCode, start: L.now - 2000, end: L.now, output: 300),
            L.record("a1", host: L.a, start: L.now - 2800, end: L.now - 200, output: 260, cost: 1200),
            L.record("a2", host: L.a, agent: "c", source: .claudeCode, start: L.now - 1500, end: L.now - 100, output: 140,
                     cost: 500),
            L.record("a3", host: L.a, start: L.now - 2600, end: L.now - 900, output: 90, isFinal: false),
            L.record("b1", host: L.b, start: L.now - 2900, end: L.now - 300, output: 75),
            L.record("old", host: L.b, start: L.day - 20_000, end: L.day - 10_000, output: 5000, cost: 9000),
        ]
        for record in records { engine.upsert(record, project: "p", isCompletion: true, emitsEvents: false) }
        engine.setAgent(L.agent(L.a, "a", phase: .generating, inFlightStartMs: L.now - 1500, lastLineMs: L.now - 100)
            .engineAgent(parentId: nil))
        engine.setAgent(EngineAgent(id: L.agentId(Self.local, "a"), source: .codex, project: "p", model: "gpt-5.5",
                                    phase: .generating, inFlightStartMs: L.now - 800, lastLineMs: L.now - 100))
        return engine
    }

    @Test func cellsAddUpToThePerSourceValues() {
        var engine = Self.busyEngine(tracksCells: true)
        let output = engine.advance(nowMs: L.now, todayStartMs: L.day, emitsEvents: false)
        #expect(Set(output.cells.keys.map(\.host)) == [Self.local, L.a, L.b])
        for kind in [SourceKind.codex, .claudeCode] {
            let cells = output.cells.filter { $0.key.source == kind }.map(\.value)
            let live = cells.reduce(0.0) { $0 + $1.live.output }
            let total = cells.reduce(0.0) { $0 + $1.live.total }
            let estimated = cells.reduce(0.0) { $0 + $1.liveEstimated.output }
            let cost = cells.reduce(0.0) { $0 + $1.liveCost.microUsdPerSecond }
            let known = cells.reduce(0.0) { $0 + $1.liveCost.knownOutput }
            let thinking = cells.reduce(0.0) { $0 + $1.intelligence.output }
            #expect(abs(live - (output.sourceLive[kind]?.output ?? 0)) < 1e-9, "\(kind)")
            #expect(abs(total - (output.sourceLive[kind]?.total ?? 0)) < 1e-9)
            #expect(abs(estimated - (output.sourceLiveEstimated[kind]?.output ?? 0)) < 1e-9)
            #expect(abs(cost - (output.sourceLiveCost[kind]?.microUsdPerSecond ?? 0)) < 1e-9)
            #expect(abs(known - (output.sourceLiveCost[kind]?.knownOutput ?? 0)) < 1e-9)
            #expect(abs(thinking - (output.sourceIntelligence[kind]?.output ?? 0)) < 1e-9)
            var today = TokenTotals()
            var todayCost = CostTally()
            for cell in cells {
                today.add(cell.today)
                todayCost.add(cell.todayCost)
            }
            #expect(today == output.todayBySource[kind] ?? TokenTotals())
            #expect(todayCost == output.todayCostBySource[kind] ?? CostTally())
        }
        let remoteCodex = output.cells[HostSource(host: L.a, source: .codex)]
        #expect((remoteCodex?.liveEstimated.output ?? 0) > 0, "the unmeasured a3 and the in-flight agent")
        #expect(output.cells[HostSource(host: L.b, source: .codex)]?.unpriced == ["gpt-5.5": 1])
        #expect(output.cells[HostSource(host: L.b, source: .codex)]?.today.requests == 1, "yesterday's is not today's")
    }

    @Test func withoutCellsTheOutputIsUnchanged() {
        var plain = Self.busyEngine(tracksCells: false)
        var tracked = Self.busyEngine(tracksCells: true)
        let a = plain.advance(nowMs: L.now, todayStartMs: L.day, emitsEvents: true)
        let b = tracked.advance(nowMs: L.now, todayStartMs: L.day, emitsEvents: true)
        #expect(a.cells.isEmpty && !b.cells.isEmpty)
        #expect(a.live == b.live)
        #expect(a.liveEstimatedShare == b.liveEstimatedShare)
        #expect(a.sourceLive == b.sourceLive)
        #expect(a.sourceLiveEstimated == b.sourceLiveEstimated)
        #expect(a.sourceLiveCost == b.sourceLiveCost)
        #expect(a.agents == b.agents)
        #expect(a.history == b.history)
        #expect(a.today == b.today)
        #expect(a.peakToday == b.peakToday)
    }

    @Test func hostSubsetPeaksCountTheirCellsOnly() {
        let records = [
            L.record("l1", host: Self.local, start: L.day + 10_000, end: L.day + 13_000, output: 900),
            L.record("a1", host: L.a, start: L.day + 11_000, end: L.day + 14_000, output: 600),
            L.record("a2", host: L.a, agent: "c", source: .claudeCode, start: L.day + 11_000, end: L.day + 13_000, output: 800),
            L.record("b1", host: L.b, start: L.day + 50_000, end: L.day + 52_000, output: 2000),
        ]
        func ledger(_ subset: [ResponseRecord]) -> DayLedger {
            var ledger = DayLedger(dayStartMs: L.day)
            for record in subset { ledger.replace(nil, with: record) }
            return ledger
        }
        var all = ledger(records)
        let codex = SourceSet(.codex)
        let cases: [(SubsetKey, [ResponseRecord])] = [
            (SubsetKey(codex), records.filter { $0.source == .codex }),
            (SubsetKey(codex, hosts: [Self.local]), [records[0]]),
            (SubsetKey(codex, hosts: [L.a]), [records[1]]),
            (SubsetKey(.everything, hosts: [L.a]), [records[1], records[2]]),
            (SubsetKey(codex, hosts: [Self.local, L.a]), [records[0], records[1]]),
            (SubsetKey(.everything, hosts: ["unknown"]), []),
        ]
        for (key, members) in cases {
            var alone = ledger(members)
            #expect(abs(all.measuredPeakOutput(for: key) - alone.measuredPeak().output) < 1e-3, "\(key)")
        }
        #expect(abs(all.measuredPeakOutput(for: .everything) - all.measuredPeak().output) < 1e-3)

        // A new response on host B recomputes only the subsets that contain its cell.
        let scans = all.subsetPeakScans
        all.replace(nil, with: L.record("b2", host: L.b, start: L.day + 60_000, end: L.day + 61_000, output: 100))
        for (key, _) in cases { _ = all.measuredPeakOutput(for: key) }
        #expect(all.subsetPeakScans == scans + 1, "only all-hosts codex contains host B's codex cell")
    }

    @Test func aHostAboveItsLimitLosesItsOldestResponses() {
        var engine = SpeedEngine()
        engine.remoteResponseLimit = 10
        engine.upsert(L.record("local", host: Self.local, start: L.day + 1000, end: L.day + 2000, output: 1),
                      project: "p", isCompletion: true, emitsEvents: false)
        for index in 0..<15 {
            let end = L.day + 10_000 + Int64(index) * 1000
            engine.upsert(L.record("a\(index)", host: L.a, start: end - 500, end: end, output: 10), project: "p",
                          isCompletion: true, emitsEvents: false)
        }
        let held = engine.storedResponses.filter { $0.host == L.a }.map(\.key)
        #expect(held.count <= 10 && held.count >= 9)
        #expect(held.contains("a14") && !held.contains("a0"))
        #expect(engine.storedResponses.contains { $0.key == "local" })
        let output = engine.advance(nowMs: L.now, todayStartMs: L.day, emitsEvents: false)
        #expect(output.today.requests == held.count + 1)
    }

    @Test func idleAndRemovalTouchOnlyTheirHost() {
        var engine = Self.busyEngine(tracksCells: true)
        engine.setIdle(host: L.a)
        #expect(engine.agents[L.agentId(L.a, "a")]?.phase == .idle)
        #expect(engine.agents[L.agentId(L.a, "a")]?.inFlightStartMs == nil)
        #expect(engine.agents[L.agentId(Self.local, "a")]?.phase == .generating)
        engine.removeHost(L.a)
        #expect(engine.agents[L.agentId(L.a, "a")] == nil)
        #expect(!engine.storedResponses.contains { $0.host == L.a })
        let output = engine.advance(nowMs: L.now, todayStartMs: L.day, emitsEvents: false)
        #expect(output.cells.keys.allSatisfy { $0.host != L.a })
    }
}
