import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 13: one remote host — status dot, name and destination, the status line with its notes, the actions button and
// the `enabled` switch. Right-click offers the same actions; hovering the status line shows the last error or the
// helper's version.
Item {
    id: row
    required property string hostId
    required property string name
    required property string destination
    required property bool showDestination
    required property bool hostEnabled
    required property string dot
    required property string line
    required property string tooltip
    required property string clockNote
    required property string historyNote
    required property bool canRetry
    required property int index
    property bool divider: true
    signal editRequested()
    signal removeRequested()
    signal menuRequested(Item anchor, real x, real y)

    Layout.fillWidth: true
    implicitHeight: body.implicitHeight + 24
    Accessible.role: Accessible.ListItem
    Accessible.name: name
    Accessible.description: [showDestination ? destination : "", line, clockNote, historyNote].filter(t => t.length > 0).join(". ")

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
        y: body.y + (nameLine.height - height) / 2
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

        RowLayout {
            id: nameLine
            Layout.fillWidth: true
            spacing: 8
            Text {
                id: nameText
                Layout.maximumWidth: row.showDestination ? Math.max(body.width * 0.62, body.width - destinationText.implicitWidth - 8) : body.width
                text: row.name
                color: row.hostEnabled ? Theme.ink : Theme.ink2
                font.pixelSize: 13
                font.weight: Font.DemiBold
                elide: Text.ElideRight
                Behavior on color { ColorAnimation { duration: 160 } }
            }
            Text {
                id: destinationText
                visible: row.showDestination
                Layout.fillWidth: true
                text: row.destination
                color: Theme.ink3
                font.pixelSize: 12
                font.family: "monospace"
                elide: Text.ElideMiddle
            }
            Item { visible: !row.showDestination; Layout.fillWidth: true }
        }
        TextEdit {
            id: lineText
            Layout.fillWidth: true
            text: row.line
            color: Theme.ink2
            font.pixelSize: 12
            wrapMode: TextEdit.Wrap
            readOnly: true
            selectByMouse: true
            selectionColor: Theme.accent
            selectedTextColor: "white"
            activeFocusOnPress: false
            Accessible.ignored: true
            HoverHandler { id: lineHover }
            ToolTip.visible: lineHover.hovered && row.tooltip.length > 0
            ToolTip.delay: 500
            ToolTip.text: row.tooltip
        }
        RowLayout {
            visible: row.clockNote.length > 0
            Layout.fillWidth: true
            spacing: 5
            Glyph {
                Layout.alignment: Qt.AlignTop
                Layout.topMargin: 2
                Layout.preferredWidth: 11
                Layout.preferredHeight: 11
                name: "clock"
                color: Theme.ink3
            }
            Text {
                Layout.fillWidth: true
                text: row.clockNote
                color: Theme.ink2
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
        }
        RowLayout {
            id: historyLine
            visible: opacity > 0
            opacity: row.historyNote.length > 0 ? 1 : 0
            Behavior on opacity { NumberAnimation { duration: 200 } }
            Layout.fillWidth: true
            spacing: 5
            Glyph {
                Layout.alignment: Qt.AlignTop
                Layout.topMargin: 2
                Layout.preferredWidth: 11
                Layout.preferredHeight: 11
                name: "warning"
                color: Theme.dark ? "#FB923C" : "#E86A0C"
            }
            Text {
                Layout.fillWidth: true
                text: row.historyNote
                color: Theme.ink2
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
        }
    }

    Row {
        id: trailing
        anchors.right: parent.right
        anchors.rightMargin: 14
        y: body.y + (nameLine.height - height) / 2
        spacing: 10
        IconButton {
            id: actions
            anchors.verticalCenter: parent.verticalCenter
            width: 26
            height: 26
            glyph: "ellipsis"
            label: S.tr2(S.rev, "remote.actions")
            Accessible.description: row.name
            onClicked: row.menuRequested(actions, 0, actions.height + 2)
            HoverHandler { id: actionsHover }
            ToolTip.visible: actionsHover.hovered
            ToolTip.delay: 600
            ToolTip.text: S.tr2(S.rev, "remote.actions")
        }
        ToggleSwitch {
            anchors.verticalCenter: parent.verticalCenter
            label: Format.fill(S.tr2(S.rev, "remote.enabled"), [row.name])
            checked: row.hostEnabled
            Accessible.description: row.line
            onToggled: (on) => remote.setHostEnabled(row.hostId, on)
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
