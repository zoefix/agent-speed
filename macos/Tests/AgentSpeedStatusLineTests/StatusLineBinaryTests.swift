import Darwin
import Foundation
import Testing

/// Runs the built `agent-speed-statusline` from a temporary `<data>/bin` with sample input (QUOTAS 5.5).
struct StatusLineBinaryTests {
    struct Run {
        var output: String
        var status: Int32
        var seconds: Double
    }

    /// A data folder with the helper copied into `bin/`.
    final class DataFolder {
        let root: URL
        var helper: URL { root.appendingPathComponent("bin/agent-speed-statusline") }
        var quota: URL { root.appendingPathComponent("quota") }

        init() throws {
            root = FileManager.default.temporaryDirectory
                .appendingPathComponent("agent-speed-statusline-\(UUID().uuidString)", isDirectory: true)
            try FileManager.default.createDirectory(at: root.appendingPathComponent("bin"), withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: try StatusLineBinaryTests.binary(), to: helper)
        }

        deinit { try? FileManager.default.removeItem(at: root) }

        func previous(_ command: String) throws {
            try FileManager.default.createDirectory(at: quota, withIntermediateDirectories: true)
            let object: [String: Any] = ["version": 1, "statusLine": ["type": "command", "command": command]]
            try JSONSerialization.data(withJSONObject: object).write(to: quota.appendingPathComponent("claude-statusline-previous.json"))
        }

        func run(_ input: String, environment: [String: String] = [:], in directory: URL? = nil) throws -> Run {
            let process = Process()
            process.executableURL = helper
            process.currentDirectoryURL = directory ?? root
            process.environment = ProcessInfo.processInfo.environment.merging(environment) { $1 }
            let stdin = Pipe(), stdout = Pipe()
            process.standardInput = stdin
            process.standardOutput = stdout
            let started = Date()
            try process.run()
            stdin.fileHandleForWriting.write(Data(input.utf8))
            try stdin.fileHandleForWriting.close()
            let output = stdout.fileHandleForReading.readDataToEndOfFile()
            process.waitUntilExit()
            return Run(output: String(decoding: output, as: UTF8.self), status: process.terminationStatus,
                       seconds: Date().timeIntervalSince(started))
        }
    }

    /// The products folder of this test run.
    static func binary() throws -> URL {
        let bundles = Bundle.allBundles.map(\.bundleURL) + [URL(fileURLWithPath: CommandLine.arguments.first ?? "")]
        for bundle in bundles where bundle.pathExtension == "xctest" || bundle.path.contains(".build") {
            var folder = bundle.deletingLastPathComponent()
            for _ in 0..<4 {
                let candidate = folder.appendingPathComponent("agent-speed-statusline")
                if FileManager.default.isExecutableFile(atPath: candidate.path) { return candidate }
                folder = folder.deletingLastPathComponent()
            }
        }
        let package = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let fallback = package.appendingPathComponent(".build/debug/agent-speed-statusline")
        guard FileManager.default.isExecutableFile(atPath: fallback.path) else {
            throw CocoaError(.fileNoSuchFile, userInfo: [NSFilePathErrorKey: fallback.path])
        }
        return fallback
    }

    static var now: Int { Int(Date().timeIntervalSince1970) }

    static func input(five: Double, fiveResets: Int, week: Double, weekResets: Int) -> String {
        "{\"session_id\":\"x\",\"model\":{\"id\":\"claude-opus-5-5\"},\"rate_limits\":{\"five_hour\":{\"used_percentage\":\(five),"
            + "\"resets_at\":\(fiveResets)},\"seven_day\":{\"used_percentage\":\(week),\"resets_at\":\(weekResets)}}}"
    }

    @Test func storesTheQuotaAndPrintsTheLine() throws {
        let data = try DataFolder()
        let run = try data.run(Self.input(five: 6, fiveResets: Self.now + 3600, week: 86.4, weekResets: Self.now + 86400))
        #expect(run.status == 0)
        #expect(run.output == "5h 6% · 7d 86%\n")
        let file = data.quota.appendingPathComponent("claude-cli.json")
        let attributes = try FileManager.default.attributesOfItem(atPath: file.path)
        #expect((attributes[.posixPermissions] as? NSNumber)?.intValue == 0o600)
        let object = try #require(try JSONSerialization.jsonObject(with: Data(contentsOf: file)) as? [String: Any])
        #expect((object["version"] as? NSNumber)?.intValue == 1)
        #expect((object["receivedMs"] as? NSNumber).map { abs($0.doubleValue / 1000 - Double(Self.now)) < 60 } == true)
        let windows = try #require(object["windows"] as? [String: Any])
        #expect((windows["seven_day"] as? [String: Any])?["used_percentage"] as? NSNumber == 86.4)

        // A later session without a 5-hour value keeps it; a lower weekly value of the same window is ignored.
        let second = try data.run("{\"rate_limits\":{\"seven_day\":{\"used_percentage\":80,\"resets_at\":\(Self.now + 86400)}}}")
        #expect(second.output == "5h 6% · 7d 86%\n")
        print("statusline helper: \(Int(run.seconds * 1000)) ms without a previous command")
        #expect(run.seconds < 1)
    }

    @Test func runsThePreviousCommandWithTheSameInput() throws {
        let data = try DataFolder()
        let folder = data.root.appendingPathComponent("project", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        try data.previous("cat; echo; echo \"cwd=$(basename \"$PWD\") env=$AGENT_SPEED_TEST_VALUE\"; exit 3")
        let input = Self.input(five: 10, fiveResets: Self.now + 600, week: 20, weekResets: Self.now + 7200)
        let run = try data.run(input, environment: ["AGENT_SPEED_TEST_VALUE": "kept"], in: folder)
        #expect(run.status == 3)
        #expect(run.output == input + "\ncwd=project env=kept\n")
        #expect(FileManager.default.fileExists(atPath: data.quota.appendingPathComponent("claude-cli.json").path))
    }

    @Test func killsASlowPreviousCommand() throws {
        let data = try DataFolder()
        try data.previous("printf 'partial'; sleep 30; echo never")
        let run = try data.run("{}")
        #expect(run.output == "partial")
        #expect(run.status == 0)
        #expect(run.seconds > 9 && run.seconds < 15)
    }

    @Test func staysQuietOnBadInput() throws {
        let data = try DataFolder()
        for input in ["", "not json", "[1,2]", "{\"rate_limits\":5}", "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":true}}}"] {
            let run = try data.run(input)
            #expect(run.status == 0, "\(input)")
            #expect(run.output.isEmpty, "\(input)")
        }
        #expect(!FileManager.default.fileExists(atPath: data.quota.appendingPathComponent("claude-cli.json").path))
    }

    @Test func dropsWindowsWhoseResetPassed() throws {
        let data = try DataFolder()
        let run = try data.run(Self.input(five: 50, fiveResets: Self.now - 10, week: 7, weekResets: Self.now + 3600))
        #expect(run.output == "7d 7%\n")
    }
}
