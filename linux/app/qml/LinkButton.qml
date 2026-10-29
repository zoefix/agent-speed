import QtQuick

// A borderless text button in the accent color (links to a service's pages, small secondary actions).
Item {
    id: link
    property string text
    property string glyph
    property color color: Theme.accent
    property bool destructive: false
    signal clicked()
    implicitWidth: row.implicitWidth + 4
    implicitHeight: 24
    activeFocusOnTab: true
    opacity: enabled ? 1 : 0.45
    Accessible.role: Accessible.Button
    Accessible.name: text
    Accessible.onPressAction: clicked()
    Keys.onSpacePressed: clicked()
    Keys.onReturnPressed: clicked()

    Row {
        id: row
        anchors.centerIn: parent
        spacing: 5
        Glyph {
            visible: link.glyph.length > 0
            name: link.glyph
            width: 13
            height: 13
            anchors.verticalCenter: parent.verticalCenter
            color: label.color
        }
        Text {
            id: label
            text: link.text
            color: link.destructive ? Theme.danger : link.color
            font.pixelSize: 12
            font.weight: Font.Medium
            font.underline: area.containsMouse || link.activeFocus
        }
    }
    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: link.clicked()
    }
}
