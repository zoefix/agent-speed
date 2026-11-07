import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 14 alerts — Regenerate the key, Remove a target: the question, what it means, Cancel (which holds the focus,
// so Return never confirms by accident) and the action.
Popup {
    id: dialog
    parent: Overlay.overlay
    anchors.centerIn: parent
    width: 400
    modal: true
    focus: true
    padding: 24
    closePolicy: Popup.CloseOnEscape
    property string question
    property string detail
    property string action
    property string glyph: "broadcast"
    property bool destructive: false
    signal confirmed()

    function ask() {
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
        Accessible.name: dialog.question
        Accessible.description: dialog.detail
        RowLayout {
            Layout.fillWidth: true
            spacing: 12
            Rectangle {
                readonly property color tint: dialog.destructive ? Theme.danger : Theme.accent
                Layout.preferredWidth: 30
                Layout.preferredHeight: 30
                Layout.alignment: Qt.AlignTop
                radius: 8
                color: Qt.rgba(tint.r, tint.g, tint.b, Theme.dark ? 0.2 : 0.12)
                Glyph {
                    anchors.centerIn: parent
                    width: 16
                    height: 16
                    name: dialog.glyph
                    color: parent.tint
                }
            }
            Text {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignVCenter
                text: dialog.question
                color: Theme.ink
                font.pixelSize: 15
                font.weight: Font.DemiBold
                wrapMode: Text.Wrap
            }
        }
        Text {
            visible: dialog.detail.length > 0
            Layout.fillWidth: true
            Layout.leftMargin: 42
            text: dialog.detail
            color: Theme.ink2
            font.pixelSize: 13
            lineHeight: 1.25
            wrapMode: Text.Wrap
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
                destructive: dialog.destructive
                accent: !dialog.destructive
                text: dialog.action
                onClicked: {
                    dialog.confirmed()
                    dialog.close()
                }
            }
        }
    }
}
