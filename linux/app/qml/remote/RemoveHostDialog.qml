import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 13 Remove: `remote.remove.confirm`, where the helper stays on the host, and *Also delete its usage history*
// (off). Cancel holds the focus, so Return never removes by accident.
Popup {
    id: dialog
    parent: Overlay.overlay
    anchors.centerIn: parent
    width: 400
    modal: true
    focus: true
    padding: 24
    closePolicy: Popup.CloseOnEscape
    property string hostId: ""
    property string hostName: ""

    function ask(id, name) {
        hostId = id
        hostName = name
        deleteHistory.checked = false
        open()
        cancelButton.forceActiveFocus()
    }

    Overlay.modal: Rectangle { color: Qt.rgba(0, 0, 0, Theme.dark ? 0.46 : 0.22) }
    enter: Transition {
        ParallelAnimation {
            NumberAnimation { property: "opacity"; from: 0; to: 1; duration: 160; easing.type: Easing.OutCubic }
            NumberAnimation { property: "scale"; from: 0.97; to: 1; duration: 190; easing.type: Easing.OutCubic }
        }
    }
    exit: Transition { NumberAnimation { property: "opacity"; from: 1; to: 0; duration: 110 } }
    background: Rectangle {
        radius: 14
        color: Theme.dark ? "#22252C" : "#FFFFFF"
        border.width: 1
        border.color: Theme.cardLine
    }

    contentItem: ColumnLayout {
        spacing: 12
        Accessible.role: Accessible.AlertMessage
        Accessible.name: title.text
        RowLayout {
            Layout.fillWidth: true
            spacing: 12
            Rectangle {
                Layout.preferredWidth: 30
                Layout.preferredHeight: 30
                Layout.alignment: Qt.AlignTop
                radius: 8
                color: Qt.rgba(Theme.danger.r, Theme.danger.g, Theme.danger.b, Theme.dark ? 0.2 : 0.12)
                Glyph {
                    anchors.centerIn: parent
                    width: 16
                    height: 16
                    name: "server"
                    color: Theme.danger
                }
            }
            Text {
                id: title
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignVCenter
                text: Format.fill(S.tr2(S.rev, "remote.remove.confirm"), [dialog.hostName])
                color: Theme.ink
                font.pixelSize: 15
                font.weight: Font.DemiBold
                wrapMode: Text.Wrap
            }
        }
        Text {
            Layout.fillWidth: true
            text: S.rev >= 0 ? hostsView.removeFiles(dialog.hostId) : ""
            color: Theme.ink2
            font.pixelSize: 13
            lineHeight: 1.25
            wrapMode: Text.Wrap
        }
        CheckBox {
            id: deleteHistory
            Layout.leftMargin: -6
            text: S.tr2(S.rev, "remote.remove.deleteHistory")
            Accessible.name: text
        }
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 6
            spacing: 8
            Item { Layout.fillWidth: true }
            PillButton {
                id: cancelButton
                text: S.tr2(S.rev, "history.cancel")
                onClicked: dialog.close()
            }
            PillButton {
                destructive: true
                text: S.tr2(S.rev, "remote.remove.button")
                onClicked: {
                    remote.removeHost(dialog.hostId, deleteHistory.checked)
                    dialog.close()
                }
            }
        }
    }
}
