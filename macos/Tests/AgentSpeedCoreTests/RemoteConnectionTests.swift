import Foundation
import Testing
@testable import AgentSpeedCore

/// A connection with a scripted relay: the probe and upload answer by themselves; the run process is driven by the
/// test (or answers pings when `answersPings`).
final class RemoteConnectionHarness: @unchecked Sendable {
    static let hostId = "8C0D6B0A-1111-4222-8333-944455556666"

    let scheduling: ManualScheduling
    let transport = FakeTransport()
    let relays: FakeRelayBundle
    let events = RemoteEventLog()
    let connection: RemoteConnection
    private let offset = LockedValue<Int64>(0)

    /// The relay's clock offset to the manual clock, in milliseconds.
    var remoteOffsetMs: Int64 {
        get { offset.value }
        set { offset.value = newValue }
    }

    init(destination: String = "devbox", installed: Bool = true, answersPings: Bool = false,
         start: Date = Date(timeIntervalSince1970: 1_780_000_000), sources: Set<SourceKind> = [.codex, .claudeCode],
         quota: RemoteQuotaConfig? = nil,
         probe: (@Sendable (FakeProcess, FakeRelayBundle) -> Void)? = nil,
         upload: (@Sendable (FakeProcess, FakeRelayBundle) -> Void)? = nil) throws {
        scheduling = ManualScheduling(start: start)
        relays = try FakeRelayBundle()
        let environment = RemoteEnvironment(transport: transport, relays: relays.bundle, scheduling: scheduling,
                                            timeZone: { TimeZone(identifier: "UTC")! }, processEnvironment: ["HOME": "/Users/me"])
        let prices = RemotePriceTableJSON(Data(#"{"schema":3}"#.utf8))!
        connection = RemoteConnection(host: RemoteHost(id: Self.hostId, destination: destination),
                                      settings: RemoteSessionSettings(sources: sources, prices: prices, quota: quota),
                                      environment: environment, eventQueue: events.queue, onEvent: events.handler)
        let relays = relays
        let scheduling = scheduling
        let offset = offset
        transport.script = { process in
            if process.isProbe {
                if let probe { probe(process, relays) } else {
                    process.sendStdout(relays.probeOutput(installed: installed))
                    process.finish(0)
                }
            } else if process.isUpload {
                process.onInputClosed = { process in
                    if let upload { upload(process, relays) } else {
                        process.sendStdout("hash \(FileDigest.sha256(of: process.writtenData))\n")
                        process.finish(0)
                    }
                }
            } else if process.isRun, answersPings {
                process.onWrite = { process, data in
                    guard let line = try? JSONParser.parse(data.dropLast()), line["t"]?.string == "ping",
                          let id = line["id"]?.int64 else { return }
                    let remote = Clock.milliseconds(scheduling.now) + offset.value
                    process.send(RelayLines.clock(id: id, now: remote))
                }
            }
        }
    }

    /// Waits until the connection's queue and the event queue are idle: every callback hops through the queue once,
    /// and a whole probe → upload → run chain takes a few dozen hops.
    func settle() {
        for _ in 0..<64 {
            connection.sync {}
            events.queue.sync {}
        }
    }

    func start() {
        connection.start()
        settle()
    }

    func advance(_ seconds: TimeInterval) {
        settle()
        scheduling.advance(by: seconds)
        settle()
    }

    var run: FakeProcess? { transport.runs.last }

    /// Starts the connection and the relay's `hello`.
    @discardableResult
    func connect(ready: Bool = true) throws -> FakeProcess {
        start()
        let run = try #require(run)
        run.send(RelayLines.hello(now: Clock.milliseconds(scheduling.now) + remoteOffsetMs))
        if ready { run.send(RelayLines.ready) }
        settle()
        return run
    }

    var retryDelays: [TimeInterval] {
        events.events.compactMap { event in
            guard case .status(let status) = event.kind, let retryAt = status.retryAt, let failed = status.lastErrorAt else {
                return nil
            }
            return retryAt.timeIntervalSince(failed)
        }
    }

    var nowMs: Int64 { Clock.milliseconds(scheduling.now) }
}

@Suite("Remote connection")
struct RemoteConnectionTests {
    @Test func probeRunHelloSeedingReadyConnected() throws {
        let harness = try RemoteConnectionHarness()
        harness.remoteOffsetMs = 2_000
        harness.start()
        #expect(harness.events.statuses() == [.connecting])
        let launched = harness.transport.launched
        #expect(launched.count == 2)
        #expect(launched[0].isProbe && launched[1].isRun)
        let run = try #require(harness.run)
        #expect(run.request.executable == "/usr/bin/ssh")
        #expect(run.request.environment["LC_ALL"] == "C")
        #expect(run.request.arguments.suffix(3).dropLast() == ["--", "devbox"])
        #expect(run.remoteCommand.hasSuffix("agent-speed ~/.cache/agent-speed \(harness.relays.x86.installedName)"))
        #expect(run.writtenTypes == ["start"])
        let start = try #require(run.writtenLines.first)
        #expect(start["sources"]?.array?.compactMap(\.string) == ["codex", "claudeCode"])
        #expect(start["todayStartMs"]?.int64 == 1_779_926_400_000)
        #expect(start["zone"]?["id"]?.string == TimeZone(identifier: "UTC")?.identifier)
        #expect(start["prices"] == (try JSONParser.parse(#"{"schema":3}"#)))

        run.send(RelayLines.hello(now: harness.nowMs + 2_000))
        harness.settle()
        #expect(harness.events.lastStatus == RemoteHostStatus(status: .seeding(fraction: 0), clockOffsetMs: 2_000,
                                                               relayVersion: "1.7.0", relayArch: "x86_64"))
        #expect(harness.events.lastStatus?.isClockSkewed == false)
        #expect(run.writtenTypes == ["start", "ping"])

        run.send(#"{"t":"progress","value":0.5}"#)
        run.send(RelayLines.agent())
        run.send(RelayLines.response())
        harness.settle()
        #expect(harness.events.statuses().last == .seeding(fraction: 0.5))
        let seeded = harness.events.messages.filter { $0.1 }.map(\.0)
        guard seeded.count == 3, case .agent(let agent) = seeded[1], case .response(let response) = seeded[2] else {
            Issue.record("unexpected messages \(seeded)")
            return
        }
        #expect(agent.lastLineMs == RelayLines.baseMs + 500 - 2_000)
        #expect(response.record.endMs == RelayLines.baseMs + 2_000 - 2_000)

        run.send(RelayLines.clock(id: 1, now: harness.nowMs + 6_000))
        harness.settle()
        #expect(harness.events.lastStatus?.clockOffsetMs == 6_000)
        #expect(harness.events.lastStatus?.isClockSkewed == true)

        run.send(RelayLines.ready)
        run.send(RelayLines.response(RelayLines.responseObject(key: "live")))
        harness.settle()
        #expect(harness.events.statuses().last == .connected)
        #expect(harness.events.historyDueCount == 1)
        guard case .response(let live)? = harness.events.messages.last.map(\.0) else { return }
        #expect(live.record.key == "live")
        #expect(harness.events.messages.last?.1 == false)
        #expect(Set(harness.events.events.map(\.connection)).count == 1)
    }

    @Test func uploadsWhenTheRelayIsMissing() throws {
        let harness = try RemoteConnectionHarness(installed: false)
        harness.start()
        let launched = harness.transport.launched
        #expect(launched.map { [$0.isProbe, $0.isUpload, $0.isRun] } == [[true, false, false], [false, true, false], [false, false, true]])
        let upload = launched[1]
        #expect(upload.writtenData == (try Data(contentsOf: harness.relays.x86.url)))
        #expect(upload.isInputClosed)
        #expect(upload.remoteCommand.hasSuffix("\(harness.relays.x86.installedName) \(harness.relays.x86.sha256)"))
        let statuses = harness.events.statuses()
        #expect(statuses.first == .connecting)
        #expect(statuses.contains(.installing(fraction: 0)))
        #expect(statuses.contains(.installing(fraction: 1)))
    }

    @Test func uploadMismatchIsUploadFailed() throws {
        let harness = try RemoteConnectionHarness(installed: false, upload: { process, _ in
            process.sendStdout("hash \(String(repeating: "0", count: 64))\n")
            process.finish(0)
        })
        harness.start()
        #expect(harness.events.statuses().last == .uploadFailed)
        #expect(harness.transport.runs.isEmpty)
        #expect(harness.retryDelays == [300])

        let exit3 = try RemoteConnectionHarness(installed: false, upload: { process, _ in process.finish(3) })
        exit3.start()
        #expect(exit3.events.statuses().last == .uploadFailed)
    }

    @Test func probeVerdictsBecomeStatuses() throws {
        let unsupported = try RemoteConnectionHarness(probe: { process, _ in
            process.sendStdout("system Linux riscv64\ntool sha256sum\nend\n")
            process.finish(0)
        })
        unsupported.start()
        #expect(unsupported.events.statuses().last == .unsupportedSystem(system: "Linux riscv64"))

        let noTool = try RemoteConnectionHarness(probe: { process, _ in
            process.sendStdout("system Linux aarch64\ntool none\nend\n")
            process.finish(0)
        })
        noTool.start()
        #expect(noTool.events.statuses().last == .cannotVerify)

        let hostKey = try RemoteConnectionHarness(probe: { process, _ in
            process.sendStderr("Host key verification failed.\r\n")
            process.finish(255)
        })
        hostKey.start()
        #expect(hostKey.events.statuses().last == .hostKeyUnknown)
        #expect(hostKey.retryDelays == [300])
    }

    @Test func invalidDestinationAndMissingSSHLaunchNothing() throws {
        let invalid = try RemoteConnectionHarness(destination: "-oProxyCommand=x")
        invalid.start()
        #expect(invalid.events.statuses() == [.invalidDestination])
        #expect(invalid.transport.launched.isEmpty)
        #expect(invalid.scheduling.deadlines.isEmpty)

        let missing = try RemoteConnectionHarness()
        missing.transport.setAvailable(false)
        missing.start()
        #expect(missing.events.statuses() == [.sshMissing])
        #expect(missing.transport.launched.isEmpty)
        #expect(missing.retryDelays == [300])
    }

    @Test func silenceEndsTheConnectionAsUnreachable() throws {
        let harness = try RemoteConnectionHarness()
        let run = try harness.connect()
        harness.advance(149)
        #expect(harness.events.statuses().last == .connected)
        #expect(run.writtenTypes.filter { $0 == "ping" }.count == 5)
        harness.advance(1)
        #expect(run.isInputClosed && run.wasTerminated)
        #expect(harness.events.statuses().last == .unreachable)
        #expect(harness.retryDelays == [5])
        harness.advance(5)
        #expect(harness.transport.launched.count == 4)
        #expect(harness.transport.runs.count == 2)
    }

    @Test func answeredPingsKeepItAlive() throws {
        let harness = try RemoteConnectionHarness(answersPings: true)
        let run = try harness.connect()
        harness.advance(600)
        #expect(harness.events.statuses().last == .connected)
        #expect(!run.wasTerminated)
        #expect(run.writtenTypes.filter { $0 == "ping" }.count == 21)
    }

    @Test func backoffSequenceAndReset() throws {
        let harness = try RemoteConnectionHarness(probe: { process, _ in
            process.sendStderr("ssh: connect to host devbox port 22: Connection refused\n")
            process.finish(255)
        })
        harness.start()
        for delay: TimeInterval in [5, 10, 20, 40, 60, 60] { harness.advance(delay) }
        #expect(harness.retryDelays == [5, 10, 20, 40, 60, 60, 60])
        #expect(harness.events.statuses().allSatisfy { $0 == .connecting || $0 == .unreachable })
    }

    @Test func backoffResetsAfterSixtySecondsUp() throws {
        let failing = LockedValue(false)
        let harness = try RemoteConnectionHarness(answersPings: true, probe: { process, relays in
            if failing.value {
                process.sendStderr("Connection closed by 10.0.0.1 port 22\n")
                process.finish(255)
            } else {
                process.sendStdout(relays.probeOutput(installed: true))
                process.finish(0)
            }
        })
        failing.value = true
        harness.start()
        harness.advance(5)
        #expect(harness.retryDelays == [5, 10])
        failing.value = false
        harness.advance(10)
        let first = try #require(harness.run)
        first.send(RelayLines.hello(now: harness.nowMs))
        harness.settle()
        harness.advance(30)
        first.sendStderr("Connection closed by 10.0.0.1 port 22\n")
        first.finish(255)
        harness.settle()
        #expect(harness.retryDelays == [5, 10, 20])
        harness.advance(20)
        let second = try #require(harness.run)
        #expect(second !== first)
        second.send(RelayLines.hello(now: harness.nowMs))
        harness.settle()
        harness.advance(61)
        second.finish(255)
        harness.settle()
        #expect(harness.retryDelays == [5, 10, 20, 5])
    }

    @Test func userActionableErrorsRetryEveryFiveMinutesOrOnRetry() throws {
        let harness = try RemoteConnectionHarness(probe: { process, _ in
            process.sendStderr("devbox: Permission denied (publickey).\n")
            process.finish(255)
        })
        harness.start()
        #expect(harness.events.statuses() == [.connecting, .authFailed])
        #expect(harness.retryDelays == [300])
        harness.connection.noteWake()
        harness.advance(299)
        #expect(harness.transport.launched.count == 1)
        harness.advance(1)
        #expect(harness.transport.launched.count == 2)
        harness.connection.retryNow()
        harness.settle()
        #expect(harness.transport.launched.count == 3)
    }

    @Test func wakeRetriesAWaitingHostAtOnce() throws {
        let harness = try RemoteConnectionHarness(probe: { process, _ in
            process.sendStderr("ssh: Could not resolve hostname devbox\n")
            process.finish(255)
        })
        harness.start()
        harness.advance(5)
        harness.advance(10)
        #expect(harness.transport.launched.count == 3)
        harness.connection.noteWake()
        harness.settle()
        #expect(harness.transport.launched.count == 4)
    }

    @Test func stopClosesInputTerminatesAndKillsAfterTwoSeconds() throws {
        let harness = try RemoteConnectionHarness()
        let run = try harness.connect()
        run.exitOnTerminate = nil
        harness.connection.stop(finalStatus: .disabled)
        harness.settle()
        #expect(run.isInputClosed)
        #expect(run.wasTerminated)
        #expect(!run.wasKilled)
        #expect(harness.events.statuses().last == .disabled)
        harness.advance(2)
        #expect(run.wasKilled)
        harness.advance(600)
        #expect(harness.events.statuses().last == .disabled)
        #expect(harness.transport.launched.count == 2)
        harness.connection.retryNow()
        harness.connection.start()
        harness.settle()
        #expect(harness.transport.launched.count == 2)
    }

    @Test func moreThan100InvalidLinesIsAProtocolError() throws {
        let harness = try RemoteConnectionHarness()
        let run = try harness.connect()
        for index in 0..<100 { run.send(index.isMultiple(of: 2) ? "garbage" : #"{"t":"agent","a":{}}"#) }
        harness.settle()
        #expect(!run.wasTerminated)
        run.send("[]")
        harness.settle()
        #expect(run.wasTerminated)
        #expect(harness.events.statuses().last == .protocolError)
        #expect(harness.retryDelays == [5])
    }

    @Test func linesBeforeHelloAreInvalidAndAWrongProtocolIsAMismatch() throws {
        let harness = try RemoteConnectionHarness()
        harness.start()
        let run = try #require(harness.run)
        run.send(RelayLines.ready)
        run.send(RelayLines.hello(protocolVersion: 2))
        harness.settle()
        #expect(harness.events.messages.isEmpty)
        #expect(run.wasTerminated)
        #expect(harness.events.statuses().last == .versionMismatch(relayProtocol: 2))
        #expect(harness.retryDelays == [300])
    }

    @Test func timesThatLeaveTheRangeAfterConversionAreInvalid() throws {
        let harness = try RemoteConnectionHarness(start: Date(timeIntervalSince1970: 1_780_000_000))
        harness.start()
        let run = try #require(harness.run)
        run.send(RelayLines.hello(now: harness.nowMs + 10_000))
        var object = RelayLines.responseObject()
        object["startMs"] = RemoteFields.earliestMs + 5_000
        object["firstMs"] = NSNull()
        run.send(RelayLines.response(object))
        run.send(RelayLines.response())
        harness.settle()
        let responses = harness.events.messages.compactMap { message -> RemoteResponse? in
            if case .response(let response) = message.0 { return response }
            return nil
        }
        #expect(responses.count == 1)
        #expect(responses.first?.record.startMs == RelayLines.baseMs - 10_000)
    }

    @Test func atMost4096AgentsPerHost() throws {
        let harness = try RemoteConnectionHarness()
        let run = try harness.connect()
        harness.events.clear()
        let text = (0...RemoteLimits.agents).map { RelayLines.agent(RelayLines.agentObject(id: "/a/\($0)")) + "\n" }.joined()
        run.sendStdout(text)
        run.send(RelayLines.agent(RelayLines.agentObject(id: "/a/1")))
        run.send(#"{"t":"agentRemoved","id":"/a/0"}"#)
        run.send(RelayLines.agent(RelayLines.agentObject(id: "/a/new")))
        harness.settle()
        let agents = harness.events.messages.compactMap { message -> String? in
            if case .agent(let agent) = message.0 { return agent.relayId }
            return nil
        }
        #expect(agents.count == RemoteLimits.agents + 2)
        #expect(!agents.contains("/a/4096"))
        #expect(agents.last == "/a/new")
    }

    @Test func historyScheduleAfterReadyEveryTenMinutesAndAtMidnight() throws {
        let midnight = Date(timeIntervalSince1970: 1_780_012_800)
        let harness = try RemoteConnectionHarness(answersPings: true, start: midnight.addingTimeInterval(-300))
        let run = try harness.connect()
        #expect(harness.events.historyDueCount == 1)
        harness.connection.requestHistory(digests: ["2026-05-28/codex": "0123456789abcdef"])
        harness.settle()
        #expect(run.writtenTypes.last == "history")
        #expect(run.writtenLines.last?["digests"]?["2026-05-28/codex"]?.string == "0123456789abcdef")

        harness.advance(300)
        #expect(run.writtenTypes.contains("today"))
        let today = run.writtenLines.last { $0["t"]?.string == "today" }
        #expect(today?["todayStartMs"]?.int64 == Clock.milliseconds(midnight))
        #expect(harness.events.historyDueCount == 1)
        run.send(#"{"t":"historyDone","days":2,"analyzed":5}"#)
        harness.settle()
        #expect(harness.events.historyDueCount == 2)

        harness.advance(300)
        #expect(harness.events.historyDueCount == 3)
        harness.advance(600)
        #expect(harness.events.historyDueCount == 4)
    }

    @Test func aFailedHistoryRunLetsTheNextSyncGo() throws {
        let harness = try RemoteConnectionHarness(answersPings: true)
        let run = try harness.connect()
        #expect(harness.events.historyDueCount == 1)
        harness.connection.requestHistory(digests: [:])
        harness.settle()
        harness.advance(600)
        #expect(harness.events.historyDueCount == 1)
        run.send(#"{"t":"error","code":"historyFailed","detail":"disk full"}"#)
        harness.settle()
        #expect(harness.events.historyDueCount == 2)
        #expect(!run.isInputClosed)

        harness.connection.requestHistory(digests: [:])
        harness.settle()
        run.send(#"{"t":"error","code":"historyFailed","detail":"disk full"}"#)
        harness.settle()
        harness.advance(600)
        #expect(harness.events.historyDueCount == 3)
    }

    @Test func historyRequestsBeforeReadyAreIgnored() throws {
        let harness = try RemoteConnectionHarness()
        let run = try harness.connect(ready: false)
        harness.connection.requestHistory(digests: [:])
        harness.settle()
        #expect(!run.writtenTypes.contains("history"))
        #expect(harness.events.historyDueCount == 0)
    }

    @Test func pricesWatchAndZoneGoToTheRunningRelay() throws {
        let harness = try RemoteConnectionHarness()
        let run = try harness.connect()
        harness.connection.updatePrices(RemotePriceTableJSON(Data(#"{"schema":4}"#.utf8))!)
        harness.connection.setWatch(true)
        harness.connection.setWatch(true)
        harness.connection.noteTimeZoneChange()
        harness.settle()
        #expect(run.writtenTypes == ["start", "ping", "prices", "watch", "zone", "today"])
        #expect(run.writtenLines[2]["table"]?["schema"]?.int64 == 4)
    }

    @Test func reconnectStartsAFreshConnectionNumber() throws {
        let harness = try RemoteConnectionHarness()
        let run = try harness.connect()
        run.sendStderr("Connection closed by remote host\n")
        run.finish(255)
        harness.advance(5)
        let second = try #require(harness.run)
        #expect(second !== run)
        let numbers = harness.events.events.map(\.connection)
        #expect(Set(numbers).count == 2)
        #expect(numbers == numbers.sorted())
    }
}

/// A value the scripted transport reads from another thread.
final class LockedValue<Value: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Value

    init(_ value: Value) { stored = value }

    var value: Value {
        get { lock.withLock { stored } }
        set { lock.withLock { stored = newValue } }
    }
}
