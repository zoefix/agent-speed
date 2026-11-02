import AgentSpeedCore
import Foundation

/// The edits the board menus and Settings make (DESIGN 2c Interaction and window). Each leaves the document valid;
/// an edit of an unknown board changes nothing.
public extension BoardsDocument {
    /// Edits one board through `update`, so a style change becomes the last used style.
    mutating func edit(_ id: String, _ change: (inout BoardConfig) -> Void) {
        guard var board = board(id) else { return }
        change(&board)
        board.opacity = OrbConfig.cleanOpacity(board.opacity)
        update(board)
    }

    mutating func setStyle(_ style: BoardStyle, of id: String) {
        edit(id) { $0.style = style }
    }

    mutating func setSize(_ size: BoardSize, of id: String) {
        edit(id) { $0.size = size }
    }

    mutating func setSources(_ sources: OrbSourceSelection, of id: String) {
        edit(id) { $0.sources = sources }
    }

    /// As an orb's sources menu (DESIGN 2b): on an all-sources board the source becomes the only one; the last
    /// tracked source cannot be removed (false).
    @discardableResult
    mutating func toggleSource(_ kind: SourceKind, of id: String) -> Bool {
        guard let next = board(id)?.sources.toggling(kind) else { return false }
        setSources(next, of: id)
        return true
    }

    mutating func setOpacity(_ opacity: Double, of id: String) {
        edit(id) { $0.opacity = opacity }
    }

    mutating func setAlwaysOnTop(_ value: Bool, of id: String) {
        edit(id) { $0.alwaysOnTop = value }
    }

    mutating func setClickThrough(_ value: Bool, of id: String) {
        edit(id) { $0.clickThrough = value }
    }

    mutating func setFadeWhenIdle(_ value: Bool, of id: String) {
        edit(id) { $0.fadeWhenIdle = value }
    }

    mutating func setAllSpaces(_ value: Bool, of id: String) {
        edit(id) { $0.allSpaces = value }
    }

    mutating func setReadouts(_ readouts: BoardReadouts, of id: String) {
        edit(id) { $0.readouts = readouts }
    }

    /// `boards.readouts` ▸ one switch: toggles it (each is independent of the others).
    mutating func toggleReadout(_ readout: BoardReadout, of id: String) {
        edit(id) { $0.readouts[readout].toggle() }
    }

    mutating func setVisible(_ value: Bool, of id: String) {
        edit(id) { $0.visible = value }
    }

    /// Some board shows: the quick menu offers `boards.hide`, else `boards.show`.
    var anyVisible: Bool { boards.contains(where: \.visible) }

    /// `boards.show` / `boards.hide`: every board at once.
    mutating func setAllVisible(_ visible: Bool) {
        for index in boards.indices { boards[index].visible = visible }
    }

    /// Moves boards in the Settings list (list order only).
    mutating func move(fromOffsets source: IndexSet, toOffset destination: Int) {
        let moving = source.sorted().filter { boards.indices.contains($0) }
        guard !moving.isEmpty else { return }
        let picked = moving.map { boards[$0] }
        var rest = boards.enumerated().filter { !moving.contains($0.offset) }.map(\.element)
        let before = moving.filter { $0 < destination }.count
        let at = min(max(0, destination - before), rest.count)
        rest.insert(contentsOf: picked, at: at)
        boards = rest
    }
}
