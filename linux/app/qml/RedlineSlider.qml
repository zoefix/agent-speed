import QtQuick
import "Heat.js" as Heat

// DESIGN 5: the redline slider (50–2000, step 50) over the heat strip.
Item {
    id: slider
    property int value: 2000
    property int from: 50
    property int to: 2000
    property int step: 50
    signal moved(int value)
    implicitWidth: 260
    implicitHeight: 30
    activeFocusOnTab: true
    Accessible.role: Accessible.Slider
    Accessible.name: String(value)
    Keys.onLeftPressed: moved(Math.max(from, value - step))
    Keys.onRightPressed: moved(Math.min(to, value + step))

    readonly property real fraction: (value - from) / (to - from)

    function valueAt(x) {
        var f = Math.max(0, Math.min(1, (x - knob.width / 2) / (track.width)))
        return Math.round((from + f * (to - from)) / step) * step
    }

    Rectangle {
        id: track
        x: knob.width / 2
        width: parent.width - knob.width
        height: 6
        radius: 3
        anchors.verticalCenter: parent.verticalCenter
        gradient: Gradient {
            orientation: Gradient.Horizontal
            GradientStop { position: 0.0; color: Heat.color(0.0) }
            GradientStop { position: 0.2; color: Heat.color(0.25) }
            GradientStop { position: 0.45; color: Heat.color(0.5) }
            GradientStop { position: 0.7; color: Heat.color(0.75) }
            GradientStop { position: 1.0; color: Heat.color(1.0) }
        }
        opacity: 0.9
    }
    Rectangle {
        id: knob
        width: 18
        height: 18
        radius: 9
        anchors.verticalCenter: parent.verticalCenter
        x: slider.fraction * track.width
        color: "white"
        border.width: slider.activeFocus ? 2.5 : 1
        border.color: slider.activeFocus ? Theme.accent : Qt.rgba(0, 0, 0, 0.18)
        Behavior on x { enabled: !drag.pressed; NumberAnimation { duration: 120 } }
    }
    MouseArea {
        id: drag
        anchors.fill: parent
        cursorShape: Qt.PointingHandCursor
        onPressed: (mouse) => slider.moved(slider.valueAt(mouse.x))
        onPositionChanged: (mouse) => { if (pressed) slider.moved(slider.valueAt(mouse.x)) }
    }
}
