import QtQuick

// DESIGN 4 phase badge: three shimmering dots in the source color while generating, a wrench while tools run, a grey
// dot when idle.
Item {
    id: badge
    property string phase: "idle"
    property color tint: "#7C8798"
    property real clock: 0
    implicitWidth: 16
    implicitHeight: 16

    NumberAnimation on clock {
        from: 0; to: 1
        duration: 1200
        loops: Animation.Infinite
        running: badge.phase === "generating" && badge.Window.window !== null && badge.Window.window.visible
    }

    function wave(index) {
        var t = badge.clock - index * 0.16
        t -= Math.floor(t)
        return 0.5 - 0.5 * Math.cos(t * Math.PI * 2)
    }

    Row {
        anchors.centerIn: parent
        spacing: 1.6
        opacity: badge.phase === "generating" ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 200 } }
        Repeater {
            model: 3
            Rectangle {
                required property int index
                width: 4
                height: 4
                radius: 2
                color: badge.tint
                opacity: 0.3 + 0.7 * badge.wave(index)
                transform: Translate { y: -1.2 * badge.wave(index) }
            }
        }
    }

    Glyph {
        anchors.centerIn: parent
        width: 13
        height: 13
        name: "wrench"
        color: Theme.ink2
        opacity: badge.phase === "tools" ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 200 } }
    }

    Rectangle {
        anchors.centerIn: parent
        width: 6
        height: 6
        radius: 3
        color: Theme.ink3
        opacity: badge.phase === "idle" ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 200 } }
    }
}
