import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 5: a pick the list refuses changes nothing and says why — the reason, the picked path and one OK button.
Popup {
    id: dialog
    parent: Overlay.overlay
    anchors.centerIn: parent
    width: 380
    modal: true
    focus: true
    padding: 24
    closePolicy: Popup.CloseOnEscape
    property string reason: ""
    property string path: ""

    function show(why, picked) {
        reason = why
        path = picked
        open()
        okButton.forceActiveFocus()
    }

    readonly property string message: reason === "alreadyListed" ? S.tr2(S.rev, "settings.sources.alreadyListed")
                                    : reason === "alreadyDefault" ? S.tr2(S.rev, "settings.sources.alreadyDefault")
                                    : Format.fill(S.tr2(S.rev, "settings.sources.limit"), [locations.maxEntries])

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
        Accessible.name: dialog.message
        Accessible.description: dialog.path
        RowLayout {
            Layout.fillWidth: true
            spacing: 12
            Rectangle {
                Layout.preferredWidth: 30
                Layout.preferredHeight: 30
                Layout.alignment: Qt.AlignTop
                radius: 8
                color: Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, Theme.dark ? 0.2 : 0.12)
                LocationIcon {
                    anchors.centerIn: parent
                    width: 18
                    height: 18
                }
            }
            Text {
                Layout.fillWidth: true
                Layout.alignment: Qt.AlignVCenter
                text: dialog.message
                color: Theme.ink
                font.pixelSize: 15
                font.weight: Font.DemiBold
                wrapMode: Text.Wrap
            }
        }
        TextEdit {
            Layout.fillWidth: true
            Layout.leftMargin: 42
            text: dialog.path
            color: Theme.ink2
            font.pixelSize: 12
            wrapMode: TextEdit.WrapAnywhere
            readOnly: true
            selectByMouse: true
            selectionColor: Theme.accent
            selectedTextColor: "white"
            activeFocusOnPress: false
            Accessible.ignored: true
        }
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 6
            Item { Layout.fillWidth: true }
            PillButton {
                id: okButton
                accent: true
                text: S.tr2(S.rev, "linux.ok")
                Layout.preferredWidth: Math.max(76, implicitWidth)
                onClicked: dialog.close()
                Keys.onEnterPressed: dialog.close()
            }
        }
    }
}
