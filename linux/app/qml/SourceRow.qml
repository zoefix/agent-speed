import QtQuick

// One source: colored dot, name, today's output (secondary) and the live value.
Item {
    id: row
    required property string source
    required property string name
    required property string todayText
    required property real live
    implicitHeight: 30

    Rectangle {
        anchors.fill: parent
        anchors.leftMargin: -6
        anchors.rightMargin: -6
        radius: 7
        color: hover.hovered ? Theme.hover : "transparent"
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    HoverHandler { id: hover }

    Rectangle {
        id: dot
        x: 4
        anchors.verticalCenter: parent.verticalCenter
        width: 8
        height: 8
        radius: 4
        color: Theme.sourceColor(row.source)
    }
    Text {
        anchors.left: dot.right
        anchors.leftMargin: 10
        anchors.right: today.left
        anchors.rightMargin: 10
        anchors.verticalCenter: parent.verticalCenter
        text: row.name
        color: Theme.ink
        font.pixelSize: 13
        elide: Text.ElideRight
    }
    Text {
        id: today
        anchors.right: slot.left
        anchors.rightMargin: 8
        anchors.verticalCenter: parent.verticalCenter
        text: row.todayText
        color: Theme.ink3
        font.pixelSize: 12
        font.features: Theme.tabular
    }
    LiveSlot {
        id: slot
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        value: row.live
    }
}
