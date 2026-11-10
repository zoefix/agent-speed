import QtQuick

// A quota window's bar (DESIGN 4 Quota lines): a 4 px capsule on its track, eased to a new length (0.4 s, 0.6 s when
// the window started over), with the 1 × 8 px pace mark centered on its height while QUOTAS 7 gives one.
Item {
    id: bar
    property real fraction: 0
    // The share of the window that has passed; below 0 while unknown.
    property real pace: -1
    property bool passed: false
    property color fill: Theme.accent
    implicitHeight: 8
    Accessible.ignored: true

    readonly property color ink: Theme.ink
    readonly property real filled: Math.max(0, Math.min(1, fraction)) * width

    Rectangle {
        anchors.verticalCenter: parent.verticalCenter
        width: parent.width
        height: 4
        radius: 2
        color: Qt.rgba(bar.ink.r, bar.ink.g, bar.ink.b, Theme.dark ? 0.12 : 0.08)
    }
    Rectangle {
        anchors.verticalCenter: parent.verticalCenter
        width: bar.filled > 0 ? Math.max(bar.filled, 2) : 0
        height: 4
        radius: 2
        color: bar.fill
        Behavior on width { NumberAnimation { duration: bar.passed ? 600 : 400; easing.type: Easing.OutCubic } }
        Behavior on color { ColorAnimation { duration: 200 } }
    }
    Rectangle {
        id: tick
        readonly property bool known: bar.pace >= 0
        anchors.verticalCenter: parent.verticalCenter
        x: Math.min(Math.max(0, bar.width * Math.max(0, bar.pace) - 0.5), bar.width - 1)
        width: 1
        height: 8
        radius: 0.5
        color: Qt.rgba(bar.ink.r, bar.ink.g, bar.ink.b, 0.35)
        opacity: known ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 200 } }
        Behavior on x { NumberAnimation { duration: 400; easing.type: Easing.OutCubic } }
    }
}
