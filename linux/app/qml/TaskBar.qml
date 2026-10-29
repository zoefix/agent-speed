import QtQuick
import QtQuick.Controls

// One side of a comparison row (DESIGN 3c): the value at the outer edge and a track filled from the centre outward.
Item {
    id: bar
    property var metric: null
    property color color: Theme.accent
    property bool mirrored: false
    property real scale: 1
    property string rank: ""
    readonly property bool known: metric !== null && metric !== undefined && metric.value !== undefined && metric.value !== null
    readonly property real fraction: known ? Math.max(0, Math.min(1, metric.value / scale)) : 0

    Text {
        id: value
        width: 64
        anchors.verticalCenter: parent.verticalCenter
        x: bar.mirrored ? 0 : parent.width - width
        horizontalAlignment: bar.mirrored ? Text.AlignLeft : Text.AlignRight
        text: bar.known ? bar.metric.text : "–"
        color: !bar.known ? Theme.ink3 : bar.rank === "worse" ? Theme.ink2 : Theme.ink
        font.pixelSize: 13
        font.weight: bar.rank === "better" ? Font.DemiBold : Font.Normal
        font.features: Theme.tabular
        HoverHandler { id: valueHover }
        ToolTip.visible: valueHover.hovered && bar.metric !== null && (bar.metric.tooltip || "").length > 0
        ToolTip.delay: 300
        ToolTip.text: bar.metric ? bar.metric.tooltip : ""
    }
    Item {
        id: track
        anchors.verticalCenter: parent.verticalCenter
        x: bar.mirrored ? value.width + 6 : 6
        width: parent.width - value.width - 12
        height: 6
        Rectangle { anchors.fill: parent; radius: 3; color: Theme.track }
        Rectangle {
            x: bar.mirrored ? parent.width - width : 0
            width: parent.width * bar.fraction
            height: 6
            radius: 3
            color: Qt.rgba(bar.color.r, bar.color.g, bar.color.b, Theme.dark ? 0.85 : 1)
            Behavior on width { NumberAnimation { duration: 350; easing.type: Easing.OutCubic } }
        }
    }
}
