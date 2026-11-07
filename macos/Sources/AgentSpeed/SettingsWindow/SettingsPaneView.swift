import SwiftUI

/// One settings pane at the window's fixed width and its natural height.
struct SettingsPaneView: View {
    let pane: SettingsPane
    let model: AppModel

    /// Wide enough for all ten toolbar tabs in every language (the Japanese labels need 628 pt).
    static let width: CGFloat = 640
    /// The floating orb pane lists live orbs and five style cards in a row; General holds three columns of title
    /// theme cards and the custom set editor beside its previews (DESIGN 5: content up to 680 pt).
    static let wideWidth: CGFloat = 680

    static func width(for pane: SettingsPane) -> CGFloat {
        pane == .orb || pane == .general ? wideWidth : width
    }

    var body: some View {
        content
            .frame(width: Self.width(for: pane))
            .fixedSize(horizontal: false, vertical: true)
    }

    @ViewBuilder
    private var content: some View {
        switch pane {
        case .general: GeneralPane(model: model)
        case .menuBar: MenuBarPane(model: model)
        case .orb: OrbPane(model: model)
        case .effects: EffectsPane(model: model)
        case .cost: CostPane(model: model)
        case .traffic: TrafficPane(model: model)
        case .report: ApiPane(model: model)
        case .sources: SourcesPane(model: model)
        case .remote: RemoteHostsPane(model: model)
        case .about: AboutPane(model: model)
        }
    }
}

/// A pane inside the window: reports its natural height and stays pinned to the top while the window resizes. When
/// the screen is shorter than the pane, the window stops at the screen and the pane scrolls.
struct SettingsPaneHost: View {
    let pane: SettingsPane
    let model: AppModel
    let onHeightChange: @MainActor (CGFloat) -> Void

    var body: some View {
        // The window follows the pane's natural height; only a pane taller than the screen allows scrolls.
        ScrollView(.vertical) {
            SettingsPaneView(pane: pane, model: model)
                .onGeometryChange(for: CGFloat.self) { proxy in
                    proxy.size.height
                } action: { height in
                    onHeightChange(height)
                }
        }
        .scrollBounceBehavior(.basedOnSize)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .appEnvironment(model)
    }
}

extension View {
    /// The grouped, non-scrolling form every pane uses.
    func settingsForm() -> some View {
        formStyle(.grouped)
            .scrollDisabled(true)
    }
}
