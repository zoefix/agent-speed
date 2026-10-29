import AgentSpeedOrbModel
import SwiftUI

/// The selected board's live metrics switches (DESIGN 2c Live metrics, *Switches*): 性能, 思考 and 費用 as three
/// independent checkboxes in the menu's order, with `boards.readouts.hint` under them. Each change applies and saves
/// at once; the style cards above show the readouts as switched.
struct BoardReadoutsSection: View {
    let model: AppModel
    let board: BoardConfig

    var body: some View {
        let l10n = model.l10n
        let boards = model.boards
        let id = board.id
        Section {
            ForEach(BoardReadout.allCases) { readout in
                Toggle(isOn: Binding(
                    get: { board.readouts[readout] },
                    set: { on in
                        var readouts = board.readouts
                        readouts[readout] = on
                        boards.setReadouts(readouts, of: id)
                    }
                )) {
                    Text(l10n(readout.titleKey))
                }
                .toggleStyle(.checkbox)
            }
        } header: {
            Text(l10n("boards.readouts"))
        } footer: {
            SectionFootnote(l10n("boards.readouts.hint"))
        }
    }
}
