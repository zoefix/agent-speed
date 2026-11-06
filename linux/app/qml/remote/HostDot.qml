import QtQuick

// DESIGN 13: a host's status dot — connected green, a breathing grey dot while connecting, orange while retrying, red
// when the user must act, hollow while off. The colors match host_dot.cpp (menus).
Item {
    id: dot
    property string kind: "connected"
    property real size: 8
    implicitWidth: size
    implicitHeight: size
    Accessible.ignored: true

    readonly property color fill: kind === "connected" ? (Theme.dark ? "#4ADE80" : "#1F9D55")
                                  : kind === "retrying" ? (Theme.dark ? "#FB923C" : "#E86A0C")
                                  : kind === "attention" ? (Theme.dark ? "#F87171" : "#D92D20")
                                  : kind === "progress" ? Theme.ink2 : "transparent"
    readonly property bool breathing: kind === "progress" && dot.visible && dot.Window.window !== null && dot.Window.window.visible

    Rectangle {
        id: disc
        anchors.centerIn: parent
        width: dot.size
        height: dot.size
        radius: width / 2
        color: dot.fill
        border.width: dot.kind === "idle" ? 1.2 : 0
        border.color: Theme.ink3
        Behavior on color { ColorAnimation { duration: 220 } }
        SequentialAnimation on opacity {
            running: dot.breathing
            loops: Animation.Infinite
            NumberAnimation { from: 0.95; to: 0.3; duration: 900; easing.type: Easing.InOutSine }
            NumberAnimation { from: 0.3; to: 0.95; duration: 900; easing.type: Easing.InOutSine }
        }
    }
    onBreathingChanged: if (!breathing) disc.opacity = 1
}
