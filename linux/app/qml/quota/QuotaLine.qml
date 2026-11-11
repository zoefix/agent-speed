import QtQuick
import QtQuick.Controls

// One window of a Sources row (DESIGN 4 Quota lines): the label in the section's label column, 8 px, the flexible bar,
// 8 px, the percent right-aligned in a `≈100%` cell (a leading `≈` while extrapolated, QUOTAS 10.7), 8 px, the amount
// used with the estimated cap in the section's amount column (QUOTAS 10.8), 8 px, then the reset the section shows with
// ` · ` and the age in tertiary.
Item {
    id: line
    property string label
    property real fraction: 0
    property real pace: -1
    property string percentText
    property bool estimated: false
    // "normal", "warning" (from 80 %) or "critical" (from 95 %).
    property string level: "normal"
    // `US$4,938` and ` / ≈5,742` (or ` / ≥152`); empty while the window has neither.
    property string amount
    property string cap
    property string reset
    property string age
    property string tooltip
    // The estimate's tooltip lines joined by "\n", and its note.
    property string details
    property string note
    property bool passed: false
    // Development aid (--quota-tooltip): the tooltip stays open.
    property bool pinned: false
    property color sourceColor: Theme.accent
    // The section's shared columns.
    property real labelWidth: 0
    property real percentWidth: 0
    property real amountWidth: 0
    property real resetWidth: 0
    readonly property string separator: " · "
    implicitHeight: 16
    Accessible.ignored: true

    readonly property real resetLeft: width - resetWidth
    readonly property real amountLeft: resetLeft - (resetWidth > 0 ? 8 : 0) - amountWidth
    readonly property real percentRight: amountLeft - (amountWidth > 0 ? 8 : 0)
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
        x: line.percentRight - width
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
    // The `≈` in the secondary token, kerned 1 px to the digits; it fades with the percent's crossfade.
    TextMetrics {
        id: digits
        font.pixelSize: 12
        font.weight: Font.DemiBold
        font.features: Theme.tabular
        text: line.percentText
    }
    Text {
        textFormat: Text.PlainText
        x: line.percentRight - digits.advanceWidth - 1 - width
        anchors.verticalCenter: parent.verticalCenter
        text: "≈"
        color: Theme.ink2
        font.pixelSize: 12
        font.weight: Font.DemiBold
        opacity: line.estimated ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
    }
    Row {
        x: line.amountLeft
        width: line.amountWidth
        visible: line.amountWidth > 0
        anchors.verticalCenter: parent.verticalCenter
        clip: true
        QuotaFadeText {
            anchors.verticalCenter: parent.verticalCenter
            text: line.amount
            color: Theme.ink
            weight: Font.Medium
            tabular: true
            elide: Text.ElideNone
        }
        QuotaFadeText {
            anchors.verticalCenter: parent.verticalCenter
            text: line.cap
            color: Theme.ink3
            weight: Font.Medium
            tabular: true
            elide: Text.ElideNone
        }
    }
    Row {
        id: tail
        x: line.resetLeft
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
    QuotaTooltip {
        visible: (hover.hovered || line.pinned) && line.tooltip.length > 0
        delay: line.pinned ? 0 : 600
        head: line.tooltip
        details: line.details
        note: line.note
    }
}
