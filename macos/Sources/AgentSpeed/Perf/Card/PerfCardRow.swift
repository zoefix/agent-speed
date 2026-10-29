import SwiftUI

/// Makes a SwiftUI row the card's owner (DESIGN 12, *Score card*): hover shows it after the presenter's delay, Space
/// or the custom action `a11y.perf.card` shows it at once, it follows the focus while shown, Esc hides it. The card's
/// content is the row's extra accessibility content (VoiceOver's "more content"), in the card's order.
private struct PerfCardRowModifier: ViewModifier {
    let presenter: PerfCardPresenter
    let key: String
    let lines: [String]
    let focused: Bool
    let l10n: Localizer
    let provider: PerfCardPresenter.Provider

    func body(content: Content) -> some View {
        lines.enumerated().reduce(AnyView(content)) { view, entry in
            AnyView(view.accessibilityCustomContent(
                AccessibilityCustomContentKey(Text(l10n("a11y.perf.card")), id: "perf.card.\(entry.offset)"),
                Text(entry.element),
                importance: entry.offset == 0 ? .high : .default
            ))
        }
        .onHover { inside in
            if inside { presenter.enter(key, provider: provider) } else { presenter.exit(key) }
        }
        .onKeyPress(.space) {
            guard focused else { return .ignored }
            presenter.showNow(key, provider: provider)
            return .handled
        }
        .onKeyPress(.escape) {
            guard focused, presenter.isShown else { return .ignored }
            presenter.dismiss()
            return .handled
        }
        .onChange(of: focused) { _, now in
            if now { presenter.focus(key, provider: provider) }
        }
        .accessibilityAction(named: Text(l10n("a11y.perf.card"))) {
            presenter.showNow(key, provider: provider)
        }
    }
}

extension View {
    /// The score card of this row; `lines` from `PerfCardText.accessibilityLines`.
    func perfCard(_ presenter: PerfCardPresenter, key: String, lines: [String], focused: Bool, l10n: Localizer,
                  provider: @escaping PerfCardPresenter.Provider) -> some View {
        modifier(PerfCardRowModifier(presenter: presenter, key: key, lines: lines, focused: focused, l10n: l10n,
                                     provider: provider))
    }
}
