import AgentSpeedOrbModel
import SwiftUI

/// The `character` orb's cards (DESIGN 2a.6, Readouts): a checkbox per card in the menu's order, the network card's
/// scope under them, and `character.readouts.hint`. It takes the place of the unit and the two-line readout: the
/// character shows the speed in tok/s and everything else on its own cards. Each change applies and saves at once.
struct OrbCharacterReadoutsSection: View {
    let model: AppModel
    let orb: OrbConfig

    var body: some View {
        let l10n = model.l10n
        let orbs = model.orbs
        let id = orb.id
        let measured = model.settings.trafficEnabled
        Section {
            ForEach(CharacterReadout.allCases) { readout in
                Toggle(isOn: Binding(
                    get: { orb.characterReadouts[readout] },
                    set: { orbs.setCharacterReadout(readout, $0, of: id) }
                )) {
                    Text(l10n(readout.titleKey))
                }
                .toggleStyle(.checkbox)
                .disabled(readout == .network && !measured)
                .help(readout == .network && !measured ? l10n("settings.traffic.enabled") : "")
            }
            PopupRow(
                title: l10n("character.readouts.networkScope"),
                selection: Binding(get: { orb.traffic == .machine ? OrbTraffic.machine : .sources },
                                   set: { orbs.setTraffic($0, of: id) })
            ) {
                ForEach([OrbTraffic.sources, .machine]) { scope in
                    Text(l10n(scope.titleKey)).tag(scope)
                }
            }
            .disabled(!measured || !orb.characterReadouts.network)
        } header: {
            Text(l10n("character.readouts"))
        } footer: {
            SectionFootnote(l10n("character.readouts.hint"))
        }
    }
}
