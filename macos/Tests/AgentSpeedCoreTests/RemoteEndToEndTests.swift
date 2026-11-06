import Foundation
import Testing
@testable import AgentSpeedCore

/// The real transport (`Process` with pipes), the real remote scripts and a relay stand-in, with a local `ssh`
/// stand-in that runs the remote command with `/bin/sh -c` the way a login shell would.
@Suite("Remote end to end", .serialized)
struct RemoteEndToEndTests {
    /// Runs the request with another executable (the `ssh` stand-in).
    struct RedirectingTransport: RemoteTransport {
        let ssh: String
        let base = ProcessRemoteTransport()

        func isAvailable(_ executable: String) -> Bool { true }

        func launch(_ request: RemoteProcessRequest, handlers: RemoteProcessHandlers) throws -> any RemoteProcess {
            var request = request
            request.executable = ssh
            return try base.launch(request, handlers: handlers)
        }
    }

    static let fakeSSH = """
    #!/bin/sh
    while [ $# -gt 0 ]; do if [ "$1" = "--" ]; then shift; break; fi; shift; done
    shift
    exec /bin/sh -c "$1"

    """

    /// Answers `start` with `hello` and `ready`, every `ping` with `clock`, and ends at end of input.
    static let relay = """
    #!/bin/sh
    [ "$1" = serve ] && [ "$2" = --folder ] || exit 2
    read start
    now=$(date +%s)000
    echo '{"t":"hello","protocol":1,"version":"1.7.0","arch":"x86_64","now":'"$now"'}'
    echo '{"t":"ready"}'
    while read line; do
      case "$line" in
        *'"ping"'*) id=${line#*'"id":'}; id=${id%%[!0-9]*}; echo '{"t":"clock","id":'"$id"',"now":'"$(date +%s)000"'}' ;;
      esac
    done
    exit 0

    """

    /// Polls without holding a thread of the shared pool, so other tests' async work keeps running meanwhile.
    static func waitUntil(_ condition: () -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(20)
        while Date() < deadline {
            if condition() { return true }
            try? await Task.sleep(for: .milliseconds(20))
        }
        return condition()
    }

    @Test func installsRunsAndStopsTheRelay() async throws {
        let root = try TempDirectory()
        let home = root.file("home")
        let stubs = root.file("stubs")
        let relays = root.file("relays")
        for folder in [home, stubs, relays] { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
        try root.write("stubs/uname", "#!/bin/sh\necho Linux x86_64\n")
        try root.write("ssh", Self.fakeSSH)
        try root.write("relays/agent-speed-relay-linux-x86_64", Self.relay)
        for path in ["stubs/uname", "ssh"] {
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: root.file(path).path)
        }
        let bundle = RemoteRelayBundle(directory: relays, version: "1.7.0")
        let executable = try #require(bundle.executable(for: .x86_64))
        let environment = RemoteEnvironment(
            transport: RedirectingTransport(ssh: root.file("ssh").path), relays: bundle,
            processEnvironment: ["HOME": home.path, "PATH": "\(stubs.path):/usr/bin:/bin:/usr/sbin:/sbin"]
        )
        let prices = try #require(RemotePriceTableJSON(Data(BundledPriceAssets.prices.utf8)))
        let settings = RemoteSessionSettings(sources: Set(SourceKind.allCases), prices: prices)

        for round in 0..<2 {
            let events = RemoteEventLog()
            let connection = RemoteConnection(host: RemoteHost(destination: "devbox"), settings: settings,
                                              environment: environment, eventQueue: events.queue, onEvent: events.handler)
            connection.start()
            #expect(await Self.waitUntil { events.statuses().contains(.connected) }, "round \(round): \(events.statuses())")
            let installing = events.statuses().contains { if case .installing = $0 { true } else { false } }
            #expect(installing == (round == 0))
            #expect(events.messages.map(\.0).contains(.ready))
            #expect(await Self.waitUntil { events.lastStatus?.clockOffsetMs.map { abs($0) < 2_000 } == true })
            connection.stop(finalStatus: .disabled)
            #expect(await Self.waitUntil { events.statuses().last == .disabled })
        }

        let folder = home.appendingPathComponent(".cache/agent-speed")
        let installed = folder.appendingPathComponent(executable.installedName)
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == [executable.installedName])
        #expect(try Data(contentsOf: installed) == Data(Self.relay.utf8))
        #expect(try FileManager.default.attributesOfItem(atPath: installed.path)[.posixPermissions] as? Int == 0o700)
        #expect(try FileManager.default.attributesOfItem(atPath: folder.path)[.posixPermissions] as? Int == 0o700)
    }

    @Test func pipedProcessDeliversOutputBeforeExitAndSurvivesAClosedPipe() async throws {
        let output = LockedValue(Data())
        let errors = LockedValue(Data())
        let status = LockedValue<Int32?>(nil)
        let outputAtExit = LockedValue(Data())
        let handlers = RemoteProcessHandlers(
            stdout: { data in output.value.append(data) },
            stderr: { data in errors.value.append(data) },
            exit: { code in
                outputAtExit.value = output.value
                status.value = code
            }
        )
        let process = try ProcessRemoteTransport().launch(
            RemoteProcessRequest(executable: "/bin/sh", arguments: ["-c", "read a; echo \"got $a\"; echo oops >&2; exit 7"],
                                 environment: [:]),
            handlers: handlers
        )
        process.write(Data("hello\n".utf8), completion: nil)
        #expect(await Self.waitUntil { status.value != nil })
        #expect(status.value == 7)
        #expect(String(decoding: outputAtExit.value, as: UTF8.self) == "got hello\n")
        #expect(String(decoding: errors.value, as: UTF8.self) == "oops\n")

        let written = LockedValue<Bool?>(nil)
        process.write(Data(repeating: 0x41, count: 1 << 20)) { ok in written.value = ok }
        #expect(await Self.waitUntil { written.value != nil })
        #expect(written.value == false)
        process.closeInput()
        process.terminate()
        process.kill()
    }

    @Test func terminateEndsALongRunningProcess() async throws {
        let status = LockedValue<Int32?>(nil)
        let process = try ProcessRemoteTransport().launch(
            RemoteProcessRequest(executable: "/bin/sleep", arguments: ["30"], environment: [:]),
            handlers: RemoteProcessHandlers(stdout: { _ in }, stderr: { _ in }, exit: { status.value = $0 })
        )
        process.terminate()
        #expect(await Self.waitUntil { status.value != nil })
        #expect(status.value == 128 + SIGTERM)
    }
}
