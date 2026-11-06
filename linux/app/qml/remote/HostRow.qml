import QtQuick
import QtQuick.Controls

// DESIGN 13 Dashboard → Hosts: status dot, name, today's output and cost (secondary) and the live value; a host that is
// not connected shows its status word instead. The row opens the usage history on its host.
Item {
    id: row
    required property string key
    required property string name
    required property string dot
    required property bool connected
    required property string word
    required property real live
    required property string liveText
    required property string todayText
    required property string todayCostText
    implicitHeight: 30
    activeFocusOnTab: true

    readonly property string separator: S.language === "ja" ? "、" : S.language === "en" ? ", " : "，"
    function open() { appState.hostHistoryRequested(row.key) }

    Accessible.role: Accessible.Button
    Accessible.name: [name, connected ? (live >= 0.05 ? liveText + " " + S.tr2(S.rev, "unit.tokS") : "") : word,
                      S.tr2(S.rev, "dashboard.today") + " " + S.tr2(S.rev, "today.output") + " " + todayText,
                      S.tr2(S.rev, "today.cost") + " " + todayCostText].filter(t => t.length > 0).join(separator)
    Accessible.description: S.tr2(S.rev, "remote.dashboard.hint")
    Accessible.onPressAction: open()
    Keys.onSpacePressed: open()
    Keys.onReturnPressed: open()

    Rectangle {
        anchors.fill: parent
        anchors.leftMargin: -6
        anchors.rightMargin: -6
        radius: 7
        color: area.pressed ? Theme.pressed : area.containsMouse ? Theme.hover : "transparent"
        border.width: row.activeFocus ? 1.5 : 0
        border.color: Theme.accent
        Behavior on color { ColorAnimation { duration: 120 } }
    }

    HostDot {
        id: dot
        x: 4
        anchors.verticalCenter: parent.verticalCenter
        kind: row.dot
    }
    Text {
        anchors.left: dot.right
        anchors.leftMargin: 10
        anchors.right: today.left
        anchors.rightMargin: 10
        anchors.verticalCenter: parent.verticalCenter
        text: row.name
        color: row.connected ? Theme.ink : Theme.ink2
        font.pixelSize: 13
        elide: Text.ElideMiddle
        Behavior on color { ColorAnimation { duration: 200 } }
    }
    Text {
        id: today
        anchors.right: trailing.left
        anchors.rightMargin: 8
        anchors.verticalCenter: parent.verticalCenter
        text: row.todayCostText.length > 0 ? row.todayText + " · " + row.todayCostText : row.todayText
        color: Theme.ink3
        font.pixelSize: 12
        font.features: Theme.tabular
    }
    Item {
        id: trailing
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        width: Math.max(52, status.implicitWidth)
        height: parent.height
        LiveSlot {
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            value: row.live
            opacity: row.connected ? 1 : 0
            Behavior on opacity { NumberAnimation { duration: 200 } }
        }
        Text {
            id: status
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            text: row.word
            color: Theme.ink3
            font.pixelSize: 12
            opacity: row.connected ? 0 : 1
            Behavior on opacity { NumberAnimation { duration: 200 } }
        }
    }
    MouseArea {
        id: area
        anchors.fill: parent
        hoverEnabled: true
        cursorShape: Qt.PointingHandCursor
        onClicked: row.open()
    }
    ToolTip.visible: area.containsMouse
    ToolTip.delay: 700
    ToolTip.text: S.tr2(S.rev, "remote.dashboard.hint")
}
