import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// Devin in the app (DESIGN 1, 5, 9): its color, its settings row and location list, its CSV code and its strings.
@Suite("Devin settings")
@MainActor
struct DevinSettingsTests {
    private static func store() -> UserDefaults {
        let suite = "DevinSettingsTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return defaults
    }

    @Test func settingsSavedBeforeDevinTurnItOnWithItsAutomaticEntry() {
        let defaults = Self.store()
        let before = SourceKind.allCases.filter { $0 != .devin }.map(\.rawValue)
        defaults.set(before.filter { $0 != "hermes" }, forKey: "enabledSources")
        defaults.set(before, forKey: "knownSources")
        let settings = AppSettings(defaults: defaults)
        #expect(settings.isEnabled(.devin) && !settings.isEnabled(.hermes))
        #expect(settings.sourceLocations[.devin] == [.automatic])
        #expect(SourceKind.devin.transcriptFolder == .devin
            && TranscriptFolder.allCases.suffix(3) == [.devin, .qoder, .qoderCli])
    }

    @Test func theAutomaticEntryNamesTheCliFolderOnly() {
        let entry = TranscriptFolder.devin.entry(.automatic)
        #expect(entry.shownURLs == [LiveMonitor.defaultDevinRoot])
        #expect(entry.shownURLs[0].path.hasSuffix("devin/cli") || ProcessInfo.processInfo.environment["CHISEL_SESSION_DB"] != nil)
        #expect(TranscriptFolder.devin.chooseMessageKey == "mac.settings.sources.chooseDevin")
        #expect(TranscriptFolder.devin.footnoteKey == "settings.sources.devinHint")
        #expect(!TranscriptFolder.devin.acceptsDatabaseFile)
    }

    @Test func aFolderCountsWhenItHoldsTheDatabase() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("devin-settings-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let cli = root.appendingPathComponent("cli", isDirectory: true)
        try FileManager.default.createDirectory(at: cli, withIntermediateDirectories: true)
        #expect(!TranscriptFolder.devin.locationExists(cli), "an empty folder holds nothing Devin wrote")
        try Data().write(to: cli.appendingPathComponent("sessions.db"))
        #expect(TranscriptFolder.devin.locationExists(cli) && TranscriptFolder.devin.entry(.path(cli.path)).exists)
        #expect(TranscriptFolder.devin.locationExists(root), "the devin folder above cli")
        let desktop = root.appendingPathComponent("Devin/User/acp-events", isDirectory: true)
        try FileManager.default.createDirectory(at: desktop, withIntermediateDirectories: true)
        #expect(!TranscriptFolder.devin.locationExists(root.appendingPathComponent("Devin")), "Devin Desktop is not read")
    }

    @Test func colorCodeAndStrings() {
        #expect(SourceKind.devin.tint == RGBColor(hex: 0x557F14))
        #expect(HistoryCSV.sourceID(.devin) == "devin")
        for language in [LanguagePreference.zhHant, .zhHans, .en, .ja] {
            let l10n = Localizer(preference: language)
            #expect(l10n("source.devin") == "Devin", "\(language)")
            for key in ["settings.sources.devinHint", "mac.settings.sources.chooseDevin"] {
                #expect(l10n(key) != key && l10n(key).contains("Devin"), "\(key) \(language)")
            }
        }
        #expect(Localizer(preference: .zhHant)("settings.sources.devinHint").contains("工作階段"))
        for language in [LanguagePreference.zhHant, .zhHans, .en, .ja] {
            let hint = Localizer(preference: language)("settings.sources.devinHint")
            #expect(!hint.contains("Desktop") && !hint.contains("acp-events"), "\(language)")
        }
    }
}
