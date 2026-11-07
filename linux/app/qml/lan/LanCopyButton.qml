import QtQuick
import QtQuick.Controls

// DESIGN 14: an address line's pull-down — Copy HTTP URL, Copy WebSocket URL and Copy TCP Address.
Item {
    id: button
    property string http
    property string ws
    property string tcp
    readonly property bool open: menu.visible
    implicitWidth: row.implicitWidth + 18
    implicitHeight: 24
    activeFocusOnTab: true
    Accessible.role: Accessible.ButtonMenu
    Accessible.name: S.tr2(S.rev, "lan.copy")
    Accessible.onPressAction: popup()
    Keys.onSpacePressed: popup()
    Keys.onReturnPressed: popup()

    // Opens under the button with their right edges aligned, so the menu stays inside the pane.
    function popup() { menu.popup(button, button.width - menu.implicitWidth, button.height + 4) }

    Rectangle {
        anchors.fill: parent
        radius: 6
        color: area.pressed || button.open ? Theme.pressed : area.containsMouse ? Theme.hover : Theme.field
        border.width: button.activeFocus ? 1.5 : 0
        border.color: Theme.accent
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    Row {
        id: row
        anchors.centerIn: parent
        spacing: 4
        Text {
            anchors.verticalCenter: parent.verticalCenter
            text: S.tr2(S.rev, "lan.copy")
            color: Theme.ink
            font.pixelSize: 12
            font.weight: Font.Medium
        }
        Glyph {
            anchors.verticalCenter: parent.verticalCenter
            width: 9
            height: 9
            name: "chevron"
            color: Theme.ink2
        }
    }
    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: button.popup()
    }
    Menu {
        id: menu
        MenuItem {
            text: S.tr2(S.rev, "lan.copy.http")
            onTriggered: lan.copy(button.http)
        }
        MenuItem {
            text: S.tr2(S.rev, "lan.copy.ws")
            onTriggered: lan.copy(button.ws)
        }
        MenuItem {
            text: S.tr2(S.rev, "lan.copy.tcp")
            onTriggered: lan.copy(button.tcp)
        }
    }
}
