import QtQuick

// One source: colored dot, name, today's output (secondary) and the live value; under it the source's quota lines
// (DESIGN 4 Quota lines) or, for Claude Code, the invitation to connect its status line.
Item {
    id: row
    required property string source
    required property string name
    required property string todayText
    required property real live
    // The quota lines of the source and the section's shared columns.
    property var quotaItems: []
    property string quotaSpoken: ""
    property bool invite: false
    property real labelWidth: 0
    property real percentWidth: 0
    property real resetWidth: 0
    signal inviteAccepted()
    signal inviteDismissed()

    readonly property bool hasQuota: quotaItems.length > 0 || invite
    readonly property real nameInset: dot.x + dot.width + 10
    implicitHeight: 30 + (hasQuota ? quota.height + 6 : 0)
    Behavior on implicitHeight { NumberAnimation { duration: 250; easing.type: Easing.OutCubic } }

    Accessible.role: Accessible.StaticText
    Accessible.name: [name, todayText, quotaSpoken].filter(t => t.length > 0).join(S.tr2(S.rev, "list.separator"))

    Rectangle {
        anchors.fill: parent
        anchors.leftMargin: -6
        anchors.rightMargin: -6
        radius: 7
        color: hover.hovered ? Theme.hover : "transparent"
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    HoverHandler { id: hover }

    Item {
        id: head
        width: parent.width
        height: 30

        Rectangle {
            id: dot
            x: 4
            anchors.verticalCenter: parent.verticalCenter
            width: 8
            height: 8
            radius: 4
            color: Theme.sourceColor(row.source)
        }
        Text {
            anchors.left: dot.right
            anchors.leftMargin: 10
            anchors.right: today.left
            anchors.rightMargin: 10
            anchors.verticalCenter: parent.verticalCenter
            text: row.name
            color: Theme.ink
            font.pixelSize: 13
            elide: Text.ElideRight
        }
        Text {
            id: today
            anchors.right: slot.left
            anchors.rightMargin: 8
            anchors.verticalCenter: parent.verticalCenter
            text: row.todayText
            color: Theme.ink3
            font.pixelSize: 12
            font.features: Theme.tabular
        }
        LiveSlot {
            id: slot
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            value: row.live
        }
    }

    Item {
        id: quota
        x: row.nameInset
        y: 29
        width: row.width - row.nameInset
        height: row.invite ? invitation.implicitHeight : lines.implicitHeight
        opacity: row.hasQuota ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 250 } }

        QuotaLines {
            id: lines
            visible: !row.invite
            width: parent.width
            items: row.invite ? [] : row.quotaItems
            sourceColor: Theme.sourceColor(row.source)
            labelWidth: row.labelWidth
            percentWidth: row.percentWidth
            resetWidth: row.resetWidth
        }
        QuotaInvite {
            id: invitation
            visible: row.invite
            width: parent.width
            onAccepted: row.inviteAccepted()
            onDismissed: row.inviteDismissed()
        }
    }
}
