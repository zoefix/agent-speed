import QtQuick
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 5: a source's switch with its status line — sessions found (over every location), folder not found or off.
Item {
    id: row
    property string source
    property string name
    // Null before the first snapshot: nothing to say yet.
    property var info: null
    readonly property bool active: S.rev >= 0 && prefs.disabledSources.indexOf(source) < 0
    readonly property bool known: info !== null
    readonly property bool found: known && info.available === true
    readonly property int sessions: known ? info.sessions || 0 : 0
    readonly property string status: !active ? S.tr2(S.rev, "source.disabled")
                                     : !known ? ""
                                     : found ? Format.fill(S.tr2(S.rev, "source.sessions"), [sessions])
                                     : S.tr2(S.rev, "source.folderMissing")

    Layout.fillWidth: true
    implicitHeight: content.implicitHeight + 22

    RowLayout {
        id: content
        anchors.left: parent.left
        anchors.leftMargin: 14
        anchors.right: parent.right
        anchors.rightMargin: 14
        anchors.verticalCenter: parent.verticalCenter
        spacing: 10
        Rectangle {
            Layout.alignment: Qt.AlignTop
            Layout.topMargin: 5
            Layout.preferredWidth: 9
            Layout.preferredHeight: 9
            radius: 4.5
            color: Theme.sourceColor(row.source)
            opacity: row.active ? 1 : 0.35
            Behavior on opacity { NumberAnimation { duration: 160 } }
        }
        ColumnLayout {
            Layout.fillWidth: true
            spacing: 3
            Text {
                Layout.fillWidth: true
                text: row.name
                color: row.active ? Theme.ink : Theme.ink2
                font.pixelSize: 13
                font.weight: Font.DemiBold
                elide: Text.ElideRight
            }
            RowLayout {
                visible: row.status.length > 0
                spacing: 5
                Glyph {
                    Layout.preferredWidth: 11
                    Layout.preferredHeight: 11
                    name: !row.active ? "minus" : !row.found ? "warning" : row.sessions > 0 ? "checkCircle" : "dotRing"
                    color: !row.active ? Theme.ink3 : !row.found ? (Theme.dark ? "#FB923C" : "#E86A0C") : row.sessions > 0 ? Theme.good : Theme.ink3
                    Accessible.ignored: true
                }
                Text {
                    text: row.status
                    color: Theme.ink2
                    font.pixelSize: 11
                    font.features: Theme.tabular
                }
            }
        }
        ToggleSwitch {
            label: row.name
            checked: row.active
            Accessible.description: row.status
            onToggled: (on) => prefs.setSourceEnabled(row.source, on)
        }
    }

    Rectangle {
        anchors.left: parent.left
        anchors.leftMargin: 14
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        color: Theme.hairline
    }
}
