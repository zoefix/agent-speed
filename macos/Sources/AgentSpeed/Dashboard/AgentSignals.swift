import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// The agent row's lines about how it works (DESIGN 4, Looping mark) and its performance score's words (DESIGN 4, 性能
/// column). Descriptive only: they never change the row's order or value.
@MainActor
enum AgentSignalText {
    /// `work.looping` while the agent goes in circles: the row's first tooltip line and part of its label.
    static func looping(_ signals: WorkSignals, _ l10n: Localizer) -> String? {
        signals.looping.map { l10n("work.looping", $0) }
    }

    /// The tooltip's closing lines: `work.repeating` while it repeats without failures, then `work.summary` once it has
    /// enough recent actions.
    static func closing(_ signals: WorkSignals, _ l10n: Localizer) -> [String] {
        var lines: [String] = []
        if let count = signals.repeating { lines.append(l10n("work.repeating", count)) }
        if let novelty = signals.novelty {
            lines.append(l10n("work.summary", HistoryText.percent(novelty), signals.failed, signals.recovered))
        }
        return lines
    }

    /// Read right after the live value (DESIGN 4, 性能 column): `a11y.agent.intel` with the score words of DESIGN 12
    /// (`性能 1,286`, `性能 912，初步（樣本少）`, `性能 1,540，閒置中`); nothing while none.
    static func intelligenceAccessibility(_ reading: PerfSetReading, _ l10n: Localizer) -> String? {
        PerfScoreText.words(reading, l10n).map { l10n("a11y.agent.intel", $0) }
    }

    /// A share as a whole percent, rounded half away from zero.
    static func percent(_ share: Double) -> String {
        "\(Int((share.clamped(to: 0...1) * 100).rounded()))%"
    }
}

/// The looping mark (DESIGN 4): an amber `arrow.triangle.2.circlepath`, 11 pt, before the project name. The row's
/// label carries `work.looping`, so the glyph itself is decoration.
struct LoopingMark: View {
    static let fadeIn: TimeInterval = 0.25
    static let fadeOut: TimeInterval = 0.6
    static let gap: CGFloat = 4

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let dark = colorScheme == .dark
        Image(systemName: "arrow.triangle.2.circlepath")
            .font(.system(size: 11, weight: .semibold))
            .foregroundStyle(DashboardPalette.legible(DashboardPalette.amber(dark: dark), dark: dark,
                                                      minimum: contrast == .increased ? 4.5 : 3).color)
            .accessibilityHidden(true)
    }

    /// Fades in quickly and out slowly; Reduce Motion shows and hides it at once.
    static func animation(appearing: Bool, reduceMotion: Bool) -> Animation? {
        reduceMotion ? nil : appearing ? .easeOut(duration: fadeIn) : .easeIn(duration: fadeOut)
    }
}
