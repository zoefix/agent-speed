import Darwin
import Foundation
import Testing
@testable import AgentSpeedCore

/// Antigravity's renamed server and agy's own usage report (QUOTAS 9.4): the shared fixtures, the sandboxed runner
/// with a stand-in agy script, and the route's fallback.
struct AntigravityReportTests {
    // MARK: Fixtures

    @Test func serversOfTheFixtures() throws {
        let cases = try QuotaVendorFixtureTests.item("antigravityServers").array ?? []
        #expect(cases.count == 14)
        let rules = AgentProcessRules(platform: TrafficPlatform(kind: .windows, home: "C:\\Users\\me"))
        for item in cases {
            let executable = item["executable"]?.string ?? ""
            let arguments = item["arguments"]?.array?.compactMap(\.string) ?? []
            let expected = item["expected"]?.bool ?? false
            if item["platform"]?.string == "macos" {
                #expect(AntigravityRules.isLanguageServer(executable: executable, arguments: arguments) == expected, "\(executable)")
            } else {
                let source = rules.source(executable: executable, arguments: { arguments }, bundleIdentifier: { nil },
                                          ancestorExecutables: [])
                #expect((source == .antigravity) == expected, "\(executable)")
            }
        }
    }

    @Test func reportsOfTheFixtures() throws {
        let cases = try QuotaVendorFixtureTests.item("antigravityCli").array ?? []
        #expect(cases.count == 10)
        for item in cases {
            let name = item["name"]?.string ?? "?"
            let windows = AntigravitySummary.cliWindows(Data((item["stdout"]?.string ?? "").utf8))
            QuotaVendorFixtureTests.expectWindows(windows, item["expected"], name)
        }
    }

    @Test func versionsOfTheFixtures() throws {
        let cases = try QuotaVendorFixtureTests.item("agyVersions").array ?? []
        #expect(cases.count == 12)
        for item in cases {
            let text = item["text"]?.string ?? ""
            let version = AntigravitySummary.agyVersion(text)
            #expect(version?.map(Int64.init) == item["version"]?.array?.compactMap(\.int64), "\(text)")
            #expect((version.map(AntigravitySummary.agySupported) ?? false) == (item["supported"]?.bool ?? false), "\(text)")
        }
    }

    /// Agent Speed's own `agy` and everything below it, and nothing else.
    @Test func theAppsOwnDescendants() {
        let entries = [
            ProcessEntry(pid: 1, ppid: 0, startUs: 0), ProcessEntry(pid: 300, ppid: 1, startUs: 10),
            ProcessEntry(pid: 301, ppid: 300, startUs: 20), ProcessEntry(pid: 302, ppid: 301, startUs: 30),
            ProcessEntry(pid: 400, ppid: 1, startUs: 10), ProcessEntry(pid: 401, ppid: 400, startUs: 20),
            // A reused pid: its "parent" started after it.
            ProcessEntry(pid: 500, ppid: 300, startUs: 5),
        ]
        #expect(AntigravityProcessLocator.descendants(of: 300, in: entries) == [300, 301, 302])
    }

    // MARK: The runner

    /// A stand-in for agy: `--version` prints `$FAKE_VERSION`; the report records what it was given, then acts on
    /// `$FAKE_MODE`.
    final class FakeAgy {
        let home: TempDirectory
        let record: URL
        let binary: URL

        init() throws {
            home = try TempDirectory()
            record = home.file("record")
            try FileManager.default.createDirectory(at: record, withIntermediateDirectories: true)
            binary = try home.write(".local/bin/agy", """
                #!/bin/sh
                if [ "$1" = "--version" ]; then
                  echo x >> "$RECORD/versions"
                  printf '%s\\n' "$FAKE_VERSION"
                  exit 0
                fi
                printf '%s\\n' "$@" > "$RECORD/args"
                pwd -P > "$RECORD/cwd"
                printf '%s\\n' "$PATH" | cut -d: -f1 > "$RECORD/path"
                printf '%s\\n' "$BROWSER" > "$RECORD/browser"
                printf '%s\\n' "$AGY_CLI_DISABLE_AUTO_UPDATE" > "$RECORD/update"
                cat > "$RECORD/stdin"
                o=$(command -v open)
                case "$o" in */agent-speed-agy-*/open) "$o" https://example.com/login; echo $? > "$RECORD/open" ;; *) echo unsafe > "$RECORD/open" ;; esac
                case "$FAKE_MODE" in
                  flood) yes 0123456789 | head -c 2000000 ;;
                  linger) sleep 300 & echo $! > "$RECORD/child"; cat "$RECORD/report" ;;
                  *) cat "$RECORD/report" ;;
                esac
                """)
            chmod(binary.path, 0o700)
        }

        func report(version: String = "1.5.9", mode: String = "report", report body: String) throws -> AgyUsageReport {
            try body.write(to: record.appendingPathComponent("report"), atomically: true, encoding: .utf8)
            let variables = ["PATH": "/usr/bin:/bin", "RECORD": record.path, "FAKE_VERSION": version, "FAKE_MODE": mode]
            return AgyUsageReport(home: home.url, variables: variables, now: { 1_794_338_773_000 })
        }

        func text(_ name: String) -> String? {
            (try? String(contentsOf: record.appendingPathComponent(name), encoding: .utf8))?
                .trimmingCharacters(in: .whitespacesAndNewlines)
        }
    }

    static var observedReport: String {
        get throws {
            let cases = try QuotaVendorFixtureTests.item("antigravityCli").array ?? []
            return try #require(cases.first { $0["name"]?.string == "observed" }?["stdout"]?.string)
        }
    }

    @Test func theReportRunsInItsSandbox() throws {
        let agy = try FakeAgy()
        let report = try agy.report(report: Self.observedReport)
        #expect(report.available())
        let result = try #require(report.report())
        #expect(result.windows.map { $0.label() } == ["Gemini 7d", "Claude/GPT 7d"])
        #expect(result.observedMs == 1_794_338_773_000)
        #expect(agy.text("args")?.split(separator: "\n").map(String.init) == AgyUsageReport.reportArguments)
        let folder = try #require(agy.text("cwd"))
        #expect(folder.contains("agent-speed-agy-"))
        #expect(agy.text("path").map { folder.hasSuffix(URL(fileURLWithPath: $0).lastPathComponent) } == true)
        #expect(agy.text("browser")?.hasSuffix("/open") == true)
        #expect(agy.text("update") == "true")
        #expect(agy.text("stdin") == "", "standard input is the null device")
        #expect(agy.text("open") == "1", "the stub `open` refuses to open a browser")
        #expect(!FileManager.default.fileExists(atPath: folder), "the private folder is removed")
    }

    @Test func theVersionIsAskedOncePerBinary() throws {
        let agy = try FakeAgy()
        let report = try agy.report(report: Self.observedReport)
        #expect(report.report() != nil)
        #expect(report.report() != nil)
        #expect(agy.text("versions") == "x", "kept for the same binary")
        try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSince1970: 1_000)], ofItemAtPath: agy.binary.path)
        #expect(report.report() != nil)
        #expect(agy.text("versions")?.split(separator: "\n").count == 2, "a changed binary is asked again")
    }

    @Test func anOldOrUnknownVersionRunsNoReport() throws {
        for version in ["1.1.10", "agy 1.5.9", ""] {
            let agy = try FakeAgy()
            let report = try agy.report(version: version, report: Self.observedReport)
            #expect(report.report() == nil, "\(version)")
            #expect(agy.text("args") == nil, "\(version): no report ran")
        }
        let none = AgyUsageReport(home: try TempDirectory().url, variables: ["PATH": "/nonexistent"])
        #expect(!none.available())
        #expect(none.report() == nil)
    }

    @Test func tooMuchOutputIsAFailure() throws {
        let agy = try FakeAgy()
        let report = try agy.report(mode: "flood", report: Self.observedReport)
        #expect(report.report() == nil)
    }

    /// The command's group goes with it: a descendant holding the output does not keep the report waiting or alive.
    @Test func descendantsDoNotOutliveTheReport() throws {
        let agy = try FakeAgy()
        let report = try agy.report(mode: "linger", report: Self.observedReport)
        let started = Date()
        #expect(report.report() != nil)
        #expect(Date().timeIntervalSince(started) < 10)
        let child = try #require(agy.text("child").flatMap { Int32($0) })
        #expect(Self.gone(child))
    }

    @Test func aCommandPastItsTimeIsKilledWithItsGroup() throws {
        let folder = try TempDirectory()
        let pidFile = folder.file("child").path
        let started = Date()
        let outcome = ChildProcess.run(ChildProcess.Command(
            executable: "/bin/sh", arguments: ["-c", "sleep 300 & echo $! > '\(pidFile)'; sleep 300"],
            environment: ["PATH": "/usr/bin:/bin"], directory: folder.path, timeoutMs: 1_000, outputCap: 1 << 10))
        #expect(outcome == .timedOut)
        #expect(Date().timeIntervalSince(started) < 5)
        let child = try #require((try? String(contentsOfFile: pidFile, encoding: .utf8)).flatMap {
            Int32($0.trimmingCharacters(in: .whitespacesAndNewlines))
        })
        #expect(Self.gone(child))
        #expect(ChildProcess.run(ChildProcess.Command(executable: "/nonexistent/agy", arguments: [], environment: [:],
                                                      directory: folder.path, timeoutMs: 1_000, outputCap: 1)) == .notStarted)
    }

    /// No such process within a second (a killed child is reaped by launchd).
    static func gone(_ pid: Int32) -> Bool {
        for _ in 0..<50 {
            if kill(pid, 0) != 0, errno == ESRCH { return true }
            usleep(20_000)
        }
        return false
    }

    // MARK: The route

    static let summary = "http://127.0.0.1:5000/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary"
    static let reported = (windows: [QuotaWindow(kind: .sevenDay, name: "Gemini", usedPercent: 4.52, resetsAtMs: 1_795_228_106_000)],
                           observedMs: QuotaServiceTests.now)

    /// agy ≥ 1.2.2 refuses a request without its token; the report takes over and the refusal stops nothing.
    @Test func aTokenlessRefusalFallsBackToTheReport() async throws {
        let home = try TempDirectory()
        let transport = ScriptedQuotaTransport()
        transport.script(Self.summary, .status(401, ""))
        let report = FixedReport(answer: Self.reported)
        let agy = AntigravityServer(pid: 88, endpoints: [.init(scheme: "http", port: 5000, token: nil)])
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport, antigravity: [agy],
                                                            agyRuns: true, report: report))
        var readings = await service.refresh(QuotaServiceTests.round([.antigravity], running: [.antigravity]))
        #expect(transport.requests.count == 1)
        #expect(report.reports == 1)
        #expect(readings.routes[.antigravity]?.snapshot?.windows.map { $0.label() } == ["Gemini 7d"])
        #expect(readings.routes[.antigravity]?.status == .ok(QuotaServiceTests.now))
        #expect(readings.routes[.antigravity]?.signedIn == true)
        // A failed report waits 15 minutes, like any failure.
        report.answer = nil
        readings = await service.refresh(QuotaServiceTests.round([.antigravity], at: QuotaServiceTests.now + 5 * 60_000,
                                                                 running: [.antigravity]))
        #expect(report.reports == 2)
        #expect(readings.routes[.antigravity]?.status == .failed)
        readings = await service.refresh(QuotaServiceTests.round([.antigravity], at: QuotaServiceTests.now + 15 * 60_000,
                                                                 running: [.antigravity]))
        #expect(report.reports == 2)
        readings = await service.refresh(QuotaServiceTests.round([.antigravity], at: QuotaServiceTests.now + 20 * 60_000,
                                                                 running: [.antigravity]))
        #expect(report.reports == 3)
    }

    @Test func aServerThatAnswersLeavesTheReportAlone() async throws {
        let home = try TempDirectory()
        let transport = ScriptedQuotaTransport()
        transport.script(Self.summary, .status(200, #"{"groups":[]}"#))
        let report = FixedReport(answer: Self.reported)
        let desktop = AntigravityServer(pid: 77, endpoints: [.init(scheme: "http", port: 5000, token: "t")])
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport, antigravity: [desktop],
                                                            agyRuns: true, report: report))
        let readings = await service.refresh(QuotaServiceTests.round([.antigravity], running: [.antigravity]))
        #expect(report.reports == 0, "an answer without windows is an answer")
        #expect(readings.routes[.antigravity]?.snapshot?.windows.isEmpty == true)
    }

    @Test func theReportRunsOnlyWithAgyOrTheDashboard() async throws {
        let home = try TempDirectory()
        let transport = ScriptedQuotaTransport()
        let report = FixedReport(answer: Self.reported)
        var service = QuotaVendorService(environment: .test(home: home.url, transport: transport, report: report))
        var readings = await service.refresh(QuotaServiceTests.round([.antigravity], running: [.antigravity]))
        #expect(report.reports == 0, "a language server's process alone, without agy or the dashboard")
        readings = await service.refresh(QuotaServiceTests.round([.antigravity], dashboard: true))
        #expect(report.reports == 1)
        #expect(readings.routes[.antigravity]?.snapshot?.windows.count == 1)
        readings = await service.refresh(QuotaServiceTests.round([.antigravity], at: QuotaServiceTests.now + 10 * 60_000,
                                                                 network: false, dashboard: true))
        #expect(report.reports == 1, "nothing runs with the network off")
        #expect(readings.routes[.antigravity]?.status == .networkOff)
        // Without a binary, nothing changes: no server is not running.
        let missing = FixedReport(binary: false, answer: Self.reported)
        service = QuotaVendorService(environment: .test(home: home.url, transport: transport, agyRuns: true, report: missing))
        readings = await service.refresh(QuotaServiceTests.round([.antigravity], running: [.antigravity]))
        #expect(missing.reports == 0)
        #expect(readings.routes[.antigravity]?.status == .notRunning)
        #expect(readings.antigravityRuns, "agy runs although it serves nothing")
    }
}
