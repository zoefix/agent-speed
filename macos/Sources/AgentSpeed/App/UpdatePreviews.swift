import AgentSpeedCore
import SwiftUI

/// Update window, dashboard and settings renders for `--render-previews`.
@MainActor
enum UpdatePreviews {
    static let release = Release(
        tag: "v1.2.0",
        version: SemanticVersion(major: 1, minor: 2, patch: 0),
        name: "Agent Speed 1.2.0",
        notes: """
        ## What's New
        - **Update checks**: Agent Speed tells you when a new version is out and installs it with one click.
        - The orb's comet tail is smoother at redline.
          - Sparks follow the comet direction.
        - Fixed sub-agent speeds that were counted twice in rare cases.

        ## Fixes
        1. The dashboard no longer jumps when a source appears.
        2. `tok/s` in the menu bar keeps its width when idle.

        > Checksums protect against corrupted downloads.

        **Full Changelog**: [v1.1.0...v1.2.0](https://github.com/zoefix/agent-speed/compare/v1.1.0...v1.2.0)
        """,
        pageURL: URL(string: "https://github.com/zoefix/agent-speed/releases/tag/v1.2.0"),
        publishedAt: Date(timeIntervalSince1970: 1_792_540_800)
    )

    static func render(model: AppModel, into directory: URL) {
        let checked = Date().addingTimeInterval(-12 * 60)
        let states: [(String, UpdateController.Status, UpdateController.Installation)] = [
            ("available", .available(release), .idle),
            ("downloading", .available(release), .downloading(0.42)),
            ("verifying", .available(release), .verifying),
            ("installing", .available(release), .installing),
            ("relaunching", .available(release), .relaunching),
            ("failed", .available(release), .failed(.checksum)),
            ("checking", .checking, .idle),
            ("uptodate", .upToDate, .idle),
            ("checkfailed", .failed(.network), .idle),
        ]
        for (name, status, installation) in states {
            model.updates.preview(status: status, installation: installation, lastChecked: checked)
            for dark in [false, true] {
                write(model, UpdateView(model: model) {}, dark: dark, name: "update-\(name)", directory: directory)
            }
        }

        model.updates.preview(status: .available(release), lastChecked: checked, justUpdatedTo: "1.1.0")
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.3)
        for dark in [false, true] {
            write(model, DashboardView(model: model).environment(\.orbStillTime, Clock.now), dark: dark, name: "update-dashboard", directory: directory)
            write(model, SettingsPaneView(pane: .about, model: model), dark: dark, name: "update-settings-available", directory: directory)
        }
        model.updates.preview(status: .upToDate, lastChecked: checked)
        write(model, SettingsPaneView(pane: .about, model: model), dark: false, name: "update-settings-uptodate", directory: directory)
        model.updates.preview(status: .available(release), installation: .downloading(0.64), lastChecked: checked)
        write(model, SettingsPaneView(pane: .about, model: model), dark: true, name: "update-settings-downloading", directory: directory)

        let language = model.settings.language
        for (code, preference) in [("zh-Hant", LanguagePreference.zhHant), ("ja", .ja)] {
            model.settings.language = preference
            model.l10n.apply(preference)
            model.updates.preview(status: .available(release), lastChecked: checked)
            write(model, UpdateView(model: model) {}, dark: false, name: "update-available-\(code)", directory: directory)
            model.updates.preview(status: .available(release), installation: .failed(.permission), lastChecked: checked)
            write(model, UpdateView(model: model) {}, dark: true, name: "update-failed-\(code)", directory: directory)
            write(model, SettingsPaneView(pane: .about, model: model), dark: false, name: "update-settings-\(code)", directory: directory)
        }
        model.settings.language = language
        model.l10n.apply(language)
        model.updates.preview(status: .idle)
    }

    private static func write<V: View>(_ model: AppModel, _ view: V, dark: Bool, name: String, directory: URL) {
        PreviewSnapshot.writeHosted(
            view.appEnvironment(model),
            dark: dark,
            to: directory.appendingPathComponent("\(name)-\(dark ? "dark" : "light").png")
        )
    }
}
