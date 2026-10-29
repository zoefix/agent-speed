import AgentSpeedCore
import AppKit
import SwiftUI

/// The dashboard's score cards (DESIGN 4, 性能 column; DESIGN 12, *Score card*): an agent row's tooltip is its card,
/// shown beside the panel toward the screen's centre after 0.45 s, updated on every engine tick, gone with the panel.
@MainActor
final class DashboardScoreCards {
    let presenter: PerfCardPresenter
    private let panel: PerfCardPanel
    private let model: AppModel
    private var loop: ObservationLoop?

    init(model: AppModel) {
        self.model = model
        let panel = PerfCardPanel(l10n: model.l10n)
        self.panel = panel
        presenter = PerfCardPresenter(display: panel, delay: PerfCardPresenter.dashboardDelay) { [weak model] in
            (model?.accessibility.reduceMotion ?? false, model?.accessibility.reduceTransparency ?? false)
        }
        loop = ObservationLoop { [weak self] in
            guard let self else { return }
            _ = self.model.store.snapshot
            self.presenter.refresh()
        }
    }

    /// The panel closed: the card goes with it.
    func close() {
        presenter.dismiss()
        panel.close()
        loop?.cancel()
        loop = nil
    }

    /// The card of the agent `id` beside the window holding `row`, its top level with the row's top.
    func request(agentId id: String, row: NSView?) -> PerfCardRequest? {
        guard let row, let window = row.window,
              let agent = model.store.snapshot.agents.first(where: { $0.id == id }) else { return nil }
        let frame = window.convertToScreen(row.convert(row.bounds, to: nil))
        let visible = (window.screen ?? NSScreen.main)?.visibleFrame ?? window.frame
        let card = PerfCardModel(agent: agent, facts: AgentRow.facts(of: agent, model: model))
        return PerfCardRequest(model: card, anchor: PerfCardAnchor(row: frame, container: window.frame,
                                                                             visibleFrame: visible,
                                                                             side: .towardScreenCenter),
                                    window: window)
    }
}

extension EnvironmentValues {
    /// Set by the dashboard's popover; nil elsewhere (previews), where a row keeps a text tooltip.
    @Entry var dashboardScoreCards: DashboardScoreCards?
}

/// A weak handle on the AppKit view behind a SwiftUI row, so its screen frame is read only when a card is shown.
@MainActor
final class ScreenAnchorBox {
    weak var view: NSView?
}

/// An invisible view filling the row it backs, recording itself in `box`.
struct ScreenAnchor: NSViewRepresentable {
    let box: ScreenAnchorBox

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        box.view = view
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        if box.view !== view { box.view = view }
    }
}
