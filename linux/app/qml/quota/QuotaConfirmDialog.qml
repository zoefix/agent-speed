import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 5 額度: the question before a quota route changes something or reads a sign-in — connecting Claude Code's
// status line, reading a site's browser cookies. The action is the default button (Return confirms); Cancel, Esc or a
// click outside leave the switch off.
Popup {
    id: dialog
    parent: Overlay.overlay
    anchors.centerIn: parent
    width: 420
    modal: true
    focus: true
    padding: 24
    closePolicy: Popup.CloseOnEscape | Popup.CloseOnPressOutside
    property string question
    property string detail
    property string action
    property string glyph: "info"
    property bool answered: false
    signal confirmed()
    signal cancelled()

    function ask() {
        answered = false
        open()
        okButton.forceActiveFocus()
    }
    onClosed: if (!answered) cancelled()

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
                Layout.preferredWidth: 30
                Layout.preferredHeight: 30
                Layout.alignment: Qt.AlignTop
                radius: 8
                color: Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, Theme.dark ? 0.2 : 0.12)
                Glyph {
                    anchors.centerIn: parent
                    width: 16
                    height: 16
                    name: dialog.glyph
                    color: Theme.accent
                }
            }
            Text {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignVCenter
                // A host's name and the path it reports are shown as plain text (REMOTE 5.6).
                textFormat: Text.PlainText
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
            textFormat: Text.PlainText
            text: dialog.detail
            color: Theme.ink2
            font.pixelSize: 13
            lineHeight: 1.3
            wrapMode: Text.Wrap
        }
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 6
            spacing: 8
            Item { Layout.fillWidth: true }
            PillButton {
                text: S.tr2(S.rev, "history.cancel")
                onClicked: dialog.close()
            }
            PillButton {
                id: okButton
                accent: true
                text: dialog.action
                Keys.onReturnPressed: clicked()
                Keys.onEnterPressed: clicked()
                onClicked: {
                    dialog.answered = true
                    dialog.close()
                    dialog.confirmed()
                }
            }
        }
    }
}
