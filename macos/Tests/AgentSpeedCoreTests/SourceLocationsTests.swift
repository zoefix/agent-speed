import Foundation
import Testing
@testable import AgentSpeedCore

/// METRICS 2.1: the location lists, their stored form, the editing rules and the roots they name (issue #45).
@Suite("Location lists")
struct SourceLocationsTests {
    // MARK: - Lists

    @Test func everyFolderStartsAutomaticOnly() {
        let locations = SourceLocations()
        #expect(TranscriptFolder.allCases.allSatisfy { locations[$0] == [.automatic] })
        #expect(locations.isAutomaticOnly)
        #expect(TranscriptFolder.claude.sources == [.claudeCode, .claudeDesktop])
        #expect(SourceKind.allCases.allSatisfy { $0.transcriptFolder.sources.contains($0) })
    }

    @Test func listsAreNormalized() {
        var locations = SourceLocations()
        locations[.codex] = [.path("/w/codex/sessions/"), .automatic, .path("/w/codex/./sessions"), .path("relative"), .path("/x/../y")]
        #expect(locations[.codex] == [.automatic, .path("/w/codex/sessions"), .path("/y")], "automatic first, each once, absolute")
        locations[.codex] = (0..<12).map { .path("/w/\($0)") }
        #expect(locations[.codex].count == SourceLocations.maxEntries)
        locations[.codex] = [.path("relative")]
        #expect(locations[.codex] == [.automatic], "an empty list reads the automatic roots")
        #expect(locations.isAutomaticOnly)
    }

    @Test func aSingleFolderOfAnEarlierVersionBecomesTheWholeList() {
        let locations = SourceLocations(legacy: [.codex: "/w/codex/sessions", .openclaw: "/w/agent/openclaw-agent.sqlite"])
        #expect(locations[.codex] == [.path("/w/codex/sessions")], "the automatic entry stays out: the same folders are read")
        #expect(locations[.openclaw] == [.path("/w/agent/openclaw-agent.sqlite")])
        #expect(locations[.claude] == [.automatic])
    }

    // MARK: - Document

    @Test func theDocumentRoundTripsAndLeavesAutomaticOnlyListsOut() throws {
        var locations = SourceLocations()
        locations[.codex] = [.automatic, .path("/Users/me/work/codex/sessions")]
        locations[.hermes] = [.path("/srv/hermes")]
        let data = locations.data()
        let text = try #require(String(data: data, encoding: .utf8))
        #expect(text == #"{"lists":{"codex":[{"automatic":true},{"path":"\/Users\/me\/work\/codex\/sessions"}],"hermes":[{"path":"\/srv\/hermes"}]},"version":1}"#)
        #expect(SourceLocations(data: data) == locations)
        #expect(SourceLocations(data: SourceLocations().data())?.isAutomaticOnly == true)
    }

    @Test func decodingIsTolerant() throws {
        let json = #"""
        {"version":7,"future":true,"lists":{
          "codex":[{"path":"relative"},{"kind":"other"},{"automatic":false},{"path":"/a"},{"path":"/a/"},{"automatic":true}],
          "claude":"not a list",
          "newSource":[{"path":"/n"}]
        }}
        """#
        let decoded = try #require(SourceLocations(data: Data(json.utf8)))
        #expect(decoded[.codex] == [.automatic, .path("/a")])
        #expect(decoded[.claude] == [.automatic])
        let reencoded = try #require(String(data: decoded.data(), encoding: .utf8))
        #expect(reencoded.contains(#""newSource":[{"path":"\/n"}]"#), "a list of an unknown source is kept")
        #expect(SourceLocations(data: Data("[1]".utf8)) == nil)
        #expect(SourceLocations(data: Data("{}".utf8))?.isAutomaticOnly == true)
    }

    // MARK: - Editing

    private static let codexDefault = AutomaticKeysFixture(keys: ["/u/.codex/sessions", "/u/.openclaw/agents/a/agent/codex-home/sessions"])

    private struct AutomaticKeysFixture {
        let keys: [String]
        var value: SourceLocations.AutomaticKeys { SourceLocations.AutomaticKeys(keys: keys) }
    }

    @Test func addingAppendsAndRefusesWhatIsAlreadyRead() {
        var locations = SourceLocations()
        let automatic = Self.codexDefault.value
        #expect(locations.add("/w/codex/sessions", to: .codex, automatic: automatic) == .added)
        #expect(locations[.codex] == [.automatic, .path("/w/codex/sessions")])
        #expect(locations.add("/w/codex/sessions/", to: .codex, automatic: automatic) == .alreadyListed)
        #expect(locations.add("/u/.codex/sessions", to: .codex, automatic: automatic) == .alreadyAutomatic)
        #expect(locations.add("/u/.openclaw/agents/a/agent/codex-home/sessions", to: .codex, automatic: automatic) == .alreadyAutomatic,
                "OpenClaw's Codex homes are automatic Codex roots")
        #expect(locations.add("relative", to: .codex, automatic: automatic) == .invalid)
        #expect(locations[.codex].count == 2)
    }

    @Test func theDefaultLocationBringsTheAutomaticEntryBack() {
        var locations = SourceLocations(legacy: [.codex: "/w/codex/sessions"])
        #expect(locations.add("/u/.codex/sessions", to: .codex, automatic: Self.codexDefault.value) == .restoredAutomatic)
        #expect(locations[.codex] == [.automatic, .path("/w/codex/sessions")], "the automatic entry comes back first")
    }

    @Test func whereTheAutomaticSetIsWiderTheDefaultIsAddedAsAFolder() {
        for folder in [TranscriptFolder.omp, .antigravity, .pi, .openclaw] {
            var locations = SourceLocations(legacy: [folder: "/w/other"])
            #expect(locations.add("/u/default", to: folder, automatic: .init(keys: ["/u/default", "/u/profile"])) == .added)
            #expect(locations[folder] == [.path("/w/other"), .path("/u/default")], "\(folder)")
        }
        #expect(TranscriptFolder.allCases.filter(\.automaticIsWiderThanDefault) == [.omp, .antigravity, .pi, .openclaw])
    }

    @Test func atMostEightEntriesAndAtLeastOne() {
        var locations = SourceLocations()
        for index in 1..<SourceLocations.maxEntries {
            #expect(locations.add("/w/\(index)", to: .dsh, automatic: .init(keys: ["/u/.dsh/sessions"])) == .added)
        }
        #expect(locations[.dsh].count == 8)
        #expect(!locations.canAdd(to: .dsh))
        #expect(locations.add("/w/9", to: .dsh, automatic: .init(keys: ["/u/.dsh/sessions"])) == .full)
        locations.remove(.automatic, from: .dsh)
        #expect(locations.add("/u/.dsh/sessions", to: .dsh, automatic: .init(keys: ["/u/.dsh/sessions"])) == .restoredAutomatic,
                "below the limit again")
        #expect(locations.canReset(.dsh))
        locations.reset(.dsh)
        #expect(locations[.dsh] == [.automatic])
        #expect(!locations.canReset(.dsh))
        #expect(!locations.canRemove(from: .dsh))
        locations.remove(.automatic, from: .dsh)
        #expect(locations[.dsh] == [.automatic], "the only entry stays")
    }

    @Test func removingTheAutomaticEntryKeepsTheFolders() {
        var locations = SourceLocations()
        locations[.claude] = [.automatic, .path("/w/a"), .path("/w/b")]
        locations.remove(.path("/w/a"), from: .claude)
        locations.remove(.automatic, from: .claude)
        #expect(locations[.claude] == [.path("/w/b")])
        #expect(!locations.canRemove(from: .claude))
    }

    @Test func locationsThatNameTheSameRootAreTheSame() throws {
        let directory = try TempDirectory()
        let root = directory.url.path
        try directory.write(".factory/sessions/keep", "")
        try directory.write("oc/opencode.db", "")
        try directory.write("z/.zcode/cli/db/db.sqlite", "")
        try directory.write("claw/agents/main/agent/openclaw-agent.sqlite", "")
        let cases: [(TranscriptFolder, String, String)] = [
            (.workbuddy, "/w/.workbuddy", "/w/.workbuddy/projects"),
            (.workbuddyAi, "/w/.workbuddy-ai/", "/w/.workbuddy-ai/projects"),
            (.kimi, "/w/.kimi-code", "/w/.kimi-code/sessions"),
            (.grokBuild, "/w/.grok", "/w/.grok/sessions"),
            (.antigravity, "/w/.gemini/antigravity-cli", "/w/.gemini/antigravity-cli/conversations"),
            (.droid, root + "/.factory", root + "/.factory/sessions"),
            (.opencode, root + "/oc", root + "/oc/opencode.db"),
            (.zcode, root + "/z", root + "/z/.zcode/cli/db/db.sqlite"),
            (.zcode, root + "/z/.zcode/cli/db", root + "/z/.zcode/cli/db/db.sqlite"),
            (.openclaw, root + "/claw/agents/main/agent", root + "/claw/agents/main/agent/openclaw-agent.sqlite"),
        ]
        for (folder, first, second) in cases {
            var locations = SourceLocations()
            #expect(locations.add(first, to: folder, automatic: .init(keys: ["/elsewhere"])) == .added, "\(folder) \(first)")
            #expect(locations.add(second, to: folder, automatic: .init(keys: ["/elsewhere"])) == .alreadyListed, "\(folder) \(second)")
        }
        var different = SourceLocations()
        #expect(different.add("/w/.hermes", to: .hermes, automatic: .init(keys: ["/elsewhere"])) == .added)
        #expect(different.add("/w/.hermes/logs", to: .hermes, automatic: .init(keys: ["/elsewhere"])) == .added,
                "a home and its logs folder differ: the home has profiles")
    }

    @Test func aSymlinkToAListedFolderIsTheSameLocation() throws {
        let directory = try TempDirectory()
        try FileManager.default.createDirectory(at: directory.file("real/sessions"), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: directory.file("link"), withDestinationURL: directory.file("real"))
        var locations = SourceLocations()
        #expect(locations.add(directory.file("real/sessions").path, to: .codex, automatic: .init(keys: ["/elsewhere"])) == .added)
        #expect(locations.add(directory.file("link/sessions").path, to: .codex, automatic: .init(keys: ["/elsewhere"])) == .alreadyListed)
    }

    @Test func automaticKeysFollowTheEnvironment() {
        var configuration = MonitorConfiguration()
        configuration.automaticRoots[.codex] = [URL(fileURLWithPath: "/nonexistent/u/.codex/sessions")]
        configuration.openclawHome = OpenClawHome(environment: ["OPENCLAW_STATE_DIR": "/nonexistent/claw"], home: URL(fileURLWithPath: "/u"))
        configuration.hermesHome = HermesHome(environment: [:], home: URL(fileURLWithPath: "/nonexistent/h"))
        #expect(SourceLocations.automaticKeys(of: .codex, configuration: configuration).keys == ["/nonexistent/u/.codex/sessions"])
        #expect(SourceLocations.automaticKeys(of: .openclaw, configuration: configuration).keys == ["/nonexistent/claw"])
        #expect(SourceLocations.automaticKeys(of: .hermes, configuration: configuration).defaultKey == "/nonexistent/h/.hermes")
    }

    // MARK: - Roots

    /// A configuration without OpenClaw state, so Codex's automatic entry is its sessions folder alone.
    private static func isolated(_ build: (inout MonitorConfiguration) -> Void = { _ in }) -> MonitorConfiguration {
        var configuration = MonitorConfiguration()
        configuration.openclawHome = OpenClawHome(environment: ["OPENCLAW_STATE_DIR": "/nonexistent/claw"], home: URL(fileURLWithPath: "/u"))
        configuration.automaticRoots[.codex] = [URL(fileURLWithPath: "/u/.codex/sessions")]
        build(&configuration)
        return configuration
    }

    @Test func aSourceReadsTheUnionOfItsEntriesInListOrder() {
        let configuration = Self.isolated {
            $0.locations[.codex] = [.automatic, .path("/w/codex/sessions"), .path("/u/.codex/sessions/"), .path("/w/b")]
        }
        #expect(configuration.resolvedRoots(for: .codex) == ["/u/.codex/sessions", "/w/codex/sessions", "/w/b"],
                "a root named twice is read once")
        let without = Self.isolated { $0.locations[.codex] = [.path("/w/a"), .path("/w/b")] }
        #expect(without.resolvedRoots(for: .codex) == ["/w/a", "/w/b"])
        #expect(without.requiresRescan(comparedTo: configuration))
        #expect(!Self.isolated { $0.locations[.codex] = [.path("/u/.codex/sessions")] }.requiresRescan(comparedTo: Self.isolated()),
                "the same folders read: nothing to read again")
    }

    @Test func eachUserLocationNamesItsSourcesRoot() {
        let configuration = Self.isolated {
            $0.automaticRoots[.workbuddy] = [URL(fileURLWithPath: "/u/.workbuddy")]
            $0.locations[.workbuddy] = [.automatic, .path("/w/.workbuddy")]
            $0.locations[.kimi] = [.path("/w/.kimi-code"), .path("/w/other/sessions")]
            $0.automaticRoots[.antigravity] = [URL(fileURLWithPath: "/u/.gemini/antigravity")]
            $0.locations[.antigravity] = [.automatic, .path("/w/.gemini/antigravity-ide/conversations")]
        }
        #expect(configuration.resolvedRoots(for: .workbuddy) == ["/u/.workbuddy/projects", "/w/.workbuddy/projects"])
        #expect(configuration.resolvedRoots(for: .kimi) == ["/w/.kimi-code/sessions", "/w/other/sessions"])
        #expect(configuration.resolvedRoots(for: .antigravity) == ["/u/.gemini/antigravity/conversations",
                                                                  "/w/.gemini/antigravity-ide/conversations"])
    }

    @Test func codexDropsOpenClawsCodexHomesOnlyWithTheAutomaticEntry() {
        var configuration = MonitorConfiguration()
        configuration.openclawHome = OpenClawTests.home(fixtures: Fixtures.root)
        configuration.automaticRoots[.codex] = [URL(fileURLWithPath: "/u/.codex/sessions")]
        let homes = configuration.openclawCodexRoots
        #expect(!homes.isEmpty)
        configuration.locations[.codex] = [.automatic, .path("/w/codex")]
        #expect(configuration.resolvedRoots(for: .codex) == ["/u/.codex/sessions"] + homes + ["/w/codex"])
        configuration.locations[.codex] = [.path("/w/codex")]
        #expect(configuration.resolvedRoots(for: .codex) == ["/w/codex"])
    }

    @Test func geminiSettingsFollowTheRootTheyBelongTo() {
        let configuration = Self.isolated {
            $0.automaticRoots[.gemini] = [URL(fileURLWithPath: "/u/.gemini/tmp"), URL(fileURLWithPath: "/u/.cache/.gemini/tmp")]
            $0.locations[.gemini] = [.automatic, .path("/w/.gemini/tmp"), .path("/w/plain")]
        }
        #expect(configuration.resolvedRoots(for: .gemini) == ["/u/.gemini/tmp", "/u/.cache/.gemini/tmp", "/w/.gemini/tmp", "/w/plain"])
        #expect(configuration.resolvedGeminiSettings == [
            "/u/.gemini/tmp": "/u/.gemini/settings.json",
            "/u/.cache/.gemini/tmp": "/u/.gemini/settings.json",
            "/w/.gemini/tmp": "/w/.gemini/settings.json",
            "/w/plain": "/w/plain/settings.json",
        ])
    }

    @Test func databaseSourcesReadEachDatabaseOnce() throws {
        let directory = try TempDirectory()
        try directory.write("a/opencode.db", "")
        try directory.write("b/opencode.db", "")
        try FileManager.default.createSymbolicLink(at: directory.file("c"), withDestinationURL: directory.file("a"))
        let configuration = Self.isolated {
            $0.locations[.opencode] = [.path(directory.file("a").path), .path(directory.file("a/opencode.db").path),
                                       .path(directory.file("b").path), .path(directory.file("c").path)]
        }
        #expect(configuration.resolvedOpencodeRoots == [directory.file("a").path, directory.file("b").path],
                "the folder and its file, and a link to it, are one database")
        let zcode = Self.isolated { $0.locations[.zcode] = [.automatic, .path("/w/zcode")] }
        #expect(zcode.resolvedZcodeRoots == [TranscriptLocations.defaultZcodeRoot.path, "/w/zcode"])
    }

    @Test func openClawAndHermesReadEveryEntry() {
        let configuration = Self.isolated {
            $0.openclawHome = OpenClawHome(environment: ["OPENCLAW_STATE_DIR": "/u/.openclaw"], home: URL(fileURLWithPath: "/u"))
            $0.locations[.openclaw] = [.automatic, .path("/w/agent/openclaw-agent.sqlite"), .path("/u/.openclaw/")]
            $0.hermesHome = HermesHome(environment: [:], home: URL(fileURLWithPath: "/u"))
            $0.locations[.hermes] = [.automatic, .path("/w/hermes/logs"), .path("/u/.hermes")]
        }
        #expect(configuration.resolvedOpenclawRoots == ["/u/.openclaw", "/w/agent/openclaw-agent.sqlite"])
        #expect(configuration.resolvedHermesRoots == [HermesRoot(path: "/u/.hermes", isSetting: false),
                                                      HermesRoot(path: "/w/hermes/logs", isSetting: true)])
    }
}
