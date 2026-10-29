import QtQuick
import QtQuick.Layouts

// DESIGN 11 Dashboard invitation (一鍵開啟): offered once, above the hero. Showing it touches nothing; only Turn On
// makes a request. No button is the default, so Return never accepts.
Item {
    id: card
    readonly property bool shown: report.inviteVisible
    Layout.fillWidth: true
    Layout.preferredHeight: shown ? frame.implicitHeight + 14 : 0
    opacity: shown ? 1 : 0
    clip: true
    Behavior on Layout.preferredHeight { NumberAnimation { duration: 260; easing.type: Easing.OutCubic } }
    Behavior on opacity { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
    Accessible.role: Accessible.Grouping
    Accessible.name: S.tr2(S.rev, "report.invite.title")

    Rectangle {
        id: frame
        width: parent.width
        y: card.shown ? 0 : -8
        Behavior on y { NumberAnimation { duration: 260; easing.type: Easing.OutCubic } }
        implicitHeight: (report.inviteDone ? doneRow.implicitHeight : content.implicitHeight) + 24
        Behavior on implicitHeight { NumberAnimation { duration: 220; easing.type: Easing.OutCubic } }
        radius: 12
        color: Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, Theme.dark ? 0.14 : 0.08)
        border.width: 0.5
        border.color: Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, 0.2)

        ColumnLayout {
            id: content
            x: 12
            y: 12
            width: parent.width - 24
            spacing: 0
            opacity: report.inviteDone ? 0 : 1
            visible: opacity > 0
            Behavior on opacity { NumberAnimation { duration: 180 } }
            RowLayout {
                Layout.fillWidth: true
                Layout.rightMargin: 18
                spacing: 8
                Rectangle {
                    width: 22
                    height: 22
                    radius: 6
                    gradient: Gradient {
                        GradientStop { position: 0; color: Qt.lighter(Theme.accent, 1.15) }
                        GradientStop { position: 1; color: Theme.accent }
                    }
                    Glyph {
                        anchors.centerIn: parent
                        name: "broadcast"
                        width: 13
                        height: 13
                        color: "white"
                    }
                }
                Text {
                    text: S.tr2(S.rev, "report.invite.title")
                    color: Theme.ink
                    font.pixelSize: 13
                    font.weight: Font.DemiBold
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
            }
            Text {
                Layout.topMargin: 8
                Layout.fillWidth: true
                text: report.inviteBody
                color: Theme.ink2
                font.pixelSize: 12
                lineHeight: 1.2
                wrapMode: Text.Wrap
                maximumLineCount: 4
            }
            Text {
                Layout.topMargin: 4
                Layout.fillWidth: true
                text: report.inviteUsername
                color: Theme.ink3
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
            RowLayout {
                Layout.topMargin: 10
                Layout.fillWidth: true
                spacing: 8
                LinkButton {
                    glyph: "eye"
                    text: S.tr2(S.rev, "report.invite.preview")
                    onClicked: report.openPreview()
                }
                Item { Layout.fillWidth: true }
                PillButton {
                    text: S.tr2(S.rev, "report.invite.decline")
                    onClicked: report.declineInvite()
                }
                PillButton {
                    accent: true
                    text: S.tr2(S.rev, "report.invite.accept")
                    onClicked: report.acceptInvite()
                }
            }
        }
        RowLayout {
            id: doneRow
            x: 12
            y: 12
            width: parent.width - 24
            spacing: 8
            opacity: report.inviteDone ? 1 : 0
            visible: opacity > 0
            Behavior on opacity { NumberAnimation { duration: 180 } }
            Glyph {
                name: "checkCircle"
                width: 15
                height: 15
                color: Theme.accent
            }
            Text {
                text: S.tr2(S.rev, "report.invite.done")
                color: Theme.ink
                font.pixelSize: 12
                wrapMode: Text.Wrap
                Layout.fillWidth: true
                Accessible.role: Accessible.AlertMessage
                Accessible.name: text
            }
        }
        // The close button: the same as decline.
        Item {
            visible: !report.inviteDone
            anchors.right: parent.right
            anchors.top: parent.top
            anchors.margins: 6
            width: 18
            height: 18
            activeFocusOnTab: true
            Accessible.role: Accessible.Button
            Accessible.name: S.tr2(S.rev, "report.invite.decline")
            Keys.onSpacePressed: report.declineInvite()
            Glyph {
                anchors.centerIn: parent
                name: "close"
                width: 10
                height: 10
                color: closeArea.containsMouse ? Theme.ink : Theme.ink3
            }
            MouseArea {
                id: closeArea
                anchors.fill: parent
                hoverEnabled: true
                cursorShape: Qt.PointingHandCursor
                onClicked: report.declineInvite()
            }
        }
    }
}
