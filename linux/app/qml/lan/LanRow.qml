import QtQuick
import QtQuick.Layouts

// A row of the server group: the label in a shared column, centered on the content's first line, then the content.
Item {
    id: row
    property string label
    property real labelWidth: 120
    // The height of the content's first line, which the label centers on.
    property real lineHeight: 28
    property bool divider: true
    default property alias content: slot.data
    Layout.fillWidth: true
    implicitHeight: Math.max(labelText.implicitHeight, slot.children.length > 0 ? slot.children[0].implicitHeight : 0) + 22

    Text {
        id: labelText
        x: 14
        y: 11 + Math.max(0, (row.lineHeight - implicitHeight) / 2)
        width: row.labelWidth
        text: row.label
        color: Theme.ink
        font.pixelSize: 13
        wrapMode: Text.Wrap
        Accessible.ignored: true
    }
    Item {
        id: slot
        x: 14 + row.labelWidth + 16
        y: 11
        width: row.width - x - 14
        height: children.length > 0 ? children[0].implicitHeight : 0
    }
    Rectangle {
        visible: row.divider
        anchors.left: parent.left
        anchors.leftMargin: 14
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        color: Theme.hairline
    }
}
