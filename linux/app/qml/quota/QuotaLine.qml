import QtQuick
import QtQuick.Controls

// One window of a Sources row (DESIGN 4 Quota lines): the label in the section's label column, 8 px, the flexible bar,
// 8 px, the percent right-aligned in a `100%` cell, 8 px, then the long reset with ` · ` and the age in tertiary.
Item {
    id: line
    property string label
    property real fraction: 0
    property real pace: -1
    property string percentText
    // "normal", "warning" (from 80 %) or "critical" (from 95 %).
    property string level: "normal"
    property string reset
    property string age
    property string tooltip
    property bool passed: false
    property color sourceColor: Theme.accent
    // The section's shared columns.
    property real labelWidth: 0
    property real percentWidth: 0
    property real resetWidth: 0
    readonly property string separator: " · "
    implicitHeight: 16
    Accessible.ignored: true

    readonly property color amber: Theme.dark ? "#F5A524" : "#B86E00"
    readonly property color fill: level === "critical" ? Theme.danger : level === "warning" ? amber : sourceColor

    Text {
        id: name
        textFormat: Text.PlainText
        width: line.labelWidth
        anchors.verticalCenter: parent.verticalCenter
        text: line.label
        color: Theme.ink2
        font.pixelSize: 11
        font.weight: Font.Medium
        elide: Text.ElideRight
    }
    QuotaBar {
        anchors.left: name.right
        anchors.leftMargin: 8
        anchors.right: percent.left
        anchors.rightMargin: 8
        anchors.verticalCenter: parent.verticalCenter
        fraction: line.fraction
        pace: line.pace
        passed: line.passed
        fill: line.fill
    }
    QuotaFadeText {
        id: percent
        x: line.width - line.resetWidth - (line.resetWidth > 0 ? 8 : 0) - width
        width: line.percentWidth
        anchors.verticalCenter: parent.verticalCenter
        text: line.percentText
        color: line.level === "critical" ? Theme.danger : Theme.ink
        pixelSize: 12
        weight: Font.DemiBold
        tabular: true
        horizontalAlignment: Text.AlignRight
        elide: Text.ElideNone
    }
    Row {
        id: tail
        x: line.width - line.resetWidth
        width: line.resetWidth
        visible: line.resetWidth > 0
        anchors.verticalCenter: parent.verticalCenter
        clip: true
        QuotaFadeText {
            id: resetText
            width: Math.min(implicitWidth, tail.width)
            anchors.verticalCenter: parent.verticalCenter
            text: line.reset
            color: Theme.ink2
        }
        Text {
            textFormat: Text.PlainText
            anchors.verticalCenter: parent.verticalCenter
            visible: line.age.length > 0
            width: Math.max(0, tail.width - resetText.width)
            text: (line.reset.length > 0 ? line.separator : "") + line.age
            color: Theme.ink3
            font.pixelSize: 11
            elide: Text.ElideRight
        }
    }
    HoverHandler { id: hover }
    ToolTip.visible: hover.hovered && line.tooltip.length > 0
    ToolTip.delay: 600
    ToolTip.text: line.tooltip
}
