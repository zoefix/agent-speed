import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtCore

// DESIGN 3c: task performance in the Usage History window — two scored sources side by side (median, distribution,
// five comparison rows) and the models across sources, over the window's range (METRICS 6.11 Comparison).
Rectangle {
    id: section
    property string first: ""
    property string last: ""
    property string source: "all"

    readonly property var comparison: history.tasksRevision >= 0 && S.rev >= 0
                                ? history.taskComparison(first, last, source === "all" ? "" : source) : ({})
    readonly property var sides: comparison.sources || []
    readonly property var models: comparison.byModel || []
    readonly property bool wide: width >= 860
    readonly property real centerWidth: wide ? 140 : 120
    readonly property real distWidth: wide ? 120 : 96
    readonly property bool single: sides.length === 1
    readonly property bool empty: sides.length === 0

    implicitHeight: content.implicitHeight + 36
    radius: 12
    color: Theme.card
    border.width: 1
    border.color: Theme.border

    Settings {
        id: remembered
        category: "history"
        property string taskPickA: ""
        property string taskPickB: ""
    }

    function sideOf(id) {
        for (var i = 0; i < sides.length; ++i) if (sides[i].source === id) return sides[i]
        return null
    }
    // The pair: the remembered one, else Codex and Claude Code, else the two with the most tasks.
    readonly property var pair: {
        if (sides.length < 2) return [sides.length ? sides[0].source : "", ""]
        var a = remembered.taskPickA, b = remembered.taskPickB
        if (a && b && a !== b && (sideOf(a) || sideOf(b))) return [a, b]
        if (sideOf("codex") && sideOf("claudeCode")) return ["codex", "claudeCode"]
        var byTasks = sides.slice().sort(function(x, y) { return y.tasks - x.tasks })
        return [byTasks[0].source, byTasks[1].source]
    }
    readonly property var sideA: sideOf(pair[0])
    readonly property var sideB: sideOf(pair[1])

    function pickerOptions(other) {
        var list = []
        for (var i = 0; i < sides.length; ++i) {
            if (sides[i].source !== other) list.push({ value: sides[i].source, label: sides[i].name })
        }
        return list
    }
    function metric(side, index) { return side ? side.metrics[index] : null }
    // DESIGN 3c "Better": both sides with 10 scored tasks, a clear difference and different texts.
    function better(index) {
        var a = metric(sideA, index), b = metric(sideB, index)
        if (!a || !b || a.value === undefined || b.value === undefined || a.value === null || b.value === null) return 0
        if (sideA.scored < 10 || sideB.scored < 10 || a.text === b.text) return 0
        var rate = index < 3
        var diff = Math.abs(a.value - b.value)
        if (rate ? diff < 0.01 : diff < 0.05 * Math.max(a.value, b.value)) return 0
        var aWins = a.lowerBetter ? a.value < b.value : a.value > b.value
        return aWins ? -1 : 1
    }
    function scaleOf(index) {
        if (index < 3) return 1
        var a = metric(sideA, index), b = metric(sideB, index)
        var top = Math.max(a && a.value ? a.value : 0, b && b.value ? b.value : 0) * 1.25
        return Math.max(top, index === 3 ? 3 : 300000)
    }

    ColumnLayout {
        id: content
        x: 18
        y: 18
        width: section.width - 36
        spacing: 0

        // Header: title, ⓘ, and the two pickers.
        RowLayout {
            Layout.fillWidth: true
            spacing: 8
            Text {
                text: S.tr2(S.rev, "history.tasks.title")
                color: Theme.ink
                font.pixelSize: 15
                font.weight: Font.DemiBold
            }
            Text {
                id: info
                text: "ⓘ"
                color: infoHover.hovered ? Theme.ink2 : Theme.ink3
                font.pixelSize: 13
                HoverHandler { id: infoHover }
                ToolTip.visible: infoHover.hovered
                ToolTip.delay: 300
                ToolTip.text: {
                    var lines = [S.tr2(S.rev, "history.tasks.info"), ""]
                    var keys = ["scored", "median", "success", "stall", "fixFail", "attempts", "firstPass"]
                    for (var i = 0; i < keys.length; ++i) {
                        lines.push(S.tr2(S.rev, "history.tasks.metric." + keys[i]) + "：" + S.tr2(S.rev, "history.tasks.metric." + keys[i] + ".hint"))
                    }
                    return lines.join("\n")
                }
            }
            Item { Layout.fillWidth: true }
            Text {
                visible: section.single && section.source === "all"
                text: section.single ? S.tr2(S.rev, "history.tasks.onlyOne").replace("%@", section.sides[0].name) : ""
                color: Theme.ink3
                font.pixelSize: 11
            }
            Dropdown {
                visible: section.sides.length >= 2
                value: section.pair[0]
                popupWidth: 200
                options: section.pickerOptions(section.pair[1])
                onActivated: (v) => { remembered.taskPickA = v; remembered.taskPickB = section.pair[1] }
            }
            Text {
                visible: section.sides.length >= 2
                text: S.tr2(S.rev, "history.tasks.vs")
                color: Theme.ink3
                font.pixelSize: 11
            }
            Dropdown {
                visible: section.sides.length >= 2
                value: section.pair[1]
                popupWidth: 200
                options: section.pickerOptions(section.pair[0])
                onActivated: (v) => { remembered.taskPickB = v; remembered.taskPickA = section.pair[0] }
            }
        }

        // Empty and re-analysing states.
        Column {
            visible: section.empty
            Layout.fillWidth: true
            Layout.topMargin: 22
            Layout.bottomMargin: 8
            spacing: 8
            Glyph { anchors.horizontalCenter: parent.horizontalCenter; name: "gauge"; width: 26; height: 26; color: Theme.ink3 }
            Text {
                anchors.horizontalCenter: parent.horizontalCenter
                text: history.analyzing ? S.tr2(S.rev, "history.tasks.analyzing") : S.tr2(S.rev, "history.tasks.empty")
                color: Theme.ink2
                font.pixelSize: 13
                font.weight: Font.Medium
            }
            Text {
                visible: !history.analyzing
                width: Math.min(parent.width, 520)
                anchors.horizontalCenter: parent.horizontalCenter
                horizontalAlignment: Text.AlignHCenter
                text: S.tr2(S.rev, "history.tasks.empty.hint")
                color: Theme.ink3
                font.pixelSize: 12
                wrapMode: Text.WordWrap
            }
        }

        // Side headers.
        RowLayout {
            visible: !section.empty
            Layout.fillWidth: true
            Layout.topMargin: 16
            spacing: 0
            TaskSide {
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                side: section.sideA
                mirrored: !section.single
                distWidth: section.distWidth
            }
            Item { visible: !section.single; Layout.preferredWidth: section.centerWidth }
            TaskSide {
                visible: !section.single
                Layout.fillWidth: true
                Layout.preferredWidth: 1
                side: section.sideB
                mirrored: false
                distWidth: section.distWidth
            }
        }

        // Comparison rows (the butterfly), or one bar per metric for a single source.
        Rectangle { visible: !section.empty; Layout.fillWidth: true; Layout.topMargin: 14; height: 1; color: Theme.hairline }
        Repeater {
            model: section.empty ? 0 : 5
            Item {
                id: compareRow
                required property int index
                readonly property var a: section.metric(section.sideA, index)
                readonly property var b: section.metric(section.sideB, index)
                readonly property int winner: section.single ? 0 : section.better(index)
                readonly property real scale: section.scaleOf(index)
                Layout.fillWidth: true
                implicitHeight: 36

                // Two sources: value · track | name | track · value
                Row {
                    visible: !section.single
                    anchors.fill: parent
                    TaskBar {
                        width: (parent.width - section.centerWidth) / 2
                        height: parent.height
                        metric: compareRow.a
                        color: Theme.sourceColor(section.pair[0])
                        mirrored: true
                        scale: compareRow.scale
                        rank: compareRow.winner === -1 ? "better" : compareRow.winner === 1 ? "worse" : ""
                    }
                    Column {
                        width: section.centerWidth
                        anchors.verticalCenter: parent.verticalCenter
                        Text {
                            width: parent.width
                            horizontalAlignment: Text.AlignHCenter
                            text: compareRow.a ? compareRow.a.label : compareRow.b ? compareRow.b.label : ""
                            color: Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.85)
                            font.pixelSize: 12
                            font.weight: Font.Medium
                            wrapMode: Text.WordWrap
                            maximumLineCount: 2
                        }
                        Text {
                            visible: compareRow.index > 0
                            width: parent.width
                            horizontalAlignment: Text.AlignHCenter
                            text: S.tr2(S.rev, "history.tasks.lowerBetter")
                            color: Theme.ink3
                            font.pixelSize: 10
                        }
                    }
                    TaskBar {
                        width: (parent.width - section.centerWidth) / 2
                        height: parent.height
                        metric: compareRow.b
                        color: Theme.sourceColor(section.pair[1])
                        mirrored: false
                        scale: compareRow.scale
                        rank: compareRow.winner === 1 ? "better" : compareRow.winner === -1 ? "worse" : ""
                    }
                }
                // One source: label · bar · value
                RowLayout {
                    visible: section.single
                    anchors.fill: parent
                    spacing: 12
                    Text {
                        Layout.preferredWidth: 180
                        text: compareRow.a ? compareRow.a.label : ""
                        color: Theme.ink
                        font.pixelSize: 12
                        font.weight: Font.Medium
                        elide: Text.ElideRight
                    }
                    TaskBar {
                        Layout.fillWidth: true
                        Layout.preferredHeight: parent.height
                        metric: compareRow.a
                        color: Theme.sourceColor(section.pair[0])
                        mirrored: false
                        scale: compareRow.scale
                    }
                }
            }
        }

        // By model.
        Text {
            visible: section.models.length > 0
            Layout.topMargin: 16
            Layout.bottomMargin: 8
            text: S.tr2(S.rev, "history.tasks.byModel")
            color: Theme.ink
            font.pixelSize: 13
            font.weight: Font.DemiBold
        }
        TaskModelTable {
            visible: section.models.length > 0
            Layout.fillWidth: true
            rows: section.models
            wide: section.wide
        }

        Text {
            visible: !section.empty
            Layout.fillWidth: true
            Layout.topMargin: 12
            text: S.tr2(S.rev, "history.tasks.footnote")
            color: Theme.ink3
            font.pixelSize: 11
            wrapMode: Text.WordWrap
        }
    }
}
