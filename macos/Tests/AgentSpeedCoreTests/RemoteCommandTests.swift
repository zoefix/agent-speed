import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Remote ssh commands")
struct RemoteCommandTests {
    static let digest = String(repeating: "ab", count: 32)

    @Test func argumentVectorIsExactlyTheSpecs() throws {
        let arguments = try #require(RemoteSSH.arguments(destination: "user@devbox", remoteCommand: "true"))
        #expect(arguments == [
            "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3", "-o", "Compression=yes", "-o", "ControlMaster=no",
            "-o", "ClearAllForwardings=yes", "-o", "ForwardAgent=no", "-o", "ForwardX11=no",
            "-o", "PermitLocalCommand=no", "-o", "RequestTTY=no", "-o", "LogLevel=ERROR",
            "--", "user@devbox", "true",
        ])
        #expect(RemoteSSH.executable == "/usr/bin/ssh")
        #expect(!arguments.contains { $0.contains("StrictHostKeyChecking") })
    }

    @Test func invalidDestinationsGetNoArguments() {
        #expect(RemoteSSH.arguments(destination: "-oProxyCommand=touch /tmp/x", remoteCommand: "true") == nil)
        #expect(RemoteSSH.arguments(destination: "host;rm", remoteCommand: "true") == nil)
        #expect(RemoteSSH.arguments(destination: "", remoteCommand: "true") == nil)
    }

    @Test func environmentAddsCLocale() {
        let environment = RemoteSSH.environment(["HOME": "/Users/me", "LC_ALL": "zh_TW.UTF-8", "LANG": "zh_TW.UTF-8"])
        #expect(environment["LC_ALL"] == "C")
        #expect(environment["HOME"] == "/Users/me")
        #expect(environment["LANG"] == "zh_TW.UTF-8")
    }

    @Test func machinesMapToArchitectures() {
        #expect(RemoteArch(machine: "x86_64") == .x86_64)
        #expect(RemoteArch(machine: "amd64") == .x86_64)
        #expect(RemoteArch(machine: "aarch64") == .aarch64)
        #expect(RemoteArch(machine: "arm64") == .aarch64)
        #expect(RemoteArch(machine: "armv7l") == nil)
        #expect(RemoteArch(machine: "riscv64") == nil)
        #expect(RemoteArch(machine: "i686") == nil)
    }

    @Test func relayFileNames() {
        #expect(RemoteRelayName.bundled(.x86_64) == "agent-speed-relay-linux-x86_64")
        #expect(RemoteRelayName.bundled(.aarch64) == "agent-speed-relay-linux-aarch64")
        #expect(RemoteRelayName.installed(version: "1.7.0", arch: .aarch64, sha256: Self.digest.uppercased())
            == "agent-speed-relay-1.7.0-aarch64-abababababab")
        #expect(RemoteRelayName.installed(version: "1.7.0+b 1;x", arch: .x86_64, sha256: Self.digest)
            == "agent-speed-relay-1.7.0-b-1-x-x86_64-abababababab")
        #expect(RemoteRelayName.isSafe("agent-speed-relay-1.7.0-x86_64-abababababab"))
        #expect(!RemoteRelayName.isSafe("a b"))
        #expect(!RemoteRelayName.isSafe("../x"))
        #expect(!RemoteRelayName.isSafe("-x"))
    }

    @Test func commandsAreFixedScriptsWithValidatedArguments() throws {
        let probe = try #require(RemoteCommand.probe(folder: "~/.cache/agent-speed"))
        #expect(probe == "sh -c '\(RemoteScripts.probe)' agent-speed ~/.cache/agent-speed")
        let upload = try #require(RemoteCommand.upload(folder: "/opt/as/", fileName: "agent-speed-relay-1-x86_64-ab",
                                                       sha256: Self.digest.uppercased()))
        #expect(upload == "sh -c '\(RemoteScripts.upload)' agent-speed /opt/as agent-speed-relay-1-x86_64-ab \(Self.digest)")
        let run = try #require(RemoteCommand.run(folder: "~/.cache/agent-speed", fileName: "agent-speed-relay-1-x86_64-ab"))
        #expect(run == "sh -c 'chmod 700 \"$1\" 2>/dev/null && [ -O \"$1/$2\" ] || exit 126; exec \"$1/$2\" serve --folder \"$1\"' agent-speed ~/.cache/agent-speed agent-speed-relay-1-x86_64-ab")
    }

    @Test func commandsRefuseUnvalidatedValues() {
        #expect(RemoteCommand.probe(folder: "~/x; rm -rf ~") == nil)
        #expect(RemoteCommand.probe(folder: "~/$(id)") == nil)
        #expect(RemoteCommand.probe(folder: "~/'x'") == nil)
        #expect(RemoteCommand.run(folder: "~/x", fileName: "a;b") == nil)
        #expect(RemoteCommand.run(folder: "~/x", fileName: "a b") == nil)
        #expect(RemoteCommand.upload(folder: "~/x", fileName: "agent-speed-relay-x", sha256: "abc") == nil)
        #expect(RemoteCommand.upload(folder: "~/x", fileName: "agent-speed-relay-x", sha256: String(repeating: "g", count: 64)) == nil)
    }

    @Test func scriptsSurviveEveryLoginShellsSingleQuotes() {
        for script in [RemoteScripts.probe, RemoteScripts.upload, RemoteScripts.run] {
            #expect(RemoteScripts.isQuotable(script))
            #expect(!script.contains("'"))
            #expect(!script.contains("\\"))
            #expect(!script.contains("!"))
            #expect(!script.contains("\n"))
        }
    }

    @Test func probeOutputParses() {
        let name = "agent-speed-relay-1.7.0-x86_64-abababababab"
        let output = """
        Welcome banner
        system Linux x86_64
        tool shasum -a 256
        hash \(Self.digest.uppercased()) \(name)
        hash \(Self.digest) agent-speed-relay-weird name
        hash nothex agent-speed-relay-x
        end

        """
        let result = RemoteProbeResult.parse(Data(output.utf8))
        #expect(result.system == "Linux x86_64")
        #expect(result.kernel == "Linux")
        #expect(result.machine == "x86_64")
        #expect(result.hashTool == "shasum -a 256")
        #expect(result.hashes == [name: Self.digest])
        #expect(result.verdict == .supported(.x86_64))
        #expect(result.hasInstalled(name: name, sha256: Self.digest.uppercased()))
        #expect(!result.hasInstalled(name: name, sha256: String(repeating: "0", count: 64)))
    }

    @Test func probeVerdicts() {
        func verdict(_ text: String) -> RemoteProbeResult.Verdict { RemoteProbeResult.parse(Data(text.utf8)).verdict }
        #expect(verdict("system Darwin arm64\ntool shasum -a 256\nend\n") == .unsupportedSystem("Darwin arm64"))
        #expect(verdict("system Linux riscv64\ntool sha256sum\nend\n") == .unsupportedSystem("Linux riscv64"))
        #expect(verdict("system Linux aarch64\ntool none\nend\n") == .cannotVerify)
        #expect(verdict("system Linux arm64\ntool openssl dgst -sha256 -r\nend\n") == .supported(.aarch64))
        #expect(verdict("system Linux x86_64\ntool sha256sum\n") == .malformed)
        #expect(verdict("") == .malformed)
        #expect(verdict("system Linux\u{7} amd64\r\ntool sha256sum\r\nend\r\n") == .supported(.x86_64))
    }

    @Test func uploadedHashIsTheScriptsLine() {
        #expect(RemoteProbeResult.uploadedHash(Data("noise\nhash \(Self.digest.uppercased())\n".utf8)) == Self.digest)
        #expect(RemoteProbeResult.uploadedHash(Data("hash short\n".utf8)) == nil)
    }
}

@Suite("Remote status classification")
struct RemoteStatusClassifierTests {
    func classify(_ status: Int32, _ stderr: String, _ stage: RemoteStatusClassifier.Stage = .run) -> RemoteStatus {
        RemoteStatusClassifier.classify(exitStatus: status, stderr: Data(stderr.utf8), stage: stage)
    }

    @Test func sshFailures() {
        #expect(classify(255, "ssh: Could not resolve hostname devbox: nodename nor servname provided") == .unreachable)
        #expect(classify(255, "ssh: connect to host 10.0.0.1 port 22: Operation timed out") == .unreachable)
        #expect(classify(255, "ssh: connect to host h port 22: Connection refused") == .unreachable)
        #expect(classify(255, "ssh: connect to host h port 22: No route to host") == .unreachable)
        #expect(classify(255, "ssh: connect to host h port 22: Network is unreachable") == .unreachable)
        #expect(classify(255, "Connection closed by 10.0.0.1 port 22") == .unreachable)
        #expect(classify(255, "user@h: Permission denied (publickey,password).", .probe) == .authFailed)
        #expect(classify(255, "Host key verification failed.") == .hostKeyUnknown)
        #expect(classify(255, "kex_exchange_identification: read: Connection reset by peer") == .unreachable)
        #expect(classify(255, "ssh: connect to host 192.0.2.10 port 2224: Permission denied") == .unreachable)
        #expect(classify(255, "ssh: connect to host h port 22: Operation not permitted") == .unreachable)
        #expect(classify(255, "") == .unreachable)
        #expect(classify(255, "\r\n") == .unreachable)
        #expect(classify(255, "something else") == .exited(status: 255))
    }

    @Test func remoteFailures() {
        #expect(classify(126, "") == .notExecutable)
        #expect(classify(1, "sh: 1: exec: /home/u/.cache/agent-speed/x: Permission denied") == .notExecutable)
        #expect(classify(2, "cannot execute binary file") == .notExecutable)
        #expect(classify(127, "not found") == .exited(status: 127))
        #expect(classify(0, "") == .exited(status: 0))
        #expect(classify(3, "", .upload) == .uploadFailed)
        #expect(classify(4, "", .upload) == .uploadFailed)
        #expect(classify(5, "", .upload) == .cannotVerify)
        #expect(classify(1, "", .probe) == .exited(status: 1))
    }

    @Test func onlyTheLast64KiBCount() {
        let noise = String(repeating: "x", count: 70_000)
        #expect(classify(255, "Permission denied" + noise) == .exited(status: 255))
        #expect(classify(255, noise + "Permission denied") == .authFailed)
    }

    @Test func statusGroups() {
        for status: RemoteStatus in [.authFailed, .hostKeyUnknown, .unsupportedSystem(system: "x"), .notExecutable,
                                     .cannotVerify, .invalidDestination, .sshMissing] {
            #expect(status.needsUser)
            #expect(!status.isRetrying)
        }
        for status: RemoteStatus in [.unreachable, .protocolError, .exited(status: 1)] {
            #expect(status.isRetrying)
            #expect(!status.needsUser)
        }
        #expect(RemoteStatus.seeding(fraction: 0.4).isInProgress)
        #expect(RemoteStatus.unsupportedSystem(system: "Linux riscv64").code == "unsupportedSystem")
    }
}
