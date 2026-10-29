import QtQuick
import QtQuick.Layouts

Rectangle {
    id: tile
    property string label
    property string value
    property string unit
    property color ink
    property color ink2
    property color ink3
    radius: 14
    implicitHeight: 72
    Layout.fillWidth: true

    Column {
        anchors.left: parent.left
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        anchors.leftMargin: 14
        anchors.rightMargin: 14
        spacing: 4
        Text {
            text: tile.label
            color: tile.ink2
            font.pixelSize: 12
            elide: Text.ElideRight
            width: parent.width
        }
        Row {
            spacing: 4
            Text {
                text: tile.value
                color: tile.ink
                font.pixelSize: 22
                font.weight: Font.DemiBold
                font.features: { "tnum": 1 }
            }
            Text {
                visible: tile.unit.length > 0
                text: tile.unit
                color: tile.ink3
                font.pixelSize: 12
                anchors.baseline: parent.children[0].baseline
            }
        }
    }
}
