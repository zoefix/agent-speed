import AgentSpeedOrbModel
import SwiftUI

/// The Boards part of Settings → 懸浮窗, under the orbs (DESIGN 2c): the list, then the editor of the selected board
/// — sources, style, the live metrics switches, size and opacity, window toggles. Every change applies live and persists immediately.
struct BoardsSettings: View {
    let model: AppModel

    private var selected: BoardConfig? {
        let boards = model.boards
        return boards.settingsSelection.flatMap { boards.board($0) } ?? boards.boards.first
    }

    var body: some View {
        BoardListSection(model: model)
        if let board = selected {
            BoardSourcesSection(model: model, board: board) {
                BoardEditorHeading(model: model, board: board)
            }
            HostPickerSection(model: model, selection: board.hosts,
                              set: { model.boards.setHosts($0, of: board.id) },
                              toggle: { model.boards.toggleHost($0, of: board.id) })
            BoardStyleSection(model: model, board: board)
            BoardReadoutsSection(model: model, board: board)
            BoardLookSection(model: model, board: board)
        }
    }
}
