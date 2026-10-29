import QtQuick
import "Format.js" as Format

// The trailing tok/s slot of a row (DESIGN 4): digits, or an hourglass for tools and an en dash otherwise when the value
// would show as 0.
Item {
    id: slot
    property real value: 0
    property string phase: "idle"
    property int pixelSize: 13
    readonly property bool resting: value < 0.05
    implicitWidth: 52
    implicitHeight: digits.implicitHeight

    Text {
        id: digits
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        text: Format.speed(slot.value)
        color: Theme.ink
        font.pixelSize: slot.pixelSize
        font.weight: Font.Medium
        font.features: Theme.tabular
        opacity: slot.resting ? 0 : 1
        Behavior on opacity { NumberAnimation { duration: 200 } }
    }
    Text {
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        text: "–"
        color: Theme.ink3
        font.pixelSize: slot.pixelSize
        opacity: slot.resting && slot.phase !== "tools" ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 200 } }
    }
    Glyph {
        id: hourglass
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        width: 11
        height: 11
        name: "hourglass"
        color: Theme.ink2
        property real breath: 1
        opacity: slot.resting && slot.phase === "tools" ? breath : 0
        Behavior on opacity { NumberAnimation { duration: 200 } }
        SequentialAnimation on breath {
            loops: Animation.Infinite
            running: slot.resting && slot.phase === "tools" && slot.Window.window !== null && slot.Window.window.visible
            NumberAnimation { from: 1; to: 0.55; duration: 1000; easing.type: Easing.InOutSine }
            NumberAnimation { from: 0.55; to: 1; duration: 1000; easing.type: Easing.InOutSine }
        }
    }
}
