import QtQuick

// A meter of the performance score (DESIGN 12): the value on the log scale in the score ink over a track, with a tick
// at 1000 (the middle). Absent: a dotted track without fill.
Item {
    id: meter
    property real f: 0          // the log-scale fraction, 0…1
    property bool present: true
    property real tickHeight: height * 2
    implicitWidth: 44
    implicitHeight: 4

    Rectangle {
        visible: meter.present
        anchors.fill: parent
        radius: height / 2
        color: Theme.track
    }
    Rectangle {
        visible: meter.present && meter.f > 0
        width: Math.max(height, meter.f * meter.width)
        height: parent.height
        radius: height / 2
        color: Theme.scoreInk
        Behavior on width { NumberAnimation { duration: 350; easing.type: Easing.OutCubic } }
    }
    Rectangle {
        visible: meter.present
        x: Math.round(meter.width / 2)
        y: (meter.height - meter.tickHeight) / 2
        width: 1
        height: meter.tickHeight
        color: Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.35)
    }
    Repeater {
        model: meter.present ? 0 : Math.max(2, Math.floor((meter.width - meter.height) / (2.2 * meter.height)) + 1)
        Rectangle {
            required property int index
            x: index * (meter.width - meter.height) / Math.max(1, Math.floor((meter.width - meter.height) / (2.2 * meter.height)))
            width: meter.height
            height: meter.height
            radius: height / 2
            color: Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.18)
        }
    }
}
