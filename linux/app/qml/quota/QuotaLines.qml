import QtQuick
import "Sync.js" as Sync

// A source's quota lines (DESIGN 4): one per window in QUOTAS 2 order, 4 px apart. Lines keep their delegates across
// updates (keyed by label), so bars ease and texts crossfade; a line joining or leaving fades (0.25 s).
Column {
    id: lines
    // [{label, fraction, pace, percentText, level, reset, age, tooltip, passed}]
    property var items: []
    property color sourceColor: Theme.accent
    property real labelWidth: 0
    property real percentWidth: 0
    property real resetWidth: 0
    spacing: 4

    ListModel { id: model }
    onItemsChanged: Sync.sync(model, items || [], "label")
    Component.onCompleted: Sync.sync(model, items || [], "label")

    Repeater {
        model: model
        delegate: QuotaLine {
            required property var model
            label: model.label
            fraction: model.fraction
            pace: model.pace
            percentText: model.percentText
            level: model.level
            reset: model.reset
            age: model.age
            tooltip: model.tooltip
            passed: model.passed
            width: lines.width
            sourceColor: lines.sourceColor
            labelWidth: lines.labelWidth
            percentWidth: lines.percentWidth
            resetWidth: lines.resetWidth
            opacity: 0
            Component.onCompleted: opacity = 1
            Behavior on opacity { NumberAnimation { duration: 250; easing.type: Easing.OutCubic } }
        }
    }
}
