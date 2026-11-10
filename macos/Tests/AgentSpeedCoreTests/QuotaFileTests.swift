import AgentSpeedStatusLineKit
import Foundation
import Testing
@testable import AgentSpeedCore

/// The quota readers and the status line bridge on real files in temporary folders (QUOTAS 4.4, 5, 6). Nothing here
/// touches the user's own Claude settings.
struct QuotaFileTests {
    // MARK: Status line bridge (QUOTAS 5.2–5.4)

    struct Bridge {
        let folder: TempDirectory
        let bridge: ClaudeStatusLineBridge
        let helper: URL

        init() throws {
            folder = try TempDirectory()
            bridge = ClaudeStatusLineBridge(settings: folder.file("claude/settings.json"), data: folder.file("data"))
            helper = try folder.write("app/agent-speed-statusline", "#!/bin/sh\nexit 0\n")
        }

        var settingsText: String? { try? String(contentsOf: bridge.settings, encoding: .utf8) }

        func mode(_ url: URL) -> Int? {
            (try? FileManager.default.attributesOfItem(atPath: url.path)[.posixPermissions] as? NSNumber)?.intValue
        }
    }

    @Test func turningOnAndOffRestoresTheSettingsExactly() throws {
        let b = try Bridge()
        let original = "{\n  \"env\": {},\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"~/bin/line.sh\",\n"
            + "    \"padding\": 0\n  },\n  \"model\": \"opus\"\n}\n"
        try b.folder.write("claude/settings.json", original)
        try FileManager.default.setAttributes([.posixPermissions: 0o640], ofItemAtPath: b.bridge.settings.path)
        #expect(b.bridge.turnOn(helper: b.helper) == .on)
        let on = try #require(b.settingsText)
        #expect(on.contains("\"command\": \(Self.quoted(b.bridge.command))"))
        #expect(on.contains("\"padding\": 0"))
        #expect(on.hasPrefix("{\n  \"env\": {},\n  \"statusLine\": {"))
        #expect(b.mode(b.bridge.settings) == 0o640)
        #expect(b.mode(b.bridge.sidecar) == 0o600)
        #expect(b.mode(b.bridge.backup) == 0o600)
        #expect(try String(contentsOf: b.bridge.backup, encoding: .utf8) == original)
        #expect(FileManager.default.isExecutableFile(atPath: b.bridge.helperCopy.path))
        #expect(b.bridge.status() == .waiting)

        // Turning on again keeps the sidecar of the user's own line.
        #expect(b.bridge.turnOn(helper: b.helper) == .on)
        #expect(try String(contentsOf: b.bridge.sidecar, encoding: .utf8).contains("~/bin/line.sh"))

        StatusLineHelper.writePrivate(StatusLineQuota(receivedMs: 1, windows: [:]).data(), to: b.bridge.quotaFile)
        #expect(b.bridge.status() == .ok)

        #expect(b.bridge.turnOff())
        #expect(b.settingsText == original)
        #expect(b.mode(b.bridge.settings) == 0o640)
        for url in [b.bridge.sidecar, b.bridge.backup, b.bridge.quotaFile] {
            #expect(!FileManager.default.fileExists(atPath: url.path))
        }
    }

    @Test func aMissingFileGetsTheLineAndLosesItAgain() throws {
        let b = try Bridge()
        #expect(b.bridge.status() == .replaced)
        #expect(b.bridge.turnOn(helper: b.helper) == .on)
        #expect(b.settingsText == "{\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": "
            + Self.quoted(b.bridge.command) + "\n  }\n}\n")
        #expect(b.bridge.turnOff())
        #expect(b.settingsText == "{}\n")
    }

    @Test func aReplacedLineIsLeftAlone() throws {
        let b = try Bridge()
        try b.folder.write("claude/settings.json", "{\"model\": \"opus\"}")
        #expect(b.bridge.turnOn(helper: b.helper) == .on)
        let replaced = "{\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"npx ccstatusline\"\n  }\n}\n"
        try b.folder.write("claude/settings.json", replaced)
        #expect(b.bridge.status() == .replaced)
        b.bridge.turnOff()
        #expect(b.settingsText == replaced)
        #expect(!FileManager.default.fileExists(atPath: b.bridge.sidecar.path))
    }

    @Test func anUnreadableFileIsNeverChanged() throws {
        let b = try Bridge()
        for text in ["[1, 2]\n", "{\"env\": \n", "\u{FEFF}{}"] {
            try b.folder.write("claude/settings.json", text)
            #expect(b.bridge.turnOn(helper: b.helper) == .unreadable)
            #expect(try Data(contentsOf: b.bridge.settings) == Data(text.utf8))
            #expect(b.bridge.status() == .unreadable)
            #expect(!FileManager.default.fileExists(atPath: b.bridge.sidecar.path))
        }
        let latin1 = Data([0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xE9, 0x22, 0x7D])
        try latin1.write(to: b.bridge.settings)
        #expect(b.bridge.turnOn(helper: b.helper) == .unreadable)
        #expect(try Data(contentsOf: b.bridge.settings) == latin1)
    }

    @Test func aSymlinkedFileStaysALink() throws {
        let b = try Bridge()
        let real = try b.folder.write("dotfiles/settings.json", "{\"model\": \"opus\"}\n")
        try FileManager.default.createDirectory(at: b.bridge.settings.deletingLastPathComponent(), withIntermediateDirectories: true)
        try FileManager.default.createSymbolicLink(at: b.bridge.settings, withDestinationURL: real)
        #expect(b.bridge.turnOn(helper: b.helper) == .on)
        let link = try FileManager.default.destinationOfSymbolicLink(atPath: b.bridge.settings.path)
        #expect(link == real.path)
        #expect(try String(contentsOf: real, encoding: .utf8).contains("agent-speed-statusline"))
        b.bridge.turnOff()
        #expect(try String(contentsOf: real, encoding: .utf8) == "{\n  \"model\": \"opus\"\n}\n")
    }

    @Test func theHelperCopyIsReplacedOnlyWhenItDiffers() throws {
        let b = try Bridge()
        #expect(b.bridge.installHelper(from: b.helper))
        let first = try FileManager.default.attributesOfItem(atPath: b.bridge.helperCopy.path)[.systemFileNumber] as? NSNumber
        #expect(b.bridge.installHelper(from: b.helper))
        let same = try FileManager.default.attributesOfItem(atPath: b.bridge.helperCopy.path)[.systemFileNumber] as? NSNumber
        #expect(first == same)
        try b.folder.write("app/agent-speed-statusline", "#!/bin/sh\necho 2\n")
        #expect(b.bridge.installHelper(from: b.helper))
        #expect(try String(contentsOf: b.bridge.helperCopy, encoding: .utf8).contains("echo 2"))
        #expect(b.mode(b.bridge.helperCopy) == 0o755)
    }

    static func quoted(_ text: String) -> String {
        var out = ""
        OrderedJSON.quote(text, into: &out)
        return out
    }

    @Test func orderedJSONKeepsTheDocument() throws {
        let text = "{\n  \"b\": 1.50,\n  \"a\": [\n    true,\n    null,\n    \"é\\n\\u0001\"\n  ],\n  \"c\": {}\n}"
        let value = try #require(OrderedJSON.parse(Data(text.utf8)))
        #expect(value.text() == text)
        #expect(OrderedJSON.parse(Data("{\"a\": 1, \"a\": 2}".utf8))?.text() == "{\n  \"a\": 2\n}")
        #expect(OrderedJSON.parse(Data("{\"a\": 01}".utf8)) == nil)
        #expect(OrderedJSON.parse(Data(String(repeating: "[", count: 70).utf8)) == nil)
    }

    // MARK: Claude Desktop cache (QUOTAS 4.4)

    @Test func theReaderFindsChecksAndDropsCandidates() throws {
        let folder = try TempDirectory()
        let now = Int64(Date().timeIntervalSince1970 * 1000)
        func put(_ fixture: String, as name: String, ageMinutes: Double) throws {
            let url = folder.file(name)
            try Data(try QuotaFixtures.entry(fixture)).write(to: url)
            try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSinceNow: -ageMinutes * 60)],
                                                  ofItemAtPath: url.path)
        }
        try put("other-url.bin", as: "00000000000000a1_0", ageMinutes: 1)
        try put("zstd-ultra.bin", as: "00000000000000a2_0", ageMinutes: 5)
        try put("identity-max.bin", as: "00000000000000a3_0", ageMinutes: 10)
        try put("identity-max.bin", as: "00000000000000a4_0", ageMinutes: 8 * 24 * 60)
        try folder.write("index", "not an entry")
        try folder.write("00000000000000a5_1", "stream 2")

        let reader = DesktopQuotaReader(folder: folder.url)
        let first = try #require(reader.poll(nowMs: now, desktopRunning: true))
        #expect(reader.lastSearchReads == 2)
        #expect(reader.candidateNames == ["00000000000000a2_0"])
        #expect(first.windows.map { $0.label() } == ["7d", "Fable 7d"])
        #expect(first.windows.map(\.usedPercent) == [94, 27])

        // A changed candidate is read again at the next check, 60 s later.
        try put("gzip-max.bin", as: "00000000000000a2_0", ageMinutes: 0)
        #expect(reader.poll(nowMs: now + 1000, desktopRunning: true) == first)
        let changed = try #require(reader.poll(nowMs: now + 61_000, desktopRunning: true))
        #expect(changed.windows.map { $0.label() } == ["5h", "7d", "Opus 7d"])

        // A candidate that disappears is dropped; Desktop starting searches again.
        try FileManager.default.removeItem(at: folder.file("00000000000000a2_0"))
        #expect(reader.poll(nowMs: now + 122_000, desktopRunning: true) == nil)
        reader.requestSearch()
        let found = try #require(reader.poll(nowMs: now + 123_000, desktopRunning: true))
        #expect(reader.candidateNames == ["00000000000000a3_0"])
        #expect(found.windows.first?.kind == .fiveHour)
    }

    @Test func aMissingFolderReadsNothing() {
        let reader = DesktopQuotaReader(folder: URL(fileURLWithPath: "/nonexistent-\(UUID().uuidString)"))
        #expect(reader.poll(nowMs: 0, desktopRunning: true) == nil)
    }

    @Test func entryNamesAndKeys() {
        #expect(DesktopQuotaReader.isEntryName("383706cf5aea4bcc_0"))
        #expect(!DesktopQuotaReader.isEntryName("383706cf5aea4bcc_1"))
        #expect(!DesktopQuotaReader.isEntryName("383706CF5AEA4BCC_0"))
        #expect(!DesktopQuotaReader.isEntryName("index-dir"))
        let organization = "0fba5d0f-43ab-4376-8ca3-f41e01ec4623"
        #expect(DesktopUsage.isUsageKey("1/0/https://claude.ai/api/organizations/\(organization)/usage?skip_spend=1"))
        #expect(DesktopUsage.isUsageKey("1/0/_dk_https://claude.ai https://claude.ai https://claude.ai/api/organizations/\(organization)/usage"))
        #expect(!DesktopUsage.isUsageKey("1/0/https://claude.ai/api/organizations/\(organization)/usage_other"))
        #expect(!DesktopUsage.isUsageKey("1/0/https://claude.ai/api/organizations/not-a-uuid/usage"))
        #expect(!DesktopUsage.isUsageKey("https://evil.example/api/organizations/\(organization)/usage"))
    }

    // MARK: Claude CLI quota file (QUOTAS 5.5, Reader)

    @Test func theCliReaderFollowsTheFile() throws {
        let folder = try TempDirectory()
        var reader = ClaudeCliQuotaReader(file: folder.file("claude-cli.json"))
        let unchanged = reader.refresh()
        #expect(!unchanged)
        var stored = StatusLineQuota()
        stored.merge(rateLimits: ["five_hour": ["used_percentage": 6, "resets_at": 1_794_354_000],
                                  "seven_day": ["used_percentage": 86, "resets_at": 1_794_448_800]], nowMs: 1_794_338_685_000)
        StatusLineHelper.writePrivate(stored.data(), to: reader.file)
        let changed = reader.refresh()
        #expect(changed)
        let snapshot = try #require(reader.snapshot)
        #expect(snapshot.source == .claudeCode)
        #expect(snapshot.observedMs == 1_794_338_685_000)
        #expect(snapshot.windows == [QuotaWindow(kind: .fiveHour, usedPercent: 6, resetsAtMs: 1_794_354_000_000),
                                     QuotaWindow(kind: .sevenDay, usedPercent: 86, resetsAtMs: 1_794_448_800_000)])
        let again = reader.refresh()
        #expect(!again)
        try FileManager.default.removeItem(at: reader.file)
        let removed = reader.refresh()
        #expect(removed)
        #expect(reader.snapshot == nil)
    }

    // MARK: Signed in (QUOTAS 6)

    @Test func signInMarkers() throws {
        let folder = try TempDirectory()
        let cases: [(QuotaSignIn.Marker, String, Bool)] = [
            (.codex, "{\"auth_mode\": \"chatgpt\", \"OPENAI_API_KEY\": null}", true),
            (.codex, "{\"auth_mode\": \"apikey\", \"OPENAI_API_KEY\": \"sk-x\"}", false),
            (.codex, "{\"tokens\": {\"id_token\": \"x\", \"access_token\": \"y\"}}", true),
            (.codex, "{\"tokens\": null}", false),
            (.codex, "{\"tokens\": \"x\", \"auth_mode\": null}", false),
            (.codex, "{\"auth_mode\": null, \"tokens\": {}}", true),
            (.codex, "not json", false),
            (.claudeCode, "{\"numStartups\": 3, \"oauthAccount\": {\"emailAddress\": \"x\"}}", true),
            (.claudeCode, "{\"oauthAccount\": null}", false),
            (.claudeCode, "{\"oauthAccount\": null, \"projects\": {\"a\": [1, {\"b\": null}]}}", false),
            (.claudeCode, "{\"oauthAccount\": [], \"x\": {\"oauthAccount\": {}}}", false),
            (.claudeCode, "{}", false),
        ]
        for (index, (marker, text, expected)) in cases.enumerated() {
            let file = try folder.write("marker-\(index).json", text)
            var signIn = QuotaSignIn(marker, file: file)
            let signedIn = signIn.refresh(nowMs: 0)
            #expect(signedIn == expected, "\(text)")
        }
        var missing = QuotaSignIn(.codex, file: folder.file("none.json"))
        let none = missing.refresh(nowMs: 0)
        #expect(!none)
    }

    @Test func aChangedMarkerIsReadAtMostOnceAMinute() throws {
        let folder = try TempDirectory()
        let file = try folder.write("auth.json", "{\"auth_mode\": \"chatgpt\"}")
        var signIn = QuotaSignIn(.codex, file: file)
        let first = signIn.refresh(nowMs: 0)
        #expect(first)
        try folder.write("auth.json", "{\"auth_mode\": \"apikey\", \"more\": 1}")
        let stillCached = signIn.refresh(nowMs: 10_000)
        #expect(stillCached)
        let reread = signIn.refresh(nowMs: 60_000)
        #expect(!reread)
    }
}
