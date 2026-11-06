import QtQuick

// A quiet rounded text button.
Item {
    id: button
    property string text
    property bool accent: false
    // A destructive confirmation (Remove): white on the danger color.
    property bool destructive: false
    readonly property bool filled: accent || destructive
    readonly property color fillColor: destructive ? Theme.danger : Theme.accent
    signal clicked()
    implicitWidth: caption.implicitWidth + 26
    implicitHeight: 28
    activeFocusOnTab: true
    opacity: enabled ? 1 : 0.45
    Accessible.role: Accessible.Button
    Accessible.name: text
    Accessible.onPressAction: clicked()
    Keys.onSpacePressed: clicked()
    Keys.onReturnPressed: clicked()

    Rectangle {
        anchors.fill: parent
        radius: 7
        color: button.filled ? (area.pressed ? Qt.darker(button.fillColor, 1.15) : button.fillColor)
                             : area.pressed ? Theme.pressed : area.containsMouse ? Theme.hover : Theme.field
        border.width: button.activeFocus ? 1.5 : 0
        border.color: button.filled ? Qt.lighter(button.fillColor, 1.45) : Theme.accent
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    Text {
        id: caption
        anchors.centerIn: parent
        text: button.text
        color: button.filled ? "white" : Theme.ink
        font.pixelSize: 12
        font.weight: Font.Medium
    }
    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: button.clicked()
    }
}
