import AgentSpeedCore
import AppKit
import SwiftUI

/// `--render-previews` group `perfcard`: the score card in every case of the prototype's gallery (DESIGN 12) on its
/// solid surface (the Reduce Transparency look; the material itself needs a real window, see `--screenshots` group
/// `perfcard`). One sheet per language and appearance, Increase Contrast, the fit steps, and each card alone in Chinese.
@MainActor
enum PerfCardPreviews {
    static func render(model: AppModel, into directory: URL) {
        let saved = model.settings.language
        defer {
            model.settings.language = saved
            model.l10n.apply(saved)
        }
        for language in [LanguagePreference.zhHant, .en, .ja] {
            model.settings.language = language
            model.l10n.apply(language)
            let cases = PerfPreviewData.cards(model.l10n)
            for dark in language == .ja ? [true] : [false, true] {
                let suffix = "\(language.rawValue)-\(dark ? "dark" : "light")"
                write(Sheet(cases: cases, l10n: model.l10n, dark: dark), dark: dark,
                      to: directory.appendingPathComponent("perfcard-sheet-\(suffix).png"))
                guard language == .zhHant else { continue }
                if let live = cases.first(where: { $0.name == "live" }) {
                    // DESIGN 12, *Fit*: full, without the meanings, without the footnote too.
                    write(HStack(alignment: .top, spacing: 24) {
                        ForEach(PerfCardFit.allCases, id: \.self) { fit in
                            PerfCardView(model: live.model, l10n: model.l10n, fit: fit)
                                .modifier(PerfCardSolidSurface())
                        }
                    }.padding(28), dark: dark, to: directory.appendingPathComponent("perfcard-fit-\(suffix).png"))
                }
                write(Sheet(cases: cases, l10n: model.l10n, dark: dark).environment(\._colorSchemeContrast, .increased),
                      dark: dark, to: directory.appendingPathComponent("perfcard-sheet-contrast-\(suffix).png"))
                for item in cases {
                    write(Card(model: item.model, l10n: model.l10n, dark: dark).padding(28), dark: dark,
                          to: directory.appendingPathComponent("perfcard-\(item.name)-\(suffix).png"))
                }
            }
        }
    }

    private static func write<V: View>(_ view: V, dark: Bool, to url: URL) {
        PreviewSnapshot.writeHosted(view, dark: dark, background: desk(dark: dark), to: url)
    }

    /// The prototype's desk behind the cards.
    static func desk(dark: Bool) -> NSColor {
        dark ? NSColor(srgbRed: 0x12 / 255, green: 0x13 / 255, blue: 0x18 / 255, alpha: 1)
            : NSColor(srgbRed: 0xE6 / 255, green: 0xE8 / 255, blue: 0xEE / 255, alpha: 1)
    }

    private struct Card: View {
        let model: PerfCardModel
        let l10n: Localizer
        let dark: Bool

        var body: some View {
            PerfCardView(model: model, l10n: l10n)
                .modifier(PerfCardSolidSurface())
                .shadow(color: .black.opacity(dark ? 0.5 : 0.16), radius: 18, y: 10)
                .shadow(color: .black.opacity(dark ? 0.3 : 0.08), radius: 3, y: 1)
        }
    }

    private struct Sheet: View {
        let cases: [(name: String, model: PerfCardModel)]
        let l10n: Localizer
        let dark: Bool

        var body: some View {
            let rows = stride(from: 0, to: cases.count, by: 4).map { Array(cases[$0..<min($0 + 4, cases.count)]) }
            Grid(alignment: .topLeading, horizontalSpacing: 24, verticalSpacing: 28) {
                ForEach(Array(rows.enumerated()), id: \.offset) { _, row in
                    GridRow(alignment: .top) {
                        ForEach(row, id: \.name) { item in
                            VStack(alignment: .leading, spacing: 8) {
                                Text(item.name.uppercased())
                                    .font(.system(size: 10.5, weight: .semibold))
                                    .kerning(0.8)
                                    .foregroundStyle(.secondary)
                                Card(model: item.model, l10n: l10n, dark: dark)
                            }
                        }
                    }
                }
            }
            .padding(32)
        }
    }
}
