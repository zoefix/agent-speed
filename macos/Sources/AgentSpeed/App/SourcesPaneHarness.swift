#if DEBUG
import AgentSpeedCore
import AppKit

/// `--screenshots DIR` group `sources` (debug builds): the real Settings window on 資料來源 with Codex's location list in
/// four states — the automatic entry alone, several entries, a missing one, and the 8-entry limit — in light and dark
/// (DESIGN 5, issue #45). The added folders are created under `DIR/source-folders`; nothing else is written.
@MainActor
enum SourcesPaneHarness {
    static func run(model: AppModel, into directory: URL) async {
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.3)
        let folders = directory.appendingPathComponent("source-folders", isDirectory: true)
        func folder(_ relative: String) -> DataLocation {
            let url = folders.appendingPathComponent(relative, isDirectory: true)
            try? FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            return .path(url.standardizedFileURL.path)
        }
        let company = folder("company-cli/sessions")
        let work = folder("work/codex/sessions")
        let missing = DataLocation.path("/Volumes/Archive/codex/sessions")
        let more = (1...4).map { folder("team-\($0)/codex/sessions") }
        let states: [(name: String, codex: [DataLocation])] = [
            ("one", [.automatic]),
            ("several", [.automatic, company, work]),
            ("missing", [company, missing]),
            ("limit", [.automatic, company, work, missing] + more),
        ]
        let saved = model.settings.sourceLocations
        defer { model.settings.sourceLocations = saved }
        let settings = SettingsWindowController(model: model)
        for dark in [false, true] {
            NSApp.appearance = NSAppearance(named: dark ? .darkAqua : .aqua)
            for state in states {
                var locations = SourceLocations()
                locations[.codex] = state.codex
                model.settings.sourceLocations = locations
                settings.show(pane: .sources)
                let window = NSApp.windows.first { $0 is SettingsWindow }
                await ScreenshotHarness.bringForward(window)
                try? await Task.sleep(for: .seconds(1.0))
                ScreenshotHarness.capture(NSApp.windows.first { $0 is SettingsWindow && $0.isVisible }, "sources-\(state.name)",
                                          dark: dark, into: directory)
            }
            NSApp.windows.first { $0 is SettingsWindow }?.orderOut(nil)
        }
    }
}
#endif
