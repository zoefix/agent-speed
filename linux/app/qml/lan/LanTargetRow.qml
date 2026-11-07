import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 14: one target — status dot, name (the address when it has none), `UDP · address`, the status line, the
// actions button and the `enabled` switch. Right-click offers the same actions.
Item {
    id: row
    required property string targetId
    required property string title
    required property string detail
    required property bool targetEnabled
    required property string dot
    required property string line
    required property int index
    property bool divider: true
    signal menuRequested(Item anchor, real x, real y)

    Layout.fillWidth: true
    implicitHeight: body.implicitHeight + 24
    Accessible.role: Accessible.ListItem
    Accessible.name: title
    Accessible.description: detail + ". " + line

    Rectangle {
        anchors.fill: parent
        anchors.margins: 3
        radius: 8
        color: rowHover.hovered ? Theme.hover : "transparent"
        Behavior on color { ColorAnimation { duration: 140 } }
    }
    HoverHandler { id: rowHover }
    TapHandler {
        acceptedButtons: Qt.RightButton
        onTapped: (point) => row.menuRequested(row, point.position.x, point.position.y)
    }

    HostDot {
        x: 15
        y: body.y + (nameText.height - height) / 2
        kind: row.dot
    }

    ColumnLayout {
        id: body
        anchors.left: parent.left
        anchors.leftMargin: 33
        anchors.right: trailing.left
        anchors.rightMargin: 12
        anchors.verticalCenter: parent.verticalCenter
        spacing: 3
        Text {
            id: nameText
            Layout.fillWidth: true
            text: row.title
            color: row.targetEnabled ? Theme.ink : Theme.ink2
            font.pixelSize: 13
            font.weight: Font.DemiBold
            elide: Text.ElideRight
            Behavior on color { ColorAnimation { duration: 160 } }
        }
        Text {
            Layout.fillWidth: true
            text: row.detail
            color: Theme.ink3
            font.pixelSize: 12
            font.family: "monospace"
            elide: Text.ElideMiddle
        }
        Text {
            Layout.fillWidth: true
            text: row.line
            color: Theme.ink2
            font.pixelSize: 12
            wrapMode: Text.Wrap
            Accessible.ignored: true
        }
    }

    Row {
        id: trailing
        anchors.right: parent.right
        anchors.rightMargin: 14
        y: body.y + (nameText.height - height) / 2
        spacing: 10
        IconButton {
            id: actions
            anchors.verticalCenter: parent.verticalCenter
            width: 26
            height: 26
            glyph: "ellipsis"
            label: S.tr2(S.rev, "remote.actions")
            Accessible.description: row.title
            onClicked: row.menuRequested(actions, 0, actions.height + 2)
            HoverHandler { id: actionsHover }
            ToolTip.visible: actionsHover.hovered
            ToolTip.delay: 600
            ToolTip.text: S.tr2(S.rev, "remote.actions")
        }
        ToggleSwitch {
            anchors.verticalCenter: parent.verticalCenter
            label: Format.fill(S.tr2(S.rev, "lan.target.enabled"), [row.title])
            checked: row.targetEnabled
            Accessible.description: row.line
            onToggled: (on) => lan.setTargetEnabled(row.targetId, on)
        }
    }

    Rectangle {
        visible: row.divider
        anchors.left: parent.left
        anchors.leftMargin: 33
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        color: Theme.hairline
    }
}
