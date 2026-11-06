import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 5 Actions row: Add Folder… then Reset, leading-aligned; at the limit Add is disabled and the limit reads at the
// trailing end and as Add's tooltip.
Item {
    id: actions
    property bool canAdd: true
    property bool canReset: false
    signal addRequested()
    signal resetRequested()

    readonly property string limitText: Format.fill(S.tr2(S.rev, "settings.sources.limit"), [locations.maxEntries])

    Layout.fillWidth: true
    implicitHeight: content.implicitHeight + 22

    RowLayout {
        id: content
        anchors.left: parent.left
        anchors.leftMargin: 14
        anchors.right: parent.right
        anchors.rightMargin: 14
        anchors.verticalCenter: parent.verticalCenter
        spacing: 8
        Item {
            implicitWidth: addButton.implicitWidth
            implicitHeight: addButton.implicitHeight
            HoverHandler { id: addHover }
            ToolTip.visible: addHover.hovered && !actions.canAdd
            ToolTip.delay: 400
            ToolTip.text: actions.limitText
            PillButton {
                id: addButton
                enabled: actions.canAdd
                text: S.tr2(S.rev, "settings.sources.add")
                Accessible.description: actions.canAdd ? "" : actions.limitText
                onClicked: actions.addRequested()
            }
        }
        PillButton {
            enabled: actions.canReset
            text: S.tr2(S.rev, "settings.sources.reset")
            onClicked: actions.resetRequested()
        }
        Item { Layout.fillWidth: true }
        Text {
            Layout.maximumWidth: content.width * 0.5
            opacity: actions.canAdd ? 0 : 1
            visible: opacity > 0
            Behavior on opacity { NumberAnimation { duration: 180 } }
            text: actions.limitText
            color: Theme.ink3
            font.pixelSize: 11
            elide: Text.ElideRight
            horizontalAlignment: Text.AlignRight
        }
    }
}
