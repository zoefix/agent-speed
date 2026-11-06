import Foundation
import Testing
@testable import AgentSpeedCore

/// DESIGN 13, *Test Connection*: one probe (REMOTE 4.2 step 1) over the user's `ssh`, its verdict or the status a
/// connection would show, never an upload or a relay run.
@Suite("Remote test connection")
struct RemoteConnectionTestTests {
    private final class Outcomes: @unchecked Sendable {
        private let lock = NSLock()
        private var values: [RemoteTestOutcome] = []

        func append(_ value: RemoteTestOutcome) { lock.withLock { values.append(value) } }
        var all: [RemoteTestOutcome] { lock.withLock { values } }
    }

    private static func run(_ host: RemoteHost = RemoteHost(destination: "devbox"), ssh: Bool = true,
                            script: @escaping @Sendable (FakeProcess) -> Void)
        -> (outcomes: [RemoteTestOutcome], transport: FakeTransport) {
        let transport = FakeTransport()
        transport.setAvailable(ssh)
        transport.script = script
        let scheduling = ManualScheduling()
        let queue = DispatchQueue(label: "test.remote.probe")
        let outcomes = Outcomes()
        let environment = RemoteEnvironment(transport: transport, scheduling: scheduling, processEnvironment: [:])
        RemoteConnectionTest.start(host, environment: environment, queue: queue) { outcomes.append($0) }
        queue.sync {}
        queue.sync {}
        return (outcomes.all, transport)
    }

    @Test func aSupportedHostReportsItsSystemAndArchitecture() {
        let (outcomes, transport) = Self.run { process in
            process.sendStdout("system Linux aarch64\ntool sha256sum\nend\n")
            process.finish(0)
        }
        #expect(outcomes == [.supported(system: "Linux aarch64", arch: .aarch64)])
        #expect(transport.launched.count == 1)
        let probesOnly = transport.launched.allSatisfy { $0.isProbe }
        #expect(probesOnly)
        #expect(transport.launched[0].request.arguments.suffix(2).first == "devbox")
        #expect(transport.launched[0].isInputClosed)
    }

    @Test func probeFailuresReadAsTheConnectionsStatuses() {
        let hostKey = Self.run { process in
            process.sendStderr("Host key verification failed.\r\n")
            process.finish(255)
        }
        #expect(hostKey.outcomes == [.failed(.hostKeyUnknown)])

        let auth = Self.run { process in
            process.sendStderr("devbox: Permission denied (publickey).\r\n")
            process.finish(255)
        }
        #expect(auth.outcomes == [.failed(.authFailed)])

        let unsupported = Self.run { process in
            process.sendStdout("system Darwin arm64\ntool shasum -a 256\nend\n")
            process.finish(0)
        }
        #expect(unsupported.outcomes == [.failed(.unsupportedSystem(system: "Darwin arm64"))])

        let noTool = Self.run { process in
            process.sendStdout("system Linux x86_64\ntool none\nend\n")
            process.finish(0)
        }
        #expect(noTool.outcomes == [.failed(.cannotVerify)])

        let banner = Self.run { process in
            process.sendStdout("Welcome to devbox\n")
            process.finish(0)
        }
        #expect(banner.outcomes == [.failed(.exited(status: 0))])
    }

    @Test func anInvalidHostOrAMissingSshNeverLaunches() {
        let invalid = Self.run(RemoteHost(destination: "dev box")) { _ in }
        #expect(invalid.outcomes == [.failed(.invalidDestination)])
        #expect(invalid.transport.launched.isEmpty)

        let folder = Self.run(RemoteHost(destination: "devbox", relayFolder: "~/../x")) { _ in }
        #expect(folder.outcomes == [.failed(.invalidDestination)])

        let missing = Self.run(ssh: false) { _ in }
        #expect(missing.outcomes == [.failed(.sshMissing)])
        #expect(missing.transport.launched.isEmpty)
    }

    @Test func cancellingEndsTheProbeAndDropsItsResult() {
        let transport = FakeTransport()
        transport.script = { _ in }
        let queue = DispatchQueue(label: "test.remote.probe.cancel")
        let outcomes = Outcomes()
        let environment = RemoteEnvironment(transport: transport, scheduling: ManualScheduling(), processEnvironment: [:])
        let test = RemoteConnectionTest.start(RemoteHost(destination: "devbox"), environment: environment, queue: queue) {
            outcomes.append($0)
        }
        queue.sync {}
        test.cancel()
        queue.sync {}
        queue.sync {}
        #expect(transport.launched.first?.wasTerminated == true)
        #expect(outcomes.all.isEmpty)
    }

    @Test func aHostThatNeverAnswersTimesOutAsUnreachable() {
        let transport = FakeTransport()
        transport.script = { _ in }
        let scheduling = ManualScheduling()
        let queue = DispatchQueue(label: "test.remote.probe.timeout")
        let outcomes = Outcomes()
        let environment = RemoteEnvironment(transport: transport, scheduling: scheduling, processEnvironment: [:])
        RemoteConnectionTest.start(RemoteHost(destination: "devbox"), environment: environment, queue: queue) {
            outcomes.append($0)
        }
        queue.sync {}
        scheduling.advance(by: RemoteConnectionTest.timeLimit + 1)
        queue.sync {}
        queue.sync {}
        #expect(outcomes.all == [.failed(.unreachable)])
    }
}
