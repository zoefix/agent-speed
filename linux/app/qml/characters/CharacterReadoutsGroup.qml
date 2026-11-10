import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// The `character` orb's cards (DESIGN 2a.6, Readouts): a checkbox per card and the 首字 row, in the menus' order, the
// network card's scope under them, and `character.readouts.hint`. It takes the place of the unit and the two-line
// readout: the character shows the speed in tok/s and everything else on its own cards. Each change applies and saves
// at once.
SettingsGroup {
    id: group
    property var orb: null
    readonly property bool measured: prefs.trafficEnabled
    title: S.tr2(S.rev, "character.readouts")
    footnote: S.tr2(S.rev, "character.readouts.hint")

    Repeater {
        // The menus' order, from the orb model: intel, thinking, latency, network, cost, agents, models, quota, model.
        model: orbs.characterReadoutItems()
        delegate: SettingsRow {
            id: readoutRow
            required property var modelData
            label: S.tr2(S.rev, modelData.title)
            CheckBox {
                anchors.verticalCenter: parent.verticalCenter
                checked: group.orb && group.orb.characterReadouts ? group.orb.characterReadouts[readoutRow.modelData.key] === true : false
                enabled: readoutRow.modelData.key !== "network" || group.measured
                onToggled: if (group.orb) orbs.setCharacterReadout(group.orb.id, readoutRow.modelData.key, checked)
                Accessible.name: S.tr2(S.rev, readoutRow.modelData.title)
                ToolTip.visible: !enabled && hovered
                ToolTip.text: S.tr2(S.rev, "settings.traffic.enabled")
            }
        }
    }
    SettingsRow {
        label: S.tr2(S.rev, "character.readouts.networkScope")
        divider: false
        enabled: group.measured && group.orb && group.orb.characterReadouts && group.orb.characterReadouts.network === true
        opacity: enabled ? 1 : 0.5
        Dropdown {
            popupWidth: 200
            value: group.orb && group.orb.traffic === "machine" ? "machine" : "sources"
            options: [
                { value: "sources", label: S.tr2(S.rev, "orbs.traffic.sources") },
                { value: "machine", label: S.tr2(S.rev, "orbs.traffic.machine") }
            ]
            onActivated: (v) => { if (group.orb) orbs.set(group.orb.id, "traffic", v) }
        }
    }
}
