import QtQuick

// One agent: phase badge, project over "model · source", and the live value; sub-agents indent 12 with connectors.
Item {
    id: row
    required property string project
    required property string detail
    required property string phase
    required property string source
    required property real live
    required property int depth
    required property bool continues
    required property bool hasChildren
    required property int rails
    implicitHeight: 40

    readonly property real indent: depth * 12
    readonly property real railX: (depth - 1) * 12 + 7.5

    Rectangle {
        anchors.fill: parent
        anchors.leftMargin: -6
        anchors.rightMargin: -6
        radius: 7
        color: hover.hovered ? Theme.hover : "transparent"
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    HoverHandler { id: hover }

    Repeater {
        model: row.depth > 1 ? row.depth - 1 : 0
        Rectangle {
            required property int index
            visible: (row.rails & (1 << (index + 1))) !== 0
            x: index * 12 + 7.5
            width: 1
            height: row.height
            color: Theme.connector
        }
    }
    Rectangle {
        visible: row.depth > 0
        x: row.railX
        width: 1
        height: row.continues ? row.height : row.height / 2
        color: Theme.connector
    }
    Rectangle {
        visible: row.depth > 0
        x: row.railX
        y: row.height / 2
        width: row.indent + 2 - row.railX
        height: 1
        color: Theme.connector
    }
    Rectangle {
        visible: row.hasChildren
        x: row.indent + 7.5
        y: row.height / 2 + 7
        width: 1
        height: row.height / 2 - 7
        color: Theme.connector
    }

    PhaseBadge {
        id: badge
        x: row.indent
        anchors.verticalCenter: parent.verticalCenter
        phase: row.phase
        tint: Theme.sourceColor(row.source)
    }

    Column {
        anchors.left: badge.right
        anchors.leftMargin: 10
        anchors.right: slot.left
        anchors.rightMargin: 10
        anchors.verticalCenter: parent.verticalCenter
        spacing: 1
        Text {
            width: parent.width
            text: row.project
            color: Theme.ink
            font.pixelSize: 13
            font.weight: Font.DemiBold
            elide: Text.ElideRight
        }
        Text {
            width: parent.width
            text: row.detail
            color: Theme.ink2
            font.pixelSize: 12
            elide: Text.ElideRight
        }
    }

    LiveSlot {
        id: slot
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        value: row.live
        phase: row.phase
        pixelSize: 14
    }
}
