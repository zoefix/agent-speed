import QtQuick
import QtQuick.Controls

// The Claude Code row's invitation (DESIGN 4): the link `dashboard.quota.enableCli` (11 px, accent), which opens the
// 額度 settings where the confirmation asks, and at the trailing edge a × that hides the invitation for good.
Item {
    id: invite
    signal accepted()
    signal dismissed()
    implicitHeight: 16

    Text {
        id: link
        textFormat: Text.PlainText
        anchors.left: parent.left
        anchors.verticalCenter: parent.verticalCenter
        width: Math.min(implicitWidth, invite.width - close.width - 8)
        text: S.tr2(S.rev, "dashboard.quota.enableCli")
        color: Theme.accent
        font.pixelSize: 11
        font.underline: linkArea.containsMouse || link.activeFocus
        elide: Text.ElideRight
        activeFocusOnTab: true
        Accessible.role: Accessible.Link
        Accessible.name: text
        Accessible.onPressAction: invite.accepted()
        Keys.onSpacePressed: invite.accepted()
        Keys.onReturnPressed: invite.accepted()
        MouseArea {
            id: linkArea
            anchors.fill: parent
            anchors.margins: -3
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: invite.accepted()
        }
    }
    Item {
        id: close
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        width: 18
        height: 16
        activeFocusOnTab: true
        Accessible.role: Accessible.Button
        Accessible.name: S.tr2(S.rev, "dashboard.quota.dismiss")
        Accessible.onPressAction: invite.dismissed()
        Keys.onSpacePressed: invite.dismissed()
        Keys.onReturnPressed: invite.dismissed()
        Glyph {
            // The glyph's trailing edge on the numbers' column edge.
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            name: "close"
            width: 10
            height: 10
            color: closeArea.containsMouse || close.activeFocus ? Theme.ink2 : Theme.ink3
            Behavior on color { ColorAnimation { duration: 150 } }
        }
        MouseArea {
            id: closeArea
            anchors.fill: parent
            hoverEnabled: true
            cursorShape: Qt.PointingHandCursor
            onClicked: invite.dismissed()
        }
        ToolTip.visible: closeArea.containsMouse
        ToolTip.delay: 450
        ToolTip.text: S.tr2(S.rev, "dashboard.quota.dismiss")
    }
}
