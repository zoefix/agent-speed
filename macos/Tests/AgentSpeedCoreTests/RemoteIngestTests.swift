import Foundation
import Testing
@testable import AgentSpeedCore

/// Live ingestion of a host's lines (REMOTE 7): seeding, live events, reconnects, disconnects, monitor restarts and the
/// snapshot's hosts.
@Suite("Remote ingestion")
struct RemoteIngestTests {
    typealias L = RemoteLive

    struct Harness {
        let directory: TempDirectory
        let feed = L.feed()
        let sink: SessionSink

        init(hosts: [RemoteHostInfo] = [RemoteHostInfo(id: L.a, name: "devbox")]) throws {
            directory = try TempDirectory()
            sink = SessionSink(L.session(directory))
            sink.session.applyRemote(feed.attach(sink))
            feed.setHosts(hosts)
        }

        func send(_ events: [RemoteHostEvent]) {
            for event in events { feed.receive(event) }
        }

        var engineAgents: [String: EngineAgent] {
            Dictionary(uniqueKeysWithValues: sink.session.perfAgents().agents.map { ($0.id, $0) })
        }
    }

    static let seeded = [
        L.agent(L.a, "a", lastLineMs: L.now - 200),
        L.agent(L.a, "b", source: .claudeCode, lastLineMs: L.now - 30_000),
    ]
    static let generating = L.agent(L.a, "a", phase: .generating, inFlightStartMs: L.now - 1500, lastLineMs: L.now - 200)
    static let seededResponses = [
        L.record("k1", host: L.a, start: L.now - 60_000, end: L.now - 50_000, output: 500),
        L.record("k2", host: L.a, agent: "b", source: .claudeCode, start: L.now - 40_000, end: L.now - 30_000, output: 300),
    ]

    static func seedingLines(generating: Bool = false) -> [RelayMessage] {
        (generating ? [seeded[1], Self.generating] : seeded).map { RelayMessage.agent($0) } + seededResponses.map { L.response($0) }
            + [.parents([L.agentId(L.a, "b"): L.agentId(L.a, "a")]), L.report([(.codex, true, 1), (.claudeCode, true, 1)])]
    }

    @Test func seededLinesAreSilentAndLiveLinesAnnounce() throws {
        let harness = try Harness()
        harness.send(L.seeding(L.a, connection: 7, Self.seedingLines()))
        var (output, events) = harness.sink.establish()
        #expect(events.isEmpty, "seeding never announces")
        #expect(output.today.output == 800)
        #expect(output.agents.map(\.agent.id).contains(L.agentId(L.a, "a")))
        #expect(output.agents.first { $0.agent.id == L.agentId(L.a, "b") }?.parentId == L.agentId(L.a, "a"))
        #expect(output.agents.allSatisfy { $0.agent.host == L.a })

        let live = L.record("k3", host: L.a, start: L.now - 2000, end: L.now - 100, output: 200)
        harness.send([L.message(L.a, 7, L.response(live), seeded: false)])
        (output, events) = harness.sink.advance(L.now + 500)
        let completions = SessionSink.completions(events)
        #expect(completions.count == 1)
        #expect(completions.first?.host == L.a && completions.first?.outputTokens == 200)
        #expect(output.today.output == 1000)
        #expect((output.sourceLive[.codex]?.output ?? 0) > 0)
    }

    @Test func aReconnectSeedsAgainWithoutCountingTwice() throws {
        let harness = try Harness()
        harness.send(L.seeding(L.a, connection: 7, Self.seedingLines()))
        let (first, _) = harness.sink.establish()

        // The connection drops; a new one seeds the same responses and only one of the agents.
        harness.send([L.status(L.a, 7, .unreachable), L.status(L.a, 9, .connecting),
                      L.message(L.a, 9, L.hello, seeded: true), L.status(L.a, 9, .seeding(fraction: 0))])
        harness.send([L.message(L.a, 9, .agent(Self.seeded[0]), seeded: true)]
            + Self.seededResponses.map { L.message(L.a, 9, L.response($0), seeded: true) })
        #expect(harness.engineAgents[L.agentId(L.a, "b")] != nil, "the old state stays until ready")
        var (output, events) = harness.sink.advance(L.now + 500)
        #expect(events.isEmpty)
        #expect(output.today == first.today)
        #expect(output.peakToday == first.peakToday)

        harness.send([L.message(L.a, 9, .ready, seeded: true), L.status(L.a, 9, .connected)])
        (output, events) = harness.sink.advance(L.now + 1000)
        #expect(events.isEmpty, "no completion and no peak from a re-seed")
        #expect(harness.engineAgents[L.agentId(L.a, "b")] == nil, "an agent the new connection did not send leaves")
        #expect(harness.engineAgents[L.agentId(L.a, "a")] != nil)
        #expect(output.today == first.today)
        #expect(harness.feed.mirror(L.a)?.parents.isEmpty == true, "a parent map the new connection never sent is gone")
    }

    @Test func aDisconnectedHostsAgentsBecomeIdle() throws {
        let harness = try Harness()
        harness.send(L.seeding(L.a, connection: 3, Self.seedingLines(generating: true)))
        let (before, _) = harness.sink.establish()
        #expect((before.sourceLiveEstimated[.codex]?.output ?? 0) > 0, "the in-flight estimate runs while connected")

        harness.send([L.status(L.a, 3, .unreachable)])
        let agent = try #require(harness.engineAgents[L.agentId(L.a, "a")])
        #expect(agent.phase == .idle && agent.inFlightStartMs == nil)
        let (after, _) = harness.sink.advance(L.now + 500)
        #expect((after.sourceLiveEstimated[.codex]?.output ?? 0) == 0)
        #expect(after.today == before.today, "its responses keep counting")
        #expect(harness.feed.mirror(L.a)?.agents.values.allSatisfy { $0.phase == .idle } == true)
    }

    @Test func aNewSessionReplaysTheMirrorAsSeeded() throws {
        let harness = try Harness()
        harness.send(L.seeding(L.a, connection: 4, Self.seedingLines()))
        let live = L.record("k3", host: L.a, start: L.now - 2000, end: L.now - 100, output: 200)
        harness.send([L.message(L.a, 4, L.response(live), seeded: false)])
        let (old, _) = harness.sink.establish()

        // The monitor restarts: a new session attaches and receives everything as seeded, without a reconnect.
        let restarted = SessionSink(L.session(harness.directory))
        restarted.session.applyRemote(harness.feed.attach(restarted))
        var (output, events) = restarted.establish()
        #expect(events.isEmpty)
        #expect(output.today == old.today)
        #expect(Set(output.agents.map(\.agent.id)) == Set(old.agents.map(\.agent.id)))
        #expect(output.agents.first { $0.agent.id == L.agentId(L.a, "b") }?.parentId == L.agentId(L.a, "a"))
        #expect(restarted.session.remote.isConnected(L.a))

        // Later lines reach only the attached session.
        let batches = harness.sink.batches
        let next = L.record("k4", host: L.a, start: L.now - 1000, end: L.now, output: 50)
        harness.send([L.message(L.a, 4, L.response(next), seeded: false)])
        #expect(harness.sink.batches == batches)
        (output, events) = restarted.advance(L.now + 500)
        #expect(output.today.output == old.today.output + 50)
        #expect(SessionSink.completions(events).map(\.outputTokens) == [50])
    }

    @Test func removedHostsLeaveAndUnknownHostsAreIgnored() throws {
        let harness = try Harness(hosts: [RemoteHostInfo(id: L.a, name: "devbox"), RemoteHostInfo(id: L.b, name: "gpu")])
        harness.send(L.seeding(L.a, connection: 1, Self.seedingLines()))
        harness.send(L.seeding(L.b, connection: 2, [L.response(L.record("b1", host: L.b, start: L.now - 9000,
                                                                          end: L.now - 8000, output: 70))]))
        harness.send([L.message("8C0D6B0A-1111-4222-8333-0000000000FF", 5,
                                L.response(L.record("x", host: L.b, start: L.now - 9000, end: L.now - 8000, output: 1)),
                                seeded: false)])
        var (output, _) = harness.sink.advance()
        #expect(output.today.output == 870)
        harness.feed.setHosts([RemoteHostInfo(id: L.b, name: "gpu")])
        (output, _) = harness.sink.advance(L.now + 500)
        #expect(output.today.output == 70)
        #expect(output.agents.allSatisfy { $0.agent.host != L.a })
        #expect(harness.feed.mirror(L.a) == nil)
    }

    @Test func disabledSourcesStayOut() throws {
        let directory = try TempDirectory()
        let feed = L.feed()
        let sink = SessionSink(L.session(directory, sources: [.codex]))
        sink.session.applyRemote(feed.attach(sink))
        feed.setHosts([RemoteHostInfo(id: L.a, name: "devbox")])
        for event in L.seeding(L.a, connection: 1, Self.seedingLines()) { feed.receive(event) }
        let (output, _) = sink.advance()
        #expect(output.today.output == 500)
        #expect(output.agents.allSatisfy { $0.agent.source == .codex })
    }

    @Test func theSnapshotListsHostsAndCells() throws {
        let harness = try Harness(hosts: [RemoteHostInfo(id: L.a, name: "devbox"), RemoteHostInfo(id: L.b, name: "gpu")])
        harness.send(L.seeding(L.a, connection: 1, Self.seedingLines()))
        harness.send([L.status(L.b, 0, .disabled)])
        let (output, _) = harness.sink.advance()
        let snapshot = harness.sink.snapshot(output)
        #expect(snapshot.hosts.map(\.id) == [RemoteHost.localId, L.a, L.b])
        #expect(snapshot.hosts.map(\.name) == [RemoteHost.localId, "devbox", "gpu"])
        #expect(snapshot.hosts.map(\.isConnected) == [true, true, false])
        #expect(snapshot.hosts[1].remote?.status == .connected)
        #expect(snapshot.hosts[2].remote?.status == .disabled)
        #expect(snapshot.hosts[1].today.output == 800 && snapshot.hosts[1].sessions == 2)
        #expect(snapshot.agents.first { $0.id == L.agentId(L.a, "a") }?.host == L.a)
        // Not detected on this computer, but on the connected host.
        #expect(snapshot.sources.first { $0.kind == .codex }?.available == true)
        #expect(snapshot.sources.first { $0.kind == .codex }?.sessionCount == 1)
        let codexCell = try #require(snapshot.hostSources.first { $0.cell == HostSource(host: L.a, source: .codex) })
        #expect(codexCell.available && codexCell.today.output == 500 && codexCell.sessionCount == 1)
        for status in snapshot.sources {
            let cells = snapshot.hostSources.filter { $0.source == status.kind }
            var today = TokenTotals()
            for cell in cells { today.add(cell.today) }
            #expect(today == status.today, "\(status.kind)")
            #expect(abs(cells.reduce(0) { $0 + $1.live.output } - status.live.output) < 1e-9)
        }

        // Without any configured host the snapshot has neither.
        let plain = try Harness(hosts: [])
        let quiet = plain.sink.snapshot(plain.sink.advance().output)
        #expect(quiet.hosts.isEmpty && quiet.hostSources.isEmpty)
    }

    @Test func mirrorKeepsItsResponsesBounded() {
        var mirror = RemoteHostMirror(hostId: L.a)
        mirror.responseLimit = 20
        for index in 0..<30 {
            let record = L.record("k\(index)", host: L.a, start: L.now - 60_000 + Int64(index), end: L.now - 50_000 + Int64(index),
                                  output: 1)
            _ = mirror.apply(L.message(L.a, 1, L.response(record), seeded: true), nowMs: L.now, todayStartMs: L.day)
        }
        #expect(mirror.responses.count <= 20)
        #expect(mirror.responses["k29"] != nil && mirror.responses["k0"] == nil)
        let yesterday = L.record("y", host: L.a, start: L.day - 9_000_000, end: L.day - 8_000_000, output: 1)
        _ = mirror.apply(L.message(L.a, 1, L.response(yesterday), seeded: true), nowMs: L.now + 120_000, todayStartMs: L.day)
        #expect(mirror.responses["y"] == nil, "outside today and the retention window")
    }
}
