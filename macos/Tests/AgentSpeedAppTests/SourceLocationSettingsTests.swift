import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// DESIGN 5, 資料來源: the location lists in Settings — migration from the single folder of earlier versions, storage,
/// editing and what each row shows (issue #45).
@Suite("Source location settings")
@MainActor
struct SourceLocationSettingsTests {
    private static func store() -> UserDefaults {
        let suite = "SourceLocationSettingsTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return defaults
    }

    @Test func anEarlierSingleFolderBecomesTheWholeList() {
        let defaults = Self.store()
        defaults.set("/Volumes/work/codex/sessions", forKey: "codexRoot")
        defaults.set("/Users/dev/.openclaw/agents/main/agent/openclaw-agent.sqlite", forKey: "openclawRoot")
        defaults.set("", forKey: "claudeRoot")
        let settings = AppSettings(defaults: defaults)
        #expect(settings.sourceLocations[.codex] == [.path("/Volumes/work/codex/sessions")], "the same folders as before")
        #expect(settings.sourceLocations[.openclaw] == [.path("/Users/dev/.openclaw/agents/main/agent/openclaw-agent.sqlite")])
        #expect(settings.sourceLocations[.claude] == [.automatic])
        #expect(settings.monitorConfiguration.locations == settings.sourceLocations)
        #expect(defaults.data(forKey: "sourceLocations") == nil, "migrating writes nothing")
        #expect(AppSettings.legacyKey(.workbuddyAi) == "workbuddyAiRoot" && AppSettings.legacyKey(.grokBuild) == "grokBuildRoot")
    }

    @Test func theOldKeysAreNeverWrittenSoADowngradeKeepsItsFolder() {
        let defaults = Self.store()
        defaults.set("/Volumes/work/codex/sessions", forKey: "codexRoot")
        let settings = AppSettings(defaults: defaults)
        TranscriptFolder.codex.add(URL(fileURLWithPath: "/Volumes/other/sessions", isDirectory: true), in: settings)
        TranscriptFolder.dsh.add(URL(fileURLWithPath: "/Volumes/dsh", isDirectory: true), in: settings)
        #expect(defaults.string(forKey: "codexRoot") == "/Volumes/work/codex/sessions")
        #expect(defaults.string(forKey: "dshRoot") == nil)
        let reloaded = AppSettings(defaults: defaults)
        #expect(reloaded.sourceLocations[.codex] == [.path("/Volumes/work/codex/sessions"), .path("/Volumes/other/sessions")])
        #expect(reloaded.sourceLocations[.dsh] == [.automatic, .path("/Volumes/dsh")])
        TranscriptFolder.codex.reset(in: reloaded)
        #expect(AppSettings(defaults: defaults).sourceLocations[.codex] == [.automatic],
                "once the new value exists the old key is not read again")
    }

    @Test func editingFollowsTheListRules() {
        let defaults = Self.store()
        let settings = AppSettings(defaults: defaults)
        let folder = TranscriptFolder.claude
        let work = URL(fileURLWithPath: "/Volumes/work/claude/projects", isDirectory: true)
        #expect(folder.add(work, in: settings) == .added)
        #expect(folder.add(URL(fileURLWithPath: "/Volumes/work/claude/projects/", isDirectory: true), in: settings) == .alreadyListed)
        #expect(folder.add(LiveMonitor.defaultClaudeRoot, in: settings) == .alreadyAutomatic)
        folder.remove(.automatic, in: settings)
        #expect(settings.sourceLocations[.claude] == [.path(work.path)])
        folder.remove(.path(work.path), in: settings)
        #expect(settings.sourceLocations[.claude] == [.path(work.path)], "the only entry stays")
        #expect(folder.add(LiveMonitor.defaultClaudeRoot, in: settings) == .restoredAutomatic)
        #expect(AppSettings(defaults: defaults).sourceLocations[.claude] == [.automatic, .path(work.path)])
        for index in 0..<6 { folder.add(URL(fileURLWithPath: "/Volumes/w\(index)", isDirectory: true), in: settings) }
        #expect(settings.sourceLocations[.claude].count == SourceLocations.maxEntries)
        #expect(folder.add(URL(fileURLWithPath: "/Volumes/w9", isDirectory: true), in: settings) == .full)
        folder.reset(in: settings)
        #expect(AppSettings(defaults: defaults).sourceLocations[.claude] == [.automatic])
    }

    @Test func rowsShowEachEntry() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent("agent-speed-rows-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let settings = AppSettings(defaults: Self.store())
        let missing = URL(fileURLWithPath: "/nonexistent/codex/sessions", isDirectory: true)
        TranscriptFolder.codex.add(directory, in: settings)
        TranscriptFolder.codex.add(missing, in: settings)
        let entries = TranscriptFolder.codex.entries(in: settings)
        #expect(entries.map(\.isAutomatic) == [true, false, false])
        #expect(entries[0].shownURLs == [LiveMonitor.defaultCodexRoot])
        #expect(entries[1].exists && entries[1].revealURL?.path == directory.standardizedFileURL.path)
        #expect(!entries[2].exists && entries[2].revealURL == nil, "a missing location is shown, dimmed, without Show in Finder")
        #expect(TranscriptFolder.gemini.entry(.automatic).shownURLs == LiveMonitor.defaultGeminiRoots,
                "the automatic entry keeps naming both Gemini CLI folders")
        #expect(TranscriptFolder.omp.entry(.automatic).shownURLs == [LiveMonitor.defaultOmpRoot])
        #expect(TranscriptFolder.openclaw.acceptsDatabaseFile && !TranscriptFolder.hermes.acceptsDatabaseFile)
    }

    @Test func theNewStringsExistInEveryLanguage() {
        for language in [LanguagePreference.zhHant, .zhHans, .en, .ja] {
            let l10n = Localizer(preference: language)
            for key in ["settings.sources.add", "settings.sources.default", "settings.sources.remove",
                        "settings.sources.keepOne", "settings.sources.alreadyListed", "settings.sources.alreadyDefault",
                        "settings.sources.locationsHint"] {
                #expect(l10n(key) != key, "\(key) \(language)")
            }
            #expect(l10n("settings.sources.limit", 8).contains("8"))
            #expect(l10n("settings.sources.removeLocation", "~/work").contains("~/work"))
        }
    }
}
