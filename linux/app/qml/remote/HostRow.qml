import QtQuick
import QtQuick.Controls

// DESIGN 13 Dashboard → Hosts: status dot, name, today's output and cost (secondary) and the live value; a host that is
// not connected shows its status word instead. The row opens the usage history on its host. Under it, each source of
// the host with a quota: a sub-line with the source's dot and name, then its quota lines indented under the name.
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
    // [{source, name, spoken, lines}] and the section's shared columns (DESIGN 13 Host quotas).
    property var quotaSources: []
    property real labelWidth: 0
    property real percentWidth: 0
    property real resetWidth: 0
    readonly property real nameInset: dot.x + dot.width + 10
    implicitHeight: 30 + (quotaSources.length > 0 ? quota.implicitHeight + 6 : 0)
    Behavior on implicitHeight { NumberAnimation { duration: 250; easing.type: Easing.OutCubic } }
    activeFocusOnTab: true

    readonly property string separator: S.language === "ja" ? "、" : S.language === "en" ? ", " : "，"
    function open() { appState.hostHistoryRequested(row.key) }

    Accessible.role: Accessible.Button
    Accessible.name: [name, connected ? (live >= 0.05 ? liveText + " " + S.tr2(S.rev, "unit.tokS") : "") : word,
                      S.tr2(S.rev, "dashboard.today") + " " + S.tr2(S.rev, "today.output") + " " + todayText,
                      S.tr2(S.rev, "today.cost") + " " + todayCostText]
                      .concat(quotaSources.map(q => q.name + " " + q.spoken)).filter(t => t.length > 0).join(separator)
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

    Item {
        id: head
        width: parent.width
        height: 30
        HostDot {
            id: dot
            x: 4
            anchors.verticalCenter: parent.verticalCenter
            kind: row.dot
        }
        Text {
            textFormat: Text.PlainText
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
            textFormat: Text.PlainText
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
                textFormat: Text.PlainText
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                text: row.word
                color: Theme.ink3
                font.pixelSize: 12
                opacity: row.connected ? 0 : 1
                Behavior on opacity { NumberAnimation { duration: 200 } }
            }
        }
    }

    Column {
        id: quota
        x: row.nameInset
        y: 29
        width: row.width - row.nameInset
        spacing: 7
        opacity: row.quotaSources.length > 0 ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 250 } }
        Repeater {
            model: row.quotaSources
            Column {
                required property var modelData
                width: quota.width
                spacing: 5
                Row {
                    spacing: 6
                    Rectangle {
                        anchors.verticalCenter: parent.verticalCenter
                        width: 6
                        height: 6
                        radius: 3
                        color: Theme.sourceColor(modelData.source)
                    }
                    Text {
                        textFormat: Text.PlainText
                        text: modelData.name
                        color: Theme.ink2
                        font.pixelSize: 11
                    }
                }
                QuotaLines {
                    x: 12
                    width: parent.width - 12
                    items: modelData.lines
                    sourceColor: Theme.sourceColor(modelData.source)
                    labelWidth: row.labelWidth
                    percentWidth: row.percentWidth
                    resetWidth: row.resetWidth
                }
            }
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
