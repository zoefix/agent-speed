import Foundation
import Testing
@testable import AgentSpeedCore

/// The remote scripts run for real with the local shells: everything they do is POSIX, `uname` aside.
@Suite("Remote scripts", .serialized)
struct RemoteScriptTests {
    struct Outcome {
        var status: Int32
        var stdout: String
        var stderr: String
    }

    static func run(_ executable: String, _ arguments: [String], stdin: Data = Data(),
                    environment: [String: String]? = nil) throws -> Outcome {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: executable)
        process.arguments = arguments
        if let environment { process.environment = environment }
        let input = Pipe()
        let output = Pipe()
        let errors = Pipe()
        process.standardInput = input
        process.standardOutput = output
        process.standardError = errors
        try process.run()
        // A thread of its own: a dispatch queue may not get one while the test threads are blocked.
        let writer = input.fileHandleForWriting
        Thread {
            try? writer.write(contentsOf: stdin)
            try? writer.close()
        }.start()
        let out = output.fileHandleForReading.readDataToEndOfFile()
        let err = errors.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return Outcome(status: process.terminationStatus, stdout: String(decoding: out, as: UTF8.self),
                       stderr: String(decoding: err, as: UTF8.self))
    }

    /// `sh -c '<script>' agent-speed <arguments>` with the script as one argument (what the remote shell ends with).
    static func script(_ script: String, _ arguments: [String], shell: String = "/bin/sh", stdin: Data = Data(),
                       environment: [String: String]? = nil) throws -> Outcome {
        try run(shell, ["-c", script, "agent-speed"] + arguments, stdin: stdin, environment: environment)
    }

    /// A PATH folder with only these tools (and the basics the scripts use), to force each hash tool in turn.
    static func toolFolder(_ directory: TempDirectory, hashTools: [String: String]) throws -> String {
        let bin = directory.file("bin")
        try FileManager.default.createDirectory(at: bin, withIntermediateDirectories: true)
        let basics = ["uname": "/usr/bin/uname", "cat": "/bin/cat", "mkdir": "/bin/mkdir", "chmod": "/bin/chmod",
                      "mv": "/bin/mv", "rm": "/bin/rm"]
        for (name, target) in basics.merging(hashTools, uniquingKeysWith: { $1 }) {
            try FileManager.default.createSymbolicLink(atPath: bin.appendingPathComponent(name).path,
                                                       withDestinationPath: target)
        }
        return bin.path
    }

    static let hashTools: [(String, [String: String])] = [
        ("sha256sum", ["sha256sum": "/sbin/sha256sum"]),
        ("shasum -a 256", ["shasum": "/usr/bin/shasum"]),
        ("openssl dgst -sha256 -r", ["openssl": "/usr/bin/openssl"]),
    ]

    @Test func probeListsSystemToolAndRelayHashes() throws {
        let directory = try TempDirectory()
        let folder = directory.file("relay")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let relay = pseudoRandomData(count: 70_001, seed: 3)
        try relay.write(to: folder.appendingPathComponent("agent-speed-relay-1.7.0-x86_64-0123456789ab"))
        try Data("old".utf8).write(to: folder.appendingPathComponent("agent-speed-relay-1.6.0-x86_64-ffffffffffff"))
        try Data("keep".utf8).write(to: folder.appendingPathComponent("history.sqlite"))
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("agent-speed-relay-dir"),
                                                withIntermediateDirectories: true)
        let result = try Self.script(RemoteScripts.probe, [folder.path])
        #expect(result.status == 0, "\(result.stderr)")
        let probe = RemoteProbeResult.parse(Data(result.stdout.utf8))
        #expect(probe.kernel == (try runTool("/usr/bin/uname", ["-s"])).trimmingCharacters(in: .whitespacesAndNewlines))
        #expect(probe.isComplete)
        #expect(probe.hashTool != nil)
        #expect(probe.hashes == [
            "agent-speed-relay-1.7.0-x86_64-0123456789ab": FileDigest.sha256(of: relay),
            "agent-speed-relay-1.6.0-x86_64-ffffffffffff": FileDigest.sha256(of: Data("old".utf8)),
        ])
        #expect(probe.verdict == .unsupportedSystem(probe.system))
    }

    @Test func probeOfAMissingFolderListsNothing() throws {
        let directory = try TempDirectory()
        let result = try Self.script(RemoteScripts.probe, [directory.file("missing").path])
        #expect(result.status == 0)
        let probe = RemoteProbeResult.parse(Data(result.stdout.utf8))
        #expect(probe.isComplete)
        #expect(probe.hashes.isEmpty)
        #expect(!FileManager.default.fileExists(atPath: directory.file("missing").path))
    }

    @Test(arguments: hashTools)
    func probeAndUploadUseEveryHashTool(_ expectedTool: String, _ tools: [String: String]) throws {
        let directory = try TempDirectory()
        let path = try Self.toolFolder(directory, hashTools: tools)
        let environment = ["PATH": path, "HOME": directory.path]
        let folder = directory.file("relay").path
        let relay = pseudoRandomData(count: 300_000, seed: 4)
        let digest = FileDigest.sha256(of: relay)
        let name = "agent-speed-relay-1.7.0-x86_64-" + digest.prefix(12)

        let upload = try Self.script(RemoteScripts.upload, [folder, name, digest], stdin: relay, environment: environment)
        #expect(upload.status == 0, "\(upload.stderr)")
        #expect(RemoteProbeResult.uploadedHash(Data(upload.stdout.utf8)) == digest)

        let probe = try Self.script(RemoteScripts.probe, [folder], environment: environment)
        let result = RemoteProbeResult.parse(Data(probe.stdout.utf8))
        #expect(result.hashTool == expectedTool)
        #expect(result.hasInstalled(name: name, sha256: digest))
    }

    @Test func probeWithoutHashToolSaysNone() throws {
        let directory = try TempDirectory()
        let path = try Self.toolFolder(directory, hashTools: [:])
        let result = try Self.script(RemoteScripts.probe, [directory.path], environment: ["PATH": path])
        let probe = RemoteProbeResult.parse(Data(result.stdout.utf8))
        #expect(probe.isComplete)
        #expect(probe.hashTool == nil)

        let upload = try Self.script(RemoteScripts.upload, [directory.file("f").path, "agent-speed-relay-x",
                                                            String(repeating: "0", count: 64)],
                                     stdin: Data("x".utf8), environment: ["PATH": path])
        #expect(upload.status == RemoteCommand.noHashToolStatus)
        #expect(try FileManager.default.contentsOfDirectory(atPath: directory.file("f").path).isEmpty)
    }

    @Test func uploadVerifiesRenamesAndCleansOnlyRelayFiles() throws {
        let directory = try TempDirectory()
        let folder = directory.file("deep/relay")
        let relay = pseudoRandomData(count: 1_000_003, seed: 5)
        let digest = FileDigest.sha256(of: relay)
        let name = "agent-speed-relay-1.7.0-x86_64-" + digest.prefix(12)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o755])
        for stale in ["agent-speed-relay-1.6.0-x86_64-aaaaaaaaaaaa", ".upload-123", "agent-speed-relay-junk"] {
            try Data("stale".utf8).write(to: folder.appendingPathComponent(stale))
        }
        for kept in ["history.sqlite", "zone.tzif", "notes.txt", "agent-speed-relayX"] {
            try Data("keep".utf8).write(to: folder.appendingPathComponent(kept))
        }

        let result = try Self.script(RemoteScripts.upload, [folder.path, name, digest], stdin: relay)
        #expect(result.status == 0, "\(result.stderr)")
        #expect(result.stdout == "hash \(digest)\n")
        let installed = folder.appendingPathComponent(name)
        #expect(try Data(contentsOf: installed) == relay)
        let mode = try FileManager.default.attributesOfItem(atPath: installed.path)[.posixPermissions] as? Int
        #expect(mode == 0o700)
        let names = Set(try FileManager.default.contentsOfDirectory(atPath: folder.path))
        #expect(names == [name, "history.sqlite", "zone.tzif", "notes.txt", "agent-speed-relayX"])
    }

    @Test func uploadCreatesTheFolderPrivate() throws {
        let directory = try TempDirectory()
        let folder = directory.file("new/agent-speed")
        let relay = Data("relay".utf8)
        let digest = FileDigest.sha256(of: relay)
        let result = try Self.script(RemoteScripts.upload, [folder.path, "agent-speed-relay-t", digest], stdin: relay)
        #expect(result.status == 0)
        let mode = try FileManager.default.attributesOfItem(atPath: folder.path)[.posixPermissions] as? Int
        #expect(mode == 0o700)
    }

    @Test func uploadMismatchDeletesTheCopyAndExits3() throws {
        let directory = try TempDirectory()
        let folder = directory.file("relay")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let previous = folder.appendingPathComponent("agent-speed-relay-1.6.0-x86_64-aaaaaaaaaaaa")
        try Data("previous".utf8).write(to: previous)
        let wrong = String(repeating: "0", count: 64)
        let result = try Self.script(RemoteScripts.upload, [folder.path, "agent-speed-relay-new", wrong],
                                     stdin: Data("truncated".utf8))
        #expect(result.status == RemoteCommand.mismatchStatus)
        #expect(RemoteProbeResult.uploadedHash(Data(result.stdout.utf8)) == FileDigest.sha256(of: Data("truncated".utf8)))
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path) == [previous.lastPathComponent])
        #expect(RemoteStatusClassifier.classify(exitStatus: result.status, stderr: Data(), stage: .upload) == .uploadFailed)
    }

    @Test func runExecsTheRelayWithItsFolder() throws {
        let directory = try TempDirectory()
        let folder = directory.file("relay")
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let relay = folder.appendingPathComponent("agent-speed-relay-t")
        try Data("#!/bin/sh\necho \"argv: $*\"\nread line\necho \"got $line\"\n".utf8).write(to: relay)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: relay.path)
        let result = try Self.script(RemoteScripts.run, [folder.path, "agent-speed-relay-t"], stdin: Data("start\n".utf8))
        #expect(result.status == 0)
        #expect(result.stdout == "argv: serve --folder \(folder.path)\ngot start\n")
    }

    @Test func runOfANonExecutableFileIsNotExecutable() throws {
        let directory = try TempDirectory()
        let relay = directory.file("agent-speed-relay-t")
        try Data("#!/bin/sh\necho hi\n".utf8).write(to: relay)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: relay.path)
        for shell in ["/bin/sh", "/bin/dash", "/bin/bash"] {
            let result = try Self.script(RemoteScripts.run, [directory.path, "agent-speed-relay-t"], shell: shell)
            #expect(result.status == 126, "\(shell): \(result.status)")
            #expect(RemoteStatusClassifier.classify(exitStatus: result.status, stderr: Data(result.stderr.utf8), stage: .run)
                == .notExecutable)
        }
    }

    /// The whole remote command through each login shell: the folder's `~` expands, the script stays intact and the
    /// arguments arrive as `$1` ….
    @Test(arguments: ["/bin/sh", "/bin/bash", "/bin/zsh", "/bin/dash", "/bin/ksh", "/bin/tcsh", "/bin/csh"])
    func remoteCommandsThroughLoginShells(_ loginShell: String) throws {
        let home = try TempDirectory()
        let environment = ["HOME": home.path, "PATH": "/usr/bin:/bin:/usr/sbin:/sbin"]
        let relay = Data("#!/bin/sh\necho \"relay $*\"\n".utf8)
        let digest = FileDigest.sha256(of: relay)
        let name = "agent-speed-relay-1.7.0-x86_64-" + digest.prefix(12)

        let upload = try #require(RemoteCommand.upload(folder: "~/.cache/agent-speed", fileName: name, sha256: digest))
        let uploaded = try Self.run(loginShell, ["-c", upload], stdin: relay, environment: environment)
        #expect(uploaded.status == 0, "\(loginShell): \(uploaded.stderr)")
        let folder = home.file(".cache/agent-speed")
        #expect(FileManager.default.fileExists(atPath: folder.appendingPathComponent(name).path))

        let probe = try #require(RemoteCommand.probe(folder: "~/.cache/agent-speed"))
        let probed = try Self.run(loginShell, ["-c", probe], environment: environment)
        #expect(RemoteProbeResult.parse(Data(probed.stdout.utf8)).hasInstalled(name: name, sha256: digest), "\(loginShell)")

        let run = try #require(RemoteCommand.run(folder: "~/.cache/agent-speed", fileName: name))
        let ran = try Self.run(loginShell, ["-c", run], environment: environment)
        #expect(ran.stdout == "relay serve --folder \(folder.path)\n", "\(loginShell): \(ran.stderr)")
    }

    /// Hostile-looking but valid values stay literal: nothing beyond the fixed script runs.
    @Test func validatedArgumentsCannotInject() throws {
        let home = try TempDirectory()
        let environment = ["HOME": home.path, "PATH": "/usr/bin:/bin"]
        let folder = "~/a-b_c.d/~e"
        let probe = try #require(RemoteCommand.probe(folder: folder))
        let result = try Self.run("/bin/zsh", ["-c", probe], environment: environment)
        #expect(result.status == 0)
        #expect(RemoteProbeResult.parse(Data(result.stdout.utf8)).isComplete)
        #expect(try FileManager.default.contentsOfDirectory(atPath: home.path).isEmpty)
        for hostile in ["~/x;touch pwned", "~/$(touch pwned)", "~/`touch pwned`", "~/x' ; touch pwned; '", "~/x\ntouch pwned"] {
            #expect(RemoteCommand.probe(folder: hostile) == nil)
        }
    }
}
