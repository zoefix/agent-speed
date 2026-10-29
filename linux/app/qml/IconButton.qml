import QtQuick

// A round, borderless footer button around a Glyph.
Item {
    id: button
    property alias glyph: icon.name
    property string label
    property bool checked: false
    signal clicked()
    implicitWidth: 28
    implicitHeight: 28
    activeFocusOnTab: true
    Accessible.role: Accessible.Button
    Accessible.name: label
    Accessible.onPressAction: clicked()
    Keys.onSpacePressed: clicked()
    Keys.onReturnPressed: clicked()

    Rectangle {
        anchors.fill: parent
        radius: width / 2
        color: area.pressed ? Theme.pressed : area.containsMouse || button.checked ? Theme.hover : "transparent"
        border.width: button.activeFocus ? 1.5 : 0
        border.color: Theme.system.highlight
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    Glyph {
        id: icon
        anchors.centerIn: parent
        width: 15
        height: 15
        color: area.containsMouse || button.checked ? Theme.ink : Theme.ink2
    }
    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: button.clicked()
    }
}
