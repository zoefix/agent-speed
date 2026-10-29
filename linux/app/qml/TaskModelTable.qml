import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 3c by-model table: tasks, scored, median with its quartile strip, the rates, attempts and first pass.
Item {
    id: table
    property var rows: []
    property bool wide: true
    property string sortKey: ""
    property bool descending: true
    implicitHeight: 30 + list.count * 30

    readonly property var columns: wide
        ? [{ key: "tasks", title: S.tr2(S.rev, "history.tasks.metric.tasks"), width: 56 },
           { key: "scored", title: S.tr2(S.rev, "history.tasks.metric.scored.short"), tip: S.tr2(S.rev, "history.tasks.metric.scored"), width: 60 },
           { key: "median", title: S.tr2(S.rev, "history.tasks.metric.median.short"), tip: S.tr2(S.rev, "history.tasks.metric.median"), width: 96 },
           { key: "success", title: S.tr2(S.rev, "history.tasks.metric.success"), width: 64, metric: 0 },
           { key: "stall", title: S.tr2(S.rev, "history.tasks.metric.stall"), width: 64, metric: 1 },
           { key: "fixFail", title: S.tr2(S.rev, "history.tasks.metric.fixFail"), width: 72, metric: 2 },
           { key: "attempts", title: S.tr2(S.rev, "history.tasks.metric.attempts.short"), tip: S.tr2(S.rev, "history.tasks.metric.attempts"), width: 72, metric: 3 },
           { key: "firstPass", title: S.tr2(S.rev, "history.tasks.metric.firstPass.short"), tip: S.tr2(S.rev, "history.tasks.metric.firstPass"), width: 84, metric: 4 }]
        : [{ key: "scoredOfTasks", title: S.tr2(S.rev, "history.tasks.column.scoredOfTasks"), width: 84 },
           { key: "median", title: S.tr2(S.rev, "history.tasks.metric.median.short"), tip: S.tr2(S.rev, "history.tasks.metric.median"), width: 88 },
           { key: "success", title: S.tr2(S.rev, "history.tasks.metric.success"), width: 56, metric: 0 },
           { key: "stall", title: S.tr2(S.rev, "history.tasks.metric.stall"), width: 56, metric: 1 },
           { key: "fixFail", title: S.tr2(S.rev, "history.tasks.metric.fixFail"), width: 64, metric: 2 },
           { key: "attempts", title: S.tr2(S.rev, "history.tasks.metric.attempts.short"), tip: S.tr2(S.rev, "history.tasks.metric.attempts"), width: 64, metric: 3 },
           { key: "firstPass", title: S.tr2(S.rev, "history.tasks.metric.firstPass.short"), tip: S.tr2(S.rev, "history.tasks.metric.firstPass"), width: 76, metric: 4 }]

    function sortValue(row, key) {
        if (key === "tasks") return row.tasks
        if (key === "scored" || key === "scoredOfTasks") return row.scored
        if (key === "median") return row.median === null || row.median === undefined ? -1 : row.median
        for (var i = 0; i < row.metrics.length; ++i) {
            if (row.metrics[i].key === key) return row.metrics[i].value === null || row.metrics[i].value === undefined ? -1 : row.metrics[i].value
        }
        return 0
    }
    readonly property var sorted: {
        if (!sortKey) return rows
        var list = rows.slice()
        var key = sortKey, desc = descending
        list.sort(function(a, b) {
            var d = sortValue(a, key) - sortValue(b, key)
            if (d === 0) return a.name < b.name ? -1 : a.name > b.name ? 1 : 0
            return desc ? -d : d
        })
        return list
    }

    RowLayout {
        id: header
        width: parent.width
        height: 30
        spacing: 8
        Text {
            Layout.fillWidth: true
            leftPadding: 10
            text: S.tr2(S.rev, "history.tasks.column.model")
            color: Theme.ink3
            font.pixelSize: 11
            font.weight: Font.Medium
        }
        Repeater {
            model: table.columns
            Text {
                required property var modelData
                Layout.preferredWidth: modelData.width
                horizontalAlignment: Text.AlignRight
                text: modelData.title + (table.sortKey === modelData.key ? (table.descending ? " ↓" : " ↑") : "")
                color: headerHover.hovered ? Theme.ink2 : Theme.ink3
                font.pixelSize: 11
                font.weight: Font.Medium
                elide: Text.ElideLeft
                HoverHandler { id: headerHover; cursorShape: Qt.PointingHandCursor }
                TapHandler {
                    onTapped: {
                        if (table.sortKey === modelData.key) table.descending = !table.descending
                        else { table.sortKey = modelData.key; table.descending = true }
                    }
                }
                ToolTip.visible: headerHover.hovered && (modelData.tip || "").length > 0
                ToolTip.delay: 300
                ToolTip.text: modelData.tip || ""
            }
        }
        Item { Layout.preferredWidth: 2 }
    }
    Rectangle { anchors.top: header.bottom; width: parent.width; height: 1; color: Theme.hairline }

    ListView {
        id: list
        anchors.top: header.bottom
        anchors.topMargin: 1
        width: parent.width
        height: count * 30
        interactive: false
        model: table.sorted
        delegate: Rectangle {
            id: rowItem
            required property var modelData
            required property int index
            width: list.width
            height: 30
            color: rowHover.hovered ? Theme.hover : index % 2 === 1 ? Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.025) : "transparent"
            readonly property color ink: modelData.low ? Theme.ink2 : Theme.ink
            HoverHandler { id: rowHover }
            RowLayout {
                anchors.fill: parent
                spacing: 8
                Row {
                    Layout.fillWidth: true
                    Layout.leftMargin: 10
                    spacing: 6
                    Item {
                        width: 7 + Math.max(0, rowItem.modelData.sources.length - 1) * 4
                        height: 30
                        Repeater {
                            model: rowItem.modelData.sources
                            Rectangle {
                                required property var modelData
                                required property int index
                                x: index * 4
                                anchors.verticalCenter: parent.verticalCenter
                                width: 9
                                height: 9
                                radius: 4.5
                                color: rowItem.color.a > 0 ? rowItem.color : Theme.card
                                Rectangle { anchors.centerIn: parent; width: 7; height: 7; radius: 3.5; color: Theme.sourceColor(parent.modelData) }
                            }
                        }
                    }
                    Text {
                        anchors.verticalCenter: parent.verticalCenter
                        width: Math.min(implicitWidth, parent.width - 60)
                        text: rowItem.modelData.name
                        color: rowItem.ink
                        font.pixelSize: 12
                        font.weight: Font.Medium
                        elide: Text.ElideMiddle
                        HoverHandler { id: nameHover }
                        ToolTip.visible: nameHover.hovered
                        ToolTip.delay: 400
                        ToolTip.text: rowItem.modelData.sourcesText
                    }
                    Rectangle {
                        visible: rowItem.modelData.low
                        anchors.verticalCenter: parent.verticalCenter
                        width: lowLabel.implicitWidth + 12
                        height: 16
                        radius: 8
                        color: Theme.field
                        Text { id: lowLabel; anchors.centerIn: parent; text: S.tr2(S.rev, "history.tasks.sample.low"); color: Theme.ink2; font.pixelSize: 10 }
                    }
                }
                Repeater {
                    model: table.columns
                    Item {
                        required property var modelData
                        Layout.preferredWidth: modelData.width
                        Layout.fillHeight: true
                        readonly property var m: modelData.metric !== undefined ? rowItem.modelData.metrics[modelData.metric] : null
                        Text {
                            anchors.right: parent.right
                            anchors.verticalCenter: parent.verticalCenter
                            anchors.rightMargin: parent.modelData.key === "median" ? 56 : 0
                            text: {
                                var k = parent.modelData.key
                                if (k === "tasks") return rowItem.modelData.tasks
                                if (k === "scored") return rowItem.modelData.scored
                                if (k === "scoredOfTasks") return rowItem.modelData.scored + " / " + rowItem.modelData.tasks
                                if (k === "median") return rowItem.modelData.medianText
                                return parent.m ? parent.m.text : "–"
                            }
                            color: parent.m && (parent.m.value === null || parent.m.value === undefined) ? Theme.ink3 : rowItem.ink
                            font.pixelSize: 12
                            font.features: Theme.tabular
                        }
                        // The median's quartile strip.
                        Item {
                            visible: parent.modelData.key === "median" && rowItem.modelData.median !== null && rowItem.modelData.median !== undefined
                            anchors.right: parent.right
                            anchors.verticalCenter: parent.verticalCenter
                            width: 48
                            height: 4
                            Rectangle { anchors.fill: parent; radius: 2; color: Theme.track }
                            Rectangle {
                                x: parent.width * (rowItem.modelData.q1 || 0) / 100
                                width: Math.max(2, parent.width * ((rowItem.modelData.q3 || 0) - (rowItem.modelData.q1 || 0)) / 100)
                                height: 4
                                radius: 2
                                color: Theme.sourceColor(rowItem.modelData.sources[0] || "")
                                opacity: 0.45
                            }
                            Rectangle { x: parent.width * (rowItem.modelData.median || 0) / 100 - 0.75; y: -1; width: 1.5; height: 6; color: Theme.ink }
                        }
                    }
                }
                Item { Layout.preferredWidth: 2 }
            }
        }
    }
}
