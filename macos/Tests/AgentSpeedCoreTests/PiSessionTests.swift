import Foundation
import Testing
@testable import AgentSpeedCore

/// Pi inside the live session and the daily history: automatic roots with Pi's custom session folder, sub-agent
/// parents, main-session counts, the omp overlap and files that start being read past their beginning (METRICS 4.13).
@Suite("Pi session")
struct PiSessionTests {
    static let base: Int64 = 1_792_497_600_000

    static func header(_ ms: Int64, cwd: String) -> String {
        #"{"type":"session","version":3,"id":"s-\#(ms)","timestamp":"\#(OmpSessionTests.iso(ms))","cwd":"\#(cwd)"}"# + "\n"
    }

    static func user(_ id: String, _ ms: Int64) -> String {
        #"{"type":"message","id":"\#(id)","timestamp":"\#(OmpSessionTests.iso(ms))","message":{"role":"user","content":"(omitted)","timestamp":\#(ms)}}"# + "\n"
    }

    static func assistant(_ id: String, start: Int64, duration: Double, output: Int) -> String {
        let end = start + Int64(duration)
        return #"{"type":"message","id":"\#(id)","timestamp":"\#(OmpSessionTests.iso(end + 3))","message":{"role":"assistant","content":[{"type":"text","text":"(omitted)"}],"api":"anthropic-messages","provider":"anthropic","model":"claude-opus-5-5","thinkingLevel":"high","usage":{"input":3,"output":\#(output),"cacheRead":100,"cacheWrite":0},"stopReason":"stop","timestamp":\#(start),"durationMs":\#(duration)}}"#
            + "\n"
    }

    struct Home {
        let directory: TempDirectory
        var configuration: MonitorConfiguration

        init(settings: String? = #"{"sessionDir": "~/pi-flat"}"#) throws {
            directory = try TempDirectory()
            try FileManager.default.createDirectory(at: directory.file(".pi/agent/sessions"), withIntermediateDirectories: true)
            if let settings { try directory.write(".pi/agent/settings.json", settings) }
            configuration = MonitorConfiguration(enabledSources: [.pi, .omp])
            configuration.piHome = PiHome(environment: [:], home: directory.url)
            configuration.automaticRoots = [.omp: [directory.file(".omp/agent/sessions")]]
        }

        func path(_ relative: String) -> String { directory.file(relative).path }
    }

    @Test func automaticRootsIncludeTheCustomFolderAndSubagentsLinkToTheirSession() throws {
        let home = try Home()
        let main = ".pi/agent/sessions/--w-halo--/2026-10-20T12-00-00-000Z_main.jsonl"
        let child = ".pi/agent/sessions/--w-halo--/2026-10-20T12-00-00-000Z_main/reviewer.jsonl"
        let flat = "pi-flat/2026-10-20T12-30-00-000Z_flat.jsonl"
        try home.directory.write(main, Self.header(Self.base, cwd: "/w/halo") + Self.user("u1", Self.base + 1000)
            + Self.assistant("a1", start: Self.base + 1005, duration: 2000.7, output: 120))
        try home.directory.write(child, Self.header(Self.base + 4000, cwd: "/w/halo") + Self.user("u1", Self.base + 4100)
            + Self.assistant("a1", start: Self.base + 4105, duration: 1500, output: 40))
        try home.directory.write(flat, Self.header(Self.base + 6000, cwd: "/w/flat")
            + Self.assistant("f1", start: Self.base + 6100, duration: 900, output: 20))
        try home.directory.write(".pi/agent/sessions/.trash/old.jsonl", Self.header(Self.base, cwd: "/w/halo")
            + Self.assistant("a1", start: Self.base + 1005, duration: 2000, output: 120))

        #expect(home.configuration.resolvedRoots(for: .pi) == [home.path(".pi/agent/sessions"), home.path("pi-flat")])
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base + 10_000 })
        let summary = session.seed(todayStartMs: Self.base)
        #expect(summary.filesDiscovered == 3, "dot folders are skipped")
        #expect(session.responses.count == 3)
        #expect(session.responses.first { $0.agentId == home.path(main) }?.endMs == Self.base + 1005 + 2000)
        #expect(session.responses.allSatisfy { $0.modes.effortAndSpeed.effort?.mode == "high" })
        let (output, _) = session.advance(nowMs: Self.base + 10_000, todayStartMs: Self.base, emitsEvents: false)
        let agents = Dictionary(uniqueKeysWithValues: output.agents.map { ($0.agent.id, $0) })
        #expect(agents[home.path(main)]?.parentId == nil && agents[home.path(main)]?.agent.isSubagent == false)
        #expect(agents[home.path(child)]?.parentId == home.path(main) && agents[home.path(child)]?.agent.isSubagent == true)
        #expect(agents[home.path(flat)]?.agent.project == "flat")
        #expect(output.todayBySource[.pi]?.output == 180)
        #expect(session.sessionCounts(todayStartMs: Self.base)[.pi] == 2, "sub-agent files are not sessions")
        #expect(session.root(for: .pi).detected)
    }

    @Test func anOmpRootThatIsPisIsReadAsPiOnly() throws {
        var home = try Home(settings: nil)
        home.configuration.setLocation(home.directory.file(".pi/agent/sessions"), for: .omp)
        let relative = ".pi/agent/sessions/--w-halo--/2026-10-20T12-00-00-000Z_main.jsonl"
        try home.directory.write(relative, Self.header(Self.base, cwd: "/w/halo") + Self.assistant("a1", start: Self.base + 5, duration: 1000, output: 7))
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base + 10_000 })
        let summary = session.seed(todayStartMs: Self.base)
        #expect(summary.filesDiscovered == 1)
        #expect(session.responses.map(\.key) == ["pi:a1:\(Self.base + 5)"] && session.responses.first?.source == .pi)
        #expect(!session.watchedPaths.isEmpty)
        #expect(session.sessionCounts(todayStartMs: Self.base)[.omp] == nil)

        let report = try HistoryScan.run(configuration: home.configuration, timeZone: HistoryTests.utc)
        #expect(report.filesAnalyzed == 1 && report.days.map(\.source) == [.pi])
    }

    @Test func aChangedSessionDirIsNoticed() throws {
        let home = try Home(settings: nil)
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base })
        #expect(!session.piRootsChanged)
        try home.directory.write(".pi/agent/settings.json", #"{"sessionDir": "~/pi-flat"}"#)
        #expect(session.piRootsChanged)
        #expect(!TranscriptSession(configuration: home.configuration, clock: { Self.base }).piRootsChanged)
    }

    @Test func anOlderFileThatGrowsReadsItsHeaderFirst() throws {
        let home = try Home()
        let relative = "pi-flat/2026-10-19T09-00-00-000Z_old.jsonl"
        let yesterday = Self.base - 86_400_000
        try home.directory.write(relative, Self.header(yesterday, cwd: "/w/nova") + Self.user("u1", yesterday + 1000)
            + Self.assistant("a1", start: yesterday + 1005, duration: 1000, output: 10),
            modified: Date(timeIntervalSince1970: TimeInterval(yesterday) / 1000 + 5))
        let session = TranscriptSession(configuration: home.configuration, clock: { Self.base + 60_000 })
        #expect(session.seed(todayStartMs: Self.base).filesRead == 0 && session.responses.isEmpty)

        try home.directory.append(relative, Self.user("u2", Self.base + 50_000)
            + Self.assistant("a2", start: Self.base + 50_005, duration: 4000.9, output: 80))
        let resolved = try #require(realpath(home.path(relative), nil))
        let canonical = String(cString: resolved)
        free(resolved)
        #expect(session.handleChanges(atCanonicalPaths: [canonical]))
        #expect(session.responses.map(\.key) == ["pi:a2:\(Self.base + 50_005)"])
        let (output, events) = session.advance(nowMs: Self.base + 60_000, todayStartMs: Self.base, emitsEvents: true)
        #expect(output.agents.first?.agent.project == "nova" && output.agents.first?.phase == .idle)
        #expect(events.count == 1)
    }
}
