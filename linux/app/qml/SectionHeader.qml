import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// A section title with up to two right-aligned column captions (DESIGN 4).
RowLayout {
    id: header
    property alias text: title.text
    property alias trailing: caption.text
    property alias secondary: second.text
    property string secondaryTip: ""
    // Distance from the right edge to the secondary caption's right edge.
    property real secondaryEdge: 0
    Layout.fillWidth: true
    Layout.bottomMargin: 6
    spacing: 0

    Text {
        id: title
        color: Theme.ink2
        font.pixelSize: 12
        font.weight: Font.DemiBold
        font.letterSpacing: 0.3
    }
    Item { Layout.fillWidth: true }
    Text {
        id: second
        visible: text.length > 0
        Layout.rightMargin: Math.max(0, header.secondaryEdge - (caption.visible ? caption.implicitWidth : 0))
        color: Theme.ink3
        font.pixelSize: 11
        font.weight: Font.Medium
        HoverHandler { id: tipHover }
        ToolTip.visible: tipHover.hovered && header.secondaryTip.length > 0
        ToolTip.delay: 450
        ToolTip.text: header.secondaryTip
    }
    Text {
        id: caption
        visible: text.length > 0
        color: Theme.ink3
        font.pixelSize: 11
        font.weight: Font.Medium
    }
}
