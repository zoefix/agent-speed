import QtQuick
import QtQuick.Layouts

// One setting: a label with an optional caption on the left, its control on the right.
Item {
    id: row
    property string label
    property string caption
    property bool divider: true
    default property alias control: trailing.data
    Layout.fillWidth: true
    implicitHeight: Math.max(texts.implicitHeight, trailing.implicitHeight) + 20

    ColumnLayout {
        id: texts
        anchors.left: parent.left
        anchors.leftMargin: 14
        anchors.right: trailing.left
        anchors.rightMargin: 14
        anchors.verticalCenter: parent.verticalCenter
        spacing: 2
        Text {
            text: row.label
            color: Theme.ink
            font.pixelSize: 13
            Layout.fillWidth: true
            wrapMode: Text.Wrap
        }
        Text {
            visible: row.caption.length > 0
            text: row.caption
            color: Theme.ink3
            font.pixelSize: 11
            lineHeight: 1.2
            Layout.fillWidth: true
            wrapMode: Text.Wrap
        }
    }
    Row {
        id: trailing
        anchors.right: parent.right
        anchors.rightMargin: 14
        anchors.verticalCenter: parent.verticalCenter
        spacing: 8
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
