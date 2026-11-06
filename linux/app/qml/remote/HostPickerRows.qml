import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 13: the Hosts rows of the orb and board editors, only while remote hosts exist — All Hosts, then every host
// (this computer first) with its status dot and, when it is not connected, its status word. With All Hosts on the
// rows show checked and disabled; the last checked host cannot be unchecked. Turning All Hosts off keeps every host
// checked, so the reading does not jump.
ColumnLayout {
    id: picker
    // The orb's or board's `hosts`: "all" or a list of ids.
    property var hosts: "all"
    // A caption row inside a card that has other rows (the board editor).
    property string title: ""
    property bool lastDivider: false
    signal picked(var hosts)

    Layout.fillWidth: true
    spacing: 0
    readonly property bool all: hosts === undefined || hosts === null || hosts === "all"
    readonly property var entries: S.rev >= 0 && hostsView.choices.length > 0 ? hostsView.entries(hosts) : []

    Item {
        visible: picker.title.length > 0
        Layout.fillWidth: true
        implicitHeight: 30
        Text {
            anchors.left: parent.left
            anchors.leftMargin: 14
            anchors.bottom: parent.bottom
            anchors.bottomMargin: 2
            text: picker.title
            color: Theme.ink2
            font.pixelSize: 12
            font.weight: Font.DemiBold
            Accessible.role: Accessible.Heading
            Accessible.name: text
        }
    }
    SettingsRow {
        label: S.tr2(S.rev, "remote.hosts.all")
        caption: S.tr2(S.rev, "remote.hosts.allHint")
        CheckBox {
            anchors.verticalCenter: parent.verticalCenter
            checked: picker.all
            onToggled: picker.picked(hostsView.allHosts(checked))
            Accessible.name: S.tr2(S.rev, "remote.hosts.all")
            Accessible.description: S.tr2(S.rev, "remote.hosts.allHint")
        }
    }
    Repeater {
        model: picker.entries
        delegate: SettingsRow {
            id: hostRow
            required property var modelData
            required property int index
            readonly property bool last: !picker.all && !modelData.enabled
            label: modelData.name
            divider: picker.lastDivider || index < picker.entries.length - 1
            Text {
                anchors.verticalCenter: parent.verticalCenter
                visible: hostRow.modelData.word.length > 0
                text: hostRow.modelData.word
                color: Theme.ink3
                font.pixelSize: 11
            }
            HostDot {
                anchors.verticalCenter: parent.verticalCenter
                kind: hostRow.modelData.dot
            }
            CheckBox {
                id: box
                anchors.verticalCenter: parent.verticalCenter
                checked: picker.all || hostRow.modelData.checked
                enabled: !picker.all && hostRow.modelData.enabled
                Accessible.name: hostRow.modelData.name
                Accessible.description: hostRow.modelData.word
                onToggled: {
                    const next = hostsView.toggled(picker.hosts, hostRow.modelData.id)
                    if (next === null || next === undefined) checked = true
                    else picker.picked(next)
                }
                ToolTip.visible: hostRow.last && trailingHover.hovered
                ToolTip.text: S.tr2(S.rev, "remote.hosts.lastOne")
            }
            // On the trailing row: a disabled check box sees no hover.
            HoverHandler { id: trailingHover }
        }
    }
}
