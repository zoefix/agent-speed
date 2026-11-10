import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// `--render-previews` group `sources`: Settings → Data Sources and the dashboard's sources list with every source,
/// both WorkBuddy editions included, in each language and appearance (`sources-<surface>-<language>-<scheme>.png`),
/// and the editions' orbs with bezels, dots and name plates (`sources-orbs-<language>-<scheme>.png`).
@MainActor
enum SourcePreviews {
    static let languages: [LanguagePreference] = [.zhHant, .zhHans, .en, .ja]

    static func render(model: AppModel, into directory: URL) {
        let original = model.settings.language
        PreviewRenderer.publish(model, speed: model.settings.redline * 0.4, agents: .crowd(27))
        for language in languages {
            model.settings.language = language
            model.l10n.apply(language)
            for dark in [false, true] {
                let suffix = "\(language.rawValue)-\(dark ? "dark" : "light").png"
                PreviewSnapshot.writeHosted(
                    SettingsPaneView(pane: .sources, model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                    dark: dark,
                    to: directory.appendingPathComponent("sources-settings-\(suffix)")
                )
                PreviewSnapshot.writeHosted(
                    DashboardView(model: model).appEnvironment(model).environment(\.orbStillTime, Clock.now),
                    dark: dark,
                    to: directory.appendingPathComponent("sources-dashboard-\(suffix)")
                )
                orbs(model: model, dark: dark)?.write(to: directory.appendingPathComponent("sources-orbs-\(suffix)"))
            }
        }
        model.settings.language = original
        model.l10n.apply(original)
    }

    /// The newest harnesses, a mixed set and DSH in every style.
    private static func orbs(model: AppModel, dark: Bool) -> NameplatePreviewSheet? {
        guard let sheet = NameplatePreviewSheet(model: model, size: CGSize(width: 840, height: 300), dark: dark, seed: 23) else {
            return nil
        }
        let entries: [(OrbStyleKind, [SourceKind])] = [
            (.liquid, [.droid]), (.needle, [.hermes]), (.led, [.openclaw]),
            (.reactor, [.codex, .claudeCode, .hermes, .droid, .devin, .qoder, .qoderCli]), (.minimal, [.deepseek]),
        ]
        for (index, entry) in entries.enumerated() {
            let config = OrbConfig(id: UUID().uuidString, sources: .explicit(entry.1), style: entry.0, size: 148)
            sheet.add(.init(config: config, center: CGPoint(x: 94 + 164 * CGFloat(index), y: 112), fraction: 0.45 + 0.08 * Double(index)))
        }
        sheet.drawOrbs()
        sheet.drawPlates()
        return sheet
    }
}
