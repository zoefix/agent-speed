import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// Qoder IDE and Qoder CLI in the app (DESIGN 1, 5, 9): colors, settings rows and location lists, CSV codes and strings.
@Suite("Qoder settings")
@MainActor
struct QoderSettingsTests {
    private static func store() -> UserDefaults {
        let suite = "QoderSettingsTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return defaults
    }

    @Test func settingsSavedBeforeQoderTurnBothOnWithTheirAutomaticEntries() {
        let defaults = Self.store()
        let before = SourceKind.allCases.filter { $0 != .qoder && $0 != .qoderCli }.map(\.rawValue)
        defaults.set(before.filter { $0 != "devin" }, forKey: "enabledSources")
        defaults.set(before, forKey: "knownSources")
        let settings = AppSettings(defaults: defaults)
        #expect(settings.isEnabled(.qoder) && settings.isEnabled(.qoderCli) && !settings.isEnabled(.devin))
        #expect(settings.sourceLocations[.qoder] == [.automatic] && settings.sourceLocations[.qoderCli] == [.automatic])
        #expect(SourceKind.qoder.transcriptFolder == .qoder && SourceKind.qoderCli.transcriptFolder == .qoderCli)
        #expect(TranscriptFolder.allCases.suffix(2) == [.qoder, .qoderCli])
    }

    @Test func theAutomaticEntriesNameBothEditions() {
        let ide = TranscriptFolder.qoder.entry(.automatic)
        #expect(ide.shownURLs == LiveMonitor.defaultQoderRoots && ide.shownURLs.count == 2)
        #expect(ide.shownURLs[0].path.hasSuffix("Application Support/Qoder/SharedClientCache/cache/db"))
        #expect(ide.shownURLs[1].path.hasSuffix("Application Support/QoderCN/SharedClientCache/cache/db"))
        let cli = TranscriptFolder.qoderCli.entry(.automatic)
        #expect(cli.shownURLs == LiveMonitor.defaultQoderCliRoots && cli.shownURLs.count == 2)
        let environment = ProcessInfo.processInfo.environment
        if environment["QODER_CONFIG_DIR"] == nil, environment["QODERCN_CONFIG_DIR"] == nil {
            #expect(cli.shownURLs.map(\.lastPathComponent) == ["projects", "projects"])
            #expect(cli.shownURLs.map { $0.deletingLastPathComponent().lastPathComponent } == [".qoder", ".qoder-cn"])
        }
        #expect(TranscriptFolder.qoder.chooseMessageKey == "mac.settings.sources.chooseQoder")
        #expect(TranscriptFolder.qoderCli.chooseMessageKey == "mac.settings.sources.chooseQoderCli")
        #expect(TranscriptFolder.qoder.footnoteKey == "settings.sources.qoderHint")
        #expect(TranscriptFolder.qoderCli.footnoteKey == "settings.sources.qoderCliHint")
        #expect(!TranscriptFolder.qoder.acceptsDatabaseFile && !TranscriptFolder.qoderCli.acceptsDatabaseFile)
    }

    @Test func aQoderFolderCountsWhenItNamesTheDatabase() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("qoder-settings-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }
        let ide = root.appendingPathComponent("Qoder", isDirectory: true)
        let db = ide.appendingPathComponent("SharedClientCache/cache/db", isDirectory: true)
        try FileManager.default.createDirectory(at: db, withIntermediateDirectories: true)
        #expect(!TranscriptFolder.qoder.locationExists(ide), "an IDE folder without its database holds nothing to read")
        try Data().write(to: db.appendingPathComponent("local.db"))
        for folder in [ide, ide.appendingPathComponent("SharedClientCache"), db] {
            #expect(TranscriptFolder.qoder.locationExists(folder) && TranscriptFolder.qoder.entry(.path(folder.path)).exists,
                    "\(folder.path)")
        }
        let state = ide.appendingPathComponent("User/globalStorage", isDirectory: true)
        try FileManager.default.createDirectory(at: state, withIntermediateDirectories: true)
        try Data().write(to: state.appendingPathComponent("state.vscdb"))
        #expect(!TranscriptFolder.qoder.locationExists(state), "the editor's state database is not read")
        let config = root.appendingPathComponent(".qoder", isDirectory: true)
        try FileManager.default.createDirectory(at: config, withIntermediateDirectories: true)
        #expect(TranscriptFolder.qoderCli.locationExists(config))
    }

    @Test func colorsCodesAndStrings() {
        #expect(SourceKind.qoder.tint == RGBColor(hex: 0xB3329D) && SourceKind.qoderCli.tint == RGBColor(hex: 0x7E61B6))
        #expect(HistoryCSV.sourceID(.qoder) == "qoder" && HistoryCSV.sourceID(.qoderCli) == "qoder-cli")
        for language in [LanguagePreference.zhHant, .zhHans, .en, .ja] {
            let l10n = Localizer(preference: language)
            #expect(l10n("source.qoder") == "Qoder IDE" && l10n("source.qoderCli") == "Qoder CLI", "\(language)")
            for key in ["settings.sources.qoderHint", "mac.settings.sources.chooseQoder"] {
                #expect(l10n(key) != key && l10n(key).contains("Qoder IDE") && l10n(key).contains("local.db"),
                        "\(key) \(language)")
            }
            for key in ["settings.sources.qoderCliHint", "mac.settings.sources.chooseQoderCli"] {
                #expect(l10n(key) != key && l10n(key).contains("Qoder CLI") && l10n(key).contains(".qoder"),
                        "\(key) \(language)")
            }
            #expect(l10n("settings.sources.qoderCliHint").contains("QODER_EXPOSE_TOKEN_USAGE=1"), "\(language)")
        }
        #expect(Localizer(preference: .zhHant)("mac.settings.sources.chooseQoder").contains("資料夾"))
        #expect(Localizer(preference: .zhHans)("mac.settings.sources.chooseQoderCli").contains("文件夹"))
    }
}
