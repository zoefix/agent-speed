import QtQuick

// DESIGN 12 proof mark: a dotted underline under an unverified or pending score. It takes no layout room.
Item {
    id: dots
    property real textWidth: 0
    property real pixelSize: 12
    property color color: Theme.ink
    property bool shown: false
    readonly property real d: Math.max(1, Math.round(0.1 * pixelSize * 2) / 2)
    readonly property int count: Math.max(2, Math.floor((textWidth - d) / (2.4 * d)) + 1)
    width: textWidth
    height: d
    opacity: shown ? 0.7 : 0
    Behavior on opacity { NumberAnimation { duration: 250 } }

    Repeater {
        model: dots.count
        Rectangle {
            required property int index
            x: dots.count > 1 ? index * (dots.textWidth - dots.d) / (dots.count - 1) : 0
            width: dots.d
            height: dots.d
            radius: dots.d / 2
            color: dots.color
        }
    }
}
