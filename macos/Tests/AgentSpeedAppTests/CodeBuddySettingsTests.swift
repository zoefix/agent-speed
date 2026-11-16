import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// CodeBuddy Code in the app (DESIGN 1, 5, 9): color, settings row and location list, CSV code and strings.
@Suite("CodeBuddy Code settings")
@MainActor
struct CodeBuddySettingsTests {
    private static func store() -> UserDefaults {
        let suite = "CodeBuddySettingsTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return defaults
    }

    @Test func settingsSavedBeforeCodeBuddyTurnItOnWithItsAutomaticEntry() {
        let defaults = Self.store()
        let before = SourceKind.allCases.filter { $0 != .codebuddyCode }.map(\.rawValue)
        defaults.set(before.filter { $0 != "devin" }, forKey: "enabledSources")
        defaults.set(before, forKey: "knownSources")
        let settings = AppSettings(defaults: defaults)
        #expect(settings.isEnabled(.codebuddyCode) && !settings.isEnabled(.devin))
        #expect(settings.sourceLocations[.codebuddyCode] == [.automatic])
        #expect(TranscriptFolder.allCases.last == .codebuddyCode && SourceKind.allCases.last == .codebuddyCode)
    }

    @Test func theAutomaticEntryNamesTheProjectsFolder() {
        let entry = TranscriptFolder.codebuddyCode.entry(.automatic)
        #expect(entry.shownURLs == [LiveMonitor.defaultCodebuddyCodeRoot])
        if ProcessInfo.processInfo.environment["CODEBUDDY_CONFIG_DIR"] == nil {
            #expect(entry.shownURLs[0].path == FileManager.default.homeDirectoryForCurrentUser
                .appendingPathComponent(".codebuddy/projects").path)
        }
        #expect(TranscriptFolder.codebuddyCode.chooseMessageKey == "mac.settings.sources.chooseCodebuddyCode")
        #expect(TranscriptFolder.codebuddyCode.footnoteKey == "settings.sources.codebuddyCodeHint")
        #expect(!TranscriptFolder.codebuddyCode.acceptsDatabaseFile && !TranscriptFolder.codebuddyCode.automaticIsWiderThanDefault)
    }

    @Test func aConfigFolderAndItsProjectsFolderAreOneLocation() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("codebuddy-settings-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let config = root.appendingPathComponent(".codebuddy", isDirectory: true)
        try FileManager.default.createDirectory(at: config.appendingPathComponent("projects"), withIntermediateDirectories: true)
        var locations = SourceLocations()
        #expect(locations.add(config.path, to: .codebuddyCode) == .added)
        #expect(locations.add(config.appendingPathComponent("projects").path, to: .codebuddyCode) == .alreadyListed)
        #expect(TranscriptFolder.codebuddyCode.locationExists(config))
    }

    @Test func colorCodeAndStrings() {
        #expect(SourceKind.codebuddyCode.tint == RGBColor(hex: 0x18AB06))
        #expect(HistoryCSV.sourceID(.codebuddyCode) == "codebuddy-code")
        for language in [LanguagePreference.zhHant, .zhHans, .en, .ja] {
            let l10n = Localizer(preference: language)
            #expect(l10n("source.codebuddyCode") == "CodeBuddy Code", "\(language)")
            for key in ["settings.sources.codebuddyCodeHint", "mac.settings.sources.chooseCodebuddyCode"] {
                #expect(l10n(key) != key && l10n(key).contains("CodeBuddy Code") && l10n(key).contains(".codebuddy"),
                        "\(key) \(language)")
            }
        }
    }
}
