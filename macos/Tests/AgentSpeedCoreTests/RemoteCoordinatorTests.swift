import Foundation
import Testing
@testable import AgentSpeedCore

/// The live monitor with a remote host over the scripted transport (REMOTE 7): the host's agents and responses reach
/// the snapshot, survive a monitor restart without a reconnect, and history events reach the sink.
@MainActor
@Suite("Remote coordinator", .serialized)
struct RemoteCoordinatorTests {
    static let hostId = "8C0D6B0A-1111-4222-8333-0000000000C1"

    /// Answers `start` with a seeding of one agent and one response, then `ready` and a source report.
    final class Relay: @unchecked Sendable {
        let transport = FakeTransport()
        let relays: FakeRelayBundle
        private let started = LockedValue<[Set<String>]>([])

        init() throws {
            relays = try FakeRelayBundle()
            let relays = relays
            let started = started
            transport.script = { process in
                if process.isProbe {
                    process.sendStdout(relays.probeOutput(installed: true))
                    process.finish(0)
                } else if process.isRun {
                    process.onWrite = { process, data in
                        for text in String(decoding: data, as: UTF8.self).split(separator: "\n") {
                            guard let line = try? JSONParser.parse(String(text)), line["t"]?.string == "start" else { continue }
                            let sources = Set((line["sources"]?.array ?? []).compactMap(\.string))
                            started.value.append(sources)
                            Relay.seed(process)
                        }
                    }
                }
            }
        }

        var starts: [Set<String>] { started.value }

        static func seed(_ process: FakeProcess) {
            let now = Clock.milliseconds(Date())
            process.send(RelayLines.hello(now: now))
            var agent = RelayLines.agentObject()
            agent["phase"] = "idle"
            agent["inFlightStartMs"] = NSNull()
            agent["lastLineMs"] = now - 1000
            agent["actions"] = [] as [Any]
            process.send(RelayLines.agent(agent))
            var response = RelayLines.responseObject()
            response["startMs"] = now - 60_000
            response["endMs"] = now - 50_000
            response["firstMs"] = now - 59_000
            process.send(RelayLines.response(response))
            process.send(RelayLines.ready)
            process.send(RelayLines.json(["t": "status", "sources": [
                ["source": "codex", "available": true, "sessions": 1, "unsupported": false, "configuredModes": [] as [Any]],
            ], "malformed": 0, "unreadable": 0, "partialHistory": false]))
        }
    }

    final class HistoryLog: RemoteHistorySink, @unchecked Sendable {
        private let lock = NSLock()
        private var events: [RemoteHostEvent] = []

        func remoteHistory(_ event: RemoteHostEvent, requests: RemoteHistoryRequests) {
            lock.withLock { events.append(event) }
            if case .historyDue = event.kind { requests.requestHistory(hostId: event.hostId, digests: ["2026-11-04/codex": "0"]) }
        }

        var kinds: [String] {
            lock.withLock {
                events.map { event in
                    switch event.kind {
                    case .status(let status): "status:\(status.status.code)"
                    case .historyDue: "historyDue"
                    case .message(let message, _): "message:\(message)"
                    }
                }
            }
        }
    }

    @Test func aHostReachesTheSnapshotAndSurvivesARestart() async throws {
        let relay = try Relay()
        let directory = try TempDirectory()
        let configuration = MonitorConfiguration(enabledSources: [.codex, .claudeCode], codexRoot: directory.file("missing-codex"),
                                                 claudeRoot: directory.file("missing-claude"))
        let store = SpeedStore()
        let monitor = LiveMonitor(store: store, configuration: configuration)
        let environment = RemoteEnvironment(transport: relay.transport, relays: relay.relays.bundle,
                                            processEnvironment: ["HOME": "/Users/me",
                                                                 RemoteSSH.configFileVariable: "/tmp/agent-speed-test/ssh_config"])
        let coordinator = RemoteCoordinator(monitor: monitor, environment: environment)
        let history = HistoryLog()
        coordinator.historySink = history
        coordinator.update(hosts: RemoteHostsDocument(hosts: [RemoteHost(id: Self.hostId, destination: "devbox")]),
                           networkEnabled: true, priceTable: Data(BundledPriceAssets.prices.utf8))
        monitor.start()
        defer {
            coordinator.stop()
            monitor.stop()
            withExtendedLifetime(directory) {}
        }

        let agentId = "ssh://\(Self.hostId)//home/u/.codex/sessions/a.jsonl"
        func showsTheHost() -> Bool {
            let snapshot = store.snapshot
            return snapshot.state == .running && snapshot.today.output == 340
                && snapshot.hosts.map(\.id) == [RemoteHost.localId, Self.hostId]
                && snapshot.hosts.last?.isConnected == true
                && snapshot.agents.contains { $0.id == agentId && $0.host == Self.hostId }
        }
        #expect(await LiveMonitorTests.waitUntil(showsTheHost), "\(store.snapshot.state) \(store.snapshot.hosts)")
        #expect(store.snapshot.hosts.last?.name == "devbox")
        #expect(store.snapshot.sources.first { $0.kind == .codex }?.available == true, "detected on the host")
        #expect(relay.starts == [["codex", "claudeCode"]])
        let run = try #require(relay.transport.runs.first)
        let arguments = run.request.arguments
        #expect(arguments.firstIndex(of: "-F").map { arguments[$0 + 1] } == "/tmp/agent-speed-test/ssh_config")
        #expect(arguments.firstIndex(of: "-F")! < arguments.firstIndex(of: "--")!)

        // History events go to the sink, which answers through the hub.
        #expect(await LiveMonitorTests.waitUntil { run.writtenTypes.contains("history") })
        #expect(history.kinds.contains("historyDue"))
        #expect(history.kinds.contains("status:connected"))
        run.send(#"{"t":"historyDone","days":0,"analyzed":3}"#)
        #expect(await LiveMonitorTests.waitUntil { history.kinds.contains { $0.contains("historyDone") } })

        // A restart that reads local files again keeps the host's state without a new connection.
        var moved = configuration
        moved.setLocation(directory.file("other-codex"), for: .codex)
        monitor.update(configuration: moved)
        #expect(await LiveMonitorTests.waitUntil(showsTheHost))
        #expect(relay.transport.runs.count == 1)

        // The report watch reaches the relay.
        monitor.setReportWatch(1)
        #expect(await LiveMonitorTests.waitUntil { run.writtenTypes.contains("watch") })

        // Changing the enabled sources reconnects with the new set.
        var reduced = moved
        reduced.enabledSources = [.codex]
        monitor.update(configuration: reduced)
        #expect(await LiveMonitorTests.waitUntil { relay.starts.count == 2 })
        #expect(relay.starts.last == ["codex"])
        #expect(await LiveMonitorTests.waitUntil(showsTheHost))
    }

    @Test func theDevelopmentHookIsAnAbsolutePathOnly() {
        #expect(RemoteSSH.developmentConfigFile([:]) == nil)
        #expect(RemoteSSH.developmentConfigFile([RemoteSSH.configFileVariable: "relative/ssh_config"]) == nil)
        #expect(RemoteSSH.developmentConfigFile([RemoteSSH.configFileVariable: "/tmp/ssh_config"]) == "/tmp/ssh_config")
        let plain = RemoteSSH.arguments(destination: "devbox", remoteCommand: "true")
        #expect(plain?.contains("-F") == false)
        let hooked = RemoteSSH.arguments(destination: "devbox", remoteCommand: "true", configFile: "/tmp/ssh_config")
        #expect(hooked?.suffix(5) == ["-F", "/tmp/ssh_config", "--", "devbox", "true"])
    }
}
