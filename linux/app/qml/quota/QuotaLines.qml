import QtQuick
import "Sync.js" as Sync

// A source's quota lines (DESIGN 4): one per window in QUOTAS 2 order, 4 px apart. Lines keep their delegates across
// updates (keyed by label), so bars ease and texts crossfade; a line joining or leaving fades (0.25 s).
Column {
    id: lines
    // [{label, fraction, pace, percentText, estimated, level, amount, cap, reset, age, tooltip, details, note, passed}]
    property var items: []
    property color sourceColor: Theme.accent
    property real labelWidth: 0
    property real percentWidth: 0
    property real amountWidth: 0
    property real resetWidth: 0
    spacing: 4

    // A model row holds no list: the estimate's tooltip lines travel as one text.
    function rows() {
        const out = []
        const source = items || []
        for (let i = 0; i < source.length; ++i) {
            const details = source[i].details
            out.push(Object.assign({}, source[i], { details: typeof details === "string" ? details : Array.from(details || []).join("\n") }))
        }
        return out
    }
    ListModel { id: model }
    onItemsChanged: Sync.sync(model, rows(), "label")
    Component.onCompleted: Sync.sync(model, rows(), "label")

    Repeater {
        model: model
        delegate: QuotaLine {
            required property var model
            label: model.label
            fraction: model.fraction
            pace: model.pace
            percentText: model.percentText
            estimated: model.estimated === true
            level: model.level
            amount: model.amount || ""
            cap: model.cap || ""
            reset: model.reset
            age: model.age
            tooltip: model.tooltip
            details: model.details || ""
            note: model.note || ""
            passed: model.passed
            pinned: model.pinned === true
            width: lines.width
            sourceColor: lines.sourceColor
            labelWidth: lines.labelWidth
            percentWidth: lines.percentWidth
            amountWidth: lines.amountWidth
            resetWidth: lines.resetWidth
            opacity: 0
            Component.onCompleted: opacity = 1
            Behavior on opacity { NumberAnimation { duration: 250; easing.type: Easing.OutCubic } }
        }
    }
}
