import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 5 Entry row: icon, the path (the automatic entry may name several, its first line badged 預設), a missing
// location dimmed with ⚠ 未找到資料夾, then Show in File Manager and Remove. Right-click offers both.
Item {
    id: row
    required property string key
    required property bool automatic
    required property var lines
    required property string path
    required property bool exists
    required property bool canReveal
    required property bool isFile
    required property int index
    property bool canRemove: true
    // Animates in when added after the list was shown, and out before it is removed.
    property bool appear: false
    property bool leaving: false
    signal revealRequested()
    signal removeRequested()

    readonly property string removeLabel: Format.fill(S.tr2(S.rev, "settings.sources.removeLocation"), [row.path])
    readonly property string revealLabel: S.tr2(S.rev, "linux.settings.sources.reveal")

    Layout.fillWidth: true
    implicitHeight: (body.implicitHeight + 18) * presence
    clip: true
    opacity: presence

    property real presence: 1
    NumberAnimation {
        id: arrive
        target: row
        property: "presence"
        from: 0
        to: 1
        duration: 200
        easing.type: Easing.OutCubic
    }
    NumberAnimation {
        id: depart
        target: row
        property: "presence"
        to: 0
        duration: 180
        easing.type: Easing.InCubic
    }
    Component.onCompleted: if (appear) arrive.start()
    onLeavingChanged: if (leaving) depart.start()

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
        onTapped: (point) => menu.popup(row, point.position.x, point.position.y)
    }

    LocationIcon {
        id: icon
        x: 14
        y: body.y + (firstLine.height - height) / 2
        file: row.isFile
        opacity: row.exists ? 1 : 0.45
    }

    ColumnLayout {
        id: body
        anchors.left: parent.left
        anchors.leftMargin: 46
        anchors.right: buttons.left
        anchors.rightMargin: 10
        anchors.verticalCenter: parent.verticalCenter
        spacing: 3
        // One element for the screen reader: the folder, its paths, 預設, and 未找到資料夾 when missing.
        Accessible.role: Accessible.StaticText
        Accessible.name: S.tr2(S.rev, "linux.settings.sources.folder")
        Accessible.description: {
            var parts = []
            for (var i = 0; i < row.lines.length; ++i) parts.push(row.lines[i].text)
            if (row.automatic) parts.push(S.tr2(S.rev, "settings.sources.default"))
            if (!row.exists) parts.push(S.tr2(S.rev, "source.folderMissing"))
            return parts.join(", ")
        }

        Repeater {
            model: row.lines
            RowLayout {
                id: line
                required property var modelData
                required property int index
                Layout.fillWidth: true
                spacing: 8
                Component.onCompleted: if (index === 0) row.firstLineItem = line
                Text {
                    Layout.fillWidth: !badge.visible
                    Layout.maximumWidth: body.width - (badge.visible ? badge.implicitWidth + 8 : 0)
                    text: line.modelData.text
                    // An automatic entry with several folders dims only the missing ones.
                    color: line.modelData.readable || (row.lines.length === 1 && row.exists) ? Theme.ink : Theme.ink3
                    font.pixelSize: 13
                    elide: Text.ElideMiddle
                    Accessible.ignored: true
                    HoverHandler { id: pathHover }
                    ToolTip.visible: pathHover.hovered
                    ToolTip.delay: 600
                    ToolTip.text: line.modelData.full
                }
                DefaultBadge {
                    id: badge
                    visible: row.automatic && line.index === 0
                    text: S.tr2(S.rev, "settings.sources.default")
                }
                Item { visible: badge.visible; Layout.fillWidth: true }
            }
        }
        RowLayout {
            visible: !row.exists
            Layout.fillWidth: true
            spacing: 5
            Glyph {
                Layout.preferredWidth: 11
                Layout.preferredHeight: 11
                name: "warning"
                color: Theme.dark ? "#FB923C" : "#E86A0C"
                Accessible.ignored: true
            }
            Text {
                Layout.fillWidth: true
                text: S.tr2(S.rev, "source.folderMissing")
                color: Theme.ink2
                font.pixelSize: 11
                elide: Text.ElideRight
                Accessible.ignored: true
            }
        }
    }
    property Item firstLineItem: null
    readonly property Item firstLine: firstLineItem ? firstLineItem : body

    Row {
        id: buttons
        anchors.right: parent.right
        anchors.rightMargin: 10
        y: body.y + (row.firstLine.height - height) / 2
        spacing: 2
        IconButton {
            enabled: row.canReveal
            opacity: enabled ? 1 : 0.35
            glyph: "search"
            label: row.revealLabel
            Accessible.description: row.path
            onClicked: row.revealRequested()
            HoverHandler { id: revealHover }
            ToolTip.visible: revealHover.hovered && enabled
            ToolTip.delay: 600
            ToolTip.text: row.revealLabel
        }
        // The disabled Remove keeps its tooltip (keep one entry), so the hover lives on a wrapper.
        Item {
            implicitWidth: removeButton.implicitWidth
            implicitHeight: removeButton.implicitHeight
            HoverHandler { id: removeHover }
            ToolTip.visible: removeHover.hovered
            ToolTip.delay: 600
            ToolTip.text: row.canRemove ? S.tr2(S.rev, "settings.sources.remove") : S.tr2(S.rev, "settings.sources.keepOne")
            IconButton {
                id: removeButton
                enabled: row.canRemove
                opacity: enabled ? 1 : 0.35
                glyph: "minusCircle"
                label: row.removeLabel
                onClicked: row.removeRequested()
            }
        }
    }

    Menu {
        id: menu
        MenuItem {
            text: row.revealLabel
            enabled: row.canReveal
            onTriggered: row.revealRequested()
        }
        MenuItem {
            text: S.tr2(S.rev, "settings.sources.remove")
            enabled: row.canRemove
            onTriggered: row.removeRequested()
        }
    }

    Rectangle {
        anchors.left: parent.left
        anchors.leftMargin: 46
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        color: Theme.hairline
    }
}
