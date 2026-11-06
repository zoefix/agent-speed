import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Remote hub")
struct RemoteHubTests {
    static let a = "8C0D6B0A-1111-4222-8333-00000000000A"
    static let b = "8C0D6B0A-1111-4222-8333-00000000000B"
    static let prices = Data(#"{"schema":3}"#.utf8)

    final class Harness: @unchecked Sendable {
        let scheduling = ManualScheduling()
        let transport = FakeTransport()
        let relays: FakeRelayBundle
        let events = RemoteEventLog()
        let hub: RemoteHub

        init() throws {
            relays = try FakeRelayBundle()
            hub = RemoteHub(environment: RemoteEnvironment(transport: transport, relays: relays.bundle, scheduling: scheduling,
                                                           timeZone: { TimeZone(identifier: "UTC")! }),
                            eventQueue: events.queue, onEvent: events.handler)
            let relays = relays
            transport.script = { process in
                if process.isProbe {
                    if process.request.arguments.contains("unreachable") {
                        process.sendStderr("ssh: Could not resolve hostname unreachable\n")
                        process.finish(255)
                    } else {
                        process.sendStdout(relays.probeOutput(installed: true))
                        process.finish(0)
                    }
                }
            }
        }

        func update(_ hosts: [RemoteHost], network: Bool = true, sources: Set<SourceKind> = [.codex],
                    prices: Data = RemoteHubTests.prices) {
            hub.update(hosts: hosts, networkEnabled: network, enabledSources: sources, priceTable: prices)
            settle()
        }

        func settle() {
            for _ in 0..<64 {
                for id in hub.connectedHostIds { hub.connection(id)?.sync {} }
                events.queue.sync {}
            }
        }

        func runs(_ destination: String) -> [FakeProcess] {
            transport.runs.filter { $0.request.arguments.contains(destination) }
        }
    }

    @Test func startsEnabledHostsAndKeepsThemOnAnIdenticalUpdate() throws {
        let harness = try Harness()
        let hosts = [RemoteHost(id: Self.a, destination: "alpha"), RemoteHost(id: Self.b, destination: "beta")]
        harness.update(hosts)
        #expect(harness.hub.connectedHostIds == [Self.a, Self.b])
        #expect(harness.runs("alpha").count == 1 && harness.runs("beta").count == 1)
        let first = harness.hub.connection(Self.a)
        harness.update(hosts)
        #expect(harness.hub.connection(Self.a) === first)
        #expect(harness.transport.runs.count == 2)
    }

    @Test func disablingRemovingAndRenaming() throws {
        let harness = try Harness()
        var alpha = RemoteHost(id: Self.a, destination: "alpha")
        let beta = RemoteHost(id: Self.b, destination: "beta")
        harness.update([alpha, beta])
        let alphaRun = try #require(harness.runs("alpha").first)
        let betaRun = try #require(harness.runs("beta").first)
        let connection = harness.hub.connection(Self.a)

        alpha.name = "Renamed"
        harness.update([alpha, beta])
        #expect(harness.hub.connection(Self.a) === connection)

        alpha.enabled = false
        harness.update([alpha, beta])
        #expect(alphaRun.isInputClosed && alphaRun.wasTerminated)
        #expect(harness.events.statuses(Self.a).last == .disabled)
        #expect(harness.hub.connectedHostIds == [Self.b])

        harness.update([alpha])
        #expect(betaRun.isInputClosed && betaRun.wasTerminated)
        #expect(harness.hub.connectedHostIds.isEmpty)
        #expect(harness.events.statuses(Self.b).last == .connecting)
        #expect(harness.events.statuses(Self.a).filter { $0 == .disabled }.count == 1)
    }

    @Test func editingTheDestinationOrSourcesReconnects() throws {
        let harness = try Harness()
        var alpha = RemoteHost(id: Self.a, destination: "alpha")
        harness.update([alpha])
        let first = try #require(harness.transport.runs.last)

        alpha.destination = "alpha2"
        harness.update([alpha])
        #expect(first.wasTerminated)
        #expect(harness.runs("alpha2").count == 1)

        alpha.relayFolder = "/opt/agent-speed"
        harness.update([alpha])
        let third = try #require(harness.transport.runs.last)
        #expect(third.remoteCommand.contains(" /opt/agent-speed "))

        harness.update([alpha], sources: [.codex, .gemini])
        let fourth = try #require(harness.transport.runs.last)
        #expect(third.wasTerminated && fourth !== third)
        #expect(fourth.writtenLines.first?["sources"]?.array?.compactMap(\.string) == ["codex", "gemini"])
    }

    @Test func masterSwitchOffMeansNoSSH() throws {
        let harness = try Harness()
        let hosts = [RemoteHost(id: Self.a, destination: "alpha"), RemoteHost(id: Self.b, destination: "beta", enabled: false)]
        harness.update(hosts, network: false)
        #expect(harness.transport.launched.isEmpty)
        #expect(harness.events.statuses(Self.a) == [.networkOff])
        #expect(harness.events.statuses(Self.b) == [.networkOff])

        harness.update(hosts, network: true)
        #expect(harness.events.statuses(Self.b) == [.networkOff, .disabled])
        #expect(harness.runs("alpha").count == 1)
        let run = try #require(harness.runs("alpha").first)

        harness.update(hosts, network: false)
        #expect(run.wasTerminated)
        #expect(harness.events.statuses(Self.a).last == .networkOff)
        #expect(harness.transport.launched.count == 2)
    }

    @Test func pricesGoToRunningRelaysWithoutReconnecting() throws {
        let harness = try Harness()
        let hosts = [RemoteHost(id: Self.a, destination: "alpha")]
        harness.update(hosts)
        let run = try #require(harness.transport.runs.last)
        harness.update(hosts, prices: Data(#"{"schema":4}"#.utf8))
        harness.update(hosts, prices: Data("not json".utf8))
        #expect(harness.transport.runs.count == 1)
        #expect(run.writtenTypes == ["start", "prices"])
        #expect(run.writtenLines.last?["table"]?["schema"]?.int64 == 4)

        harness.hub.setWatch(true)
        harness.settle()
        #expect(run.writtenTypes.last == "watch")
    }

    @Test func invalidHostsGetAStatusAndNoConnection() throws {
        let harness = try Harness()
        harness.update([RemoteHost(id: Self.a, destination: "-oProxyCommand=x"),
                        RemoteHost(id: Self.b, destination: "beta", relayFolder: "~/../x"),
                        RemoteHost(id: "local", destination: "gamma")])
        #expect(harness.transport.launched.isEmpty)
        #expect(harness.events.statuses(Self.a) == [.invalidDestination])
        #expect(harness.events.statuses(Self.b) == [.invalidDestination])
        #expect(harness.events.statuses("local").isEmpty)
    }

    @Test func wakeRetriesWaitingHostsAndHistoryRoutesToTheHost() throws {
        let harness = try Harness()
        harness.update([RemoteHost(id: Self.a, destination: "unreachable"), RemoteHost(id: Self.b, destination: "beta")])
        #expect(harness.events.statuses(Self.a).last == .unreachable)
        #expect(harness.transport.launched.filter { $0.request.arguments.contains("unreachable") }.count == 1)
        harness.hub.noteWake()
        harness.settle()
        #expect(harness.transport.launched.filter { $0.request.arguments.contains("unreachable") }.count == 2)

        let run = try #require(harness.runs("beta").first)
        run.send(RelayLines.hello())
        run.send(RelayLines.ready)
        harness.settle()
        #expect(harness.events.historyDueCount(Self.b) == 1)
        harness.hub.requestHistory(hostId: Self.b, digests: ["2026-11-03/codex": "0123456789abcdef"])
        harness.settle()
        #expect(run.writtenTypes.last == "history")

        harness.hub.stopAll()
        harness.settle()
        #expect(run.wasTerminated)
        #expect(harness.hub.connectedHostIds.isEmpty)
    }
}
