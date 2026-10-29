import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtCore
import "History.js" as H
import "Format.js" as Format

// DESIGN 4b: the Usage History window — source and range, summary cards, the daily chart and the daily table.
Window {
    id: win
    width: 980
    height: 720
    minimumWidth: 760
    minimumHeight: 560
    title: S.tr2(S.rev, "history.title")
    color: Theme.panel

    Settings {
        category: "history"
        property alias range: win.range
        property alias windowWidth: win.width
        property alias windowHeight: win.height
    }

    property string source: "all"
    property string range: "30d"
    readonly property string today: H.iso(new Date())
    readonly property string currency: prefs.currency
    readonly property string rate: S.rev >= 0 ? catalog.rateFor(currency) : "1"
    readonly property var rows: history.rows

    readonly property string firstDate: rows.length > 0 ? rows[0].date : today
    readonly property var span: H.bounds(range, firstDate)
    readonly property var dayList: H.days(rows, source, span[0], span[1])
    readonly property var total: H.totals(dayList)
    readonly property var previousSpan: {
        var n = H.dayCount(span[0], span[1])
        var end = H.addDays(H.parse(span[0]), -1)
        return [H.iso(H.addDays(end, -(n - 1))), H.iso(end)]
    }
    readonly property var previousTotal: H.totals(H.days(rows, source, previousSpan[0], previousSpan[1]))
    readonly property bool singleDay: span[0] === span[1]
    readonly property var sourcesWithHistory: {
        var seen = {}
        for (var i = 0; i < rows.length; ++i) seen[rows[i].source] = true
        var order = ["codex", "claudeCode", "claudeDesktop", "deepseek", "opencode", "gemini", "omp", "antigravity", "workbuddy",
                     "workbuddyAi", "zcode", "kimi", "grokBuild", "pi", "openclaw", "hermes", "droid"]
        return order.filter(function(s) { return seen[s] && prefs.disabledSources.indexOf(s) < 0 })
    }
    readonly property var chartSeries: {
        if (singleDay || dayList.length === 0) return dayList
        // Every day of the range, empty days included, so gaps show.
        var out = [], byDate = {}
        for (var i = 0; i < dayList.length; ++i) byDate[dayList[i].date] = dayList[i]
        var start = range === "all" || range === "year" ? H.parse(dayList[0].date) : H.parse(span[0])
        for (var d = start; H.iso(d) <= span[1]; d = H.addDays(d, 1)) out.push(byDate[H.iso(d)] || H.emptyDay(H.iso(d)))
        return out
    }

    function present(sourceId) {
        if (sourceId !== undefined && sourceId.length > 0) source = sourceId
        show()
        raise()
        requestActivate()
        history.refreshRequested()
    }

    function money(micro) { return history.money(micro, currency, rate, S.language) }
    function rangeLabel() {
        var n = H.dayCount(span[0], span[1])
        var a = span[0].replace(/-/g, "/"), b = span[1].replace(/-/g, "/")
        return (a === b ? a : a + " – " + b) + " · " + Format.fill(S.tr2(S.rev, "history.summary.activeDays"), [total.activeDays])
    }
    function sourceCode(id) {
        var codes = { claudeCode: "claude-code", claudeDesktop: "claude-desktop", workbuddyAi: "workbuddy-ai" }
        return id === "all" ? "all" : (codes[id] || id)
    }

    Shortcut {
        sequences: [StandardKey.Close]
        onActivated: win.close()
    }

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 24
        spacing: 18

        // Toolbar
        RowLayout {
            Layout.fillWidth: true
            spacing: 12
            Dropdown {
                value: win.source
                popupWidth: 230
                options: {
                    var list = [{ value: "all", label: S.tr2(S.rev, "history.allSources") }]
                    for (var i = 0; i < win.sourcesWithHistory.length; ++i) {
                        var s = win.sourcesWithHistory[i]
                        list.push({ value: s, label: S.tr2(S.rev, "source." + s) })
                    }
                    return list
                }
                onActivated: (v) => win.source = v
            }
            Item { Layout.fillWidth: true }
            Segmented {
                value: win.range
                options: [
                    { value: "today", label: S.tr2(S.rev, "history.range.today") },
                    { value: "yesterday", label: S.tr2(S.rev, "history.range.yesterday") },
                    { value: "7d", label: S.tr2(S.rev, "history.range.7d") },
                    { value: "30d", label: S.tr2(S.rev, "history.range.30d") },
                    { value: "90d", label: S.tr2(S.rev, "history.range.90d") },
                    { value: "year", label: S.tr2(S.rev, "history.range.year") },
                    { value: "all", label: S.tr2(S.rev, "history.range.all") }
                ]
                onActivated: (v) => win.range = v
            }
            Item { Layout.fillWidth: true }
            PillButton {
                text: S.tr2(S.rev, "history.export")
                enabled: win.dayList.length > 0
                onClicked: {
                    var name = "agent-speed-history-" + win.sourceCode(win.source) + "-" + win.span[0] + "-" + win.span[1] + ".csv"
                    var path = history.chooseCsvPath(name)
                    if (path.length > 0) history.exportCsv(path, win.dayList.slice().reverse(), win.sourceCode(win.source), win.currency, win.rate)
                }
            }
        }
        Text {
            Layout.topMargin: -8
            text: win.rangeLabel()
            color: Theme.ink3
            font.pixelSize: 12
            font.features: Theme.tabular
        }

        // Summary
        RowLayout {
            Layout.fillWidth: true
            spacing: 12
            readonly property var outputs: win.chartSeries.map(function(d) { return d.output })
            SummaryCard {
                title: S.tr2(S.rev, "history.summary.output")
                value: H.compact(win.total.output)
                deltaText: H.delta(win.total.output, win.previousTotal.output)
                deltaGood: win.total.output >= win.previousTotal.output
                detail: win.total.best ? Format.fill(S.tr2(S.rev, "history.summary.bestDay"), [win.total.best.date.substr(5).replace("-", "/")]) : ""
                series: parent.outputs
                tone: win.source === "all" ? Theme.accent : Theme.sourceColor(win.source)
            }
            SummaryCard {
                title: width < 170 ? S.tr2(S.rev, "history.summary.input.short") : S.tr2(S.rev, "history.summary.input")
                value: H.compact(win.total.input)
                deltaText: H.delta(win.total.input, win.previousTotal.input)
                deltaGood: true
                series: win.chartSeries.map(function(d) { return d.input })
                tone: Theme.ink2
            }
            SummaryCard {
                title: S.tr2(S.rev, "history.summary.requests")
                value: H.grouped(win.total.requests)
                deltaText: H.delta(win.total.requests, win.previousTotal.requests)
                deltaGood: true
                detail: Format.fill(S.tr2(S.rev, "history.summary.activeDays"), [win.total.activeDays])
                series: win.chartSeries.map(function(d) { return d.requests })
                tone: Theme.ink2
            }
            SummaryCard {
                readonly property real avg: H.speed(win.total.measuredOutput, win.total.measuredMs)
                title: S.tr2(S.rev, "history.summary.speed")
                value: avg >= 0 ? Format.speed(avg) : "–"
                unit: avg >= 0 ? "tok/s" : ""
                deltaText: H.delta(avg, H.speed(win.previousTotal.measuredOutput, win.previousTotal.measuredMs))
                deltaGood: avg >= H.speed(win.previousTotal.measuredOutput, win.previousTotal.measuredMs)
                series: win.chartSeries.map(function(d) { return Math.max(0, H.speed(d.measuredOutput, d.measuredMs)) })
                tone: Theme.ink2
            }
            SummaryCard {
                title: S.tr2(S.rev, "history.summary.cost")
                value: win.total.hasCost ? (win.total.unpriced > 0 ? "≥" : "") + win.money(win.total.cost) : "–"
                detail: win.total.unpriced > 0 ? Format.fill(S.tr2(S.rev, "history.unpricedDay"), [win.total.unpriced])
                        : win.total.activeDays > 0 && win.total.hasCost
                          ? Format.fill(S.tr2(S.rev, "history.summary.costPerDay"), [win.money(win.total.cost / Math.max(1, H.dayCount(win.span[0], win.span[1])))]) : ""
                series: win.chartSeries.map(function(d) { return d.cost || 0 })
                tone: Theme.ink2
            }
        }

        // Chart, or the single day's breakdown
        Rectangle {
            Layout.fillWidth: true
            Layout.preferredHeight: 280
            radius: 12
            color: Theme.card
            border.width: 1
            border.color: Theme.border
            Text {
                x: 16
                y: 12
                text: win.singleDay ? (win.source === "all" ? S.tr2(S.rev, "history.chart.bySource") : S.tr2(S.rev, "history.chart.byModel"))
                                    : S.tr2(S.rev, "history.chart.output")
                color: Theme.ink2
                font.pixelSize: 12
                font.weight: Font.DemiBold
            }
            HistoryChart {
                visible: !win.singleDay && win.dayList.length > 0
                anchors.fill: parent
                anchors.topMargin: 34
                anchors.margins: 10
                series: win.chartSeries
                source: win.source
                today: win.today
                sourceOrder: {
                    var totals = win.total.bySource
                    return Object.keys(totals).sort(function(a, b) { return totals[b] - totals[a] })
                }
                onPicked: (date) => table.highlight(date)
            }
            Column {
                visible: win.singleDay && win.dayList.length > 0
                anchors.fill: parent
                anchors.topMargin: 40
                anchors.leftMargin: 16
                anchors.rightMargin: 16
                spacing: 8
                Repeater {
                    model: {
                        var t = win.total
                        var list = []
                        if (win.source === "all") {
                            for (var s in t.bySource) list.push({ name: S.tr2(S.rev, "source." + s), color: Theme.sourceColor(s), output: t.bySource[s] })
                        } else {
                            for (var m in t.models) list.push({ name: m, color: Theme.sourceColor(win.source), output: t.models[m].output })
                        }
                        list.sort(function(a, b) { return b.output - a.output })
                        return list.slice(0, 8)
                    }
                    RowLayout {
                        required property var modelData
                        width: parent.width
                        spacing: 10
                        Text {
                            Layout.preferredWidth: 150
                            text: modelData.name
                            color: Theme.ink
                            font.pixelSize: 12
                            elide: Text.ElideRight
                        }
                        Rectangle {
                            Layout.fillWidth: true
                            Layout.preferredHeight: 14
                            radius: 4
                            color: Theme.field
                            Rectangle {
                                width: parent.width * (win.total.output > 0 ? modelData.output / win.total.output : 0)
                                height: parent.height
                                radius: 4
                                color: modelData.color
                                Behavior on width { NumberAnimation { duration: 350; easing.type: Easing.OutCubic } }
                            }
                        }
                        Text {
                            Layout.preferredWidth: 110
                            horizontalAlignment: Text.AlignRight
                            text: H.compact(modelData.output) + " · " + (win.total.output > 0 ? Math.round(modelData.output / win.total.output * 100) : 0) + "%"
                            color: Theme.ink2
                            font.pixelSize: 12
                            font.features: Theme.tabular
                        }
                    }
                }
            }
            Column {
                visible: win.dayList.length === 0
                anchors.centerIn: parent
                spacing: 8
                Glyph { anchors.horizontalCenter: parent.horizontalCenter; name: "gauge"; width: 28; height: 28; color: Theme.ink3 }
                Text {
                    anchors.horizontalCenter: parent.horizontalCenter
                    text: S.tr2(S.rev, "history.empty")
                    color: Theme.ink2
                    font.pixelSize: 14
                    font.weight: Font.Medium
                }
                Text {
                    anchors.horizontalCenter: parent.horizontalCenter
                    text: S.tr2(S.rev, "history.empty.hint")
                    color: Theme.ink3
                    font.pixelSize: 12
                }
            }
        }

        // Daily table
        Rectangle {
            Layout.fillWidth: true
            Layout.fillHeight: true
            radius: 12
            color: Theme.card
            border.width: 1
            border.color: Theme.border
            clip: true
            readonly property var columns: [
                { key: "date", title: S.tr2(S.rev, "history.table.date"), width: 1.4, align: Text.AlignLeft },
                { key: "requests", title: S.tr2(S.rev, "history.table.requests"), width: 0.8 },
                { key: "output", title: S.tr2(S.rev, "history.table.output"), width: 0.9 },
                { key: "input", title: S.tr2(S.rev, "history.table.input"), width: 1.1 },
                { key: "cost", title: S.tr2(S.rev, "history.table.cost"), width: 0.9 },
                { key: "cacheHit", title: S.tr2(S.rev, "history.table.cacheHit"), width: 0.8 },
                { key: "speed", title: S.tr2(S.rev, "history.table.speed"), width: 0.9 },
                { key: "peak", title: S.tr2(S.rev, "history.table.peak"), width: 0.8 },
                { key: "agents", title: S.tr2(S.rev, "history.table.agents"), width: 0.6 },
                { key: "models", title: S.tr2(S.rev, "history.table.models"), width: 1.8, align: Text.AlignLeft }
            ]
            readonly property real unit: (width - 32) / columns.reduce(function(a, c) { return a + c.width }, 0)

            function cell(day, key) {
                switch (key) {
                case "date":
                    var label = day.date === win.today ? S.tr2(S.rev, "history.today")
                              : day.date === H.iso(H.addDays(new Date(), -1)) ? S.tr2(S.rev, "history.yesterday") : day.date.replace(/-/g, "/")
                    return label + "  " + H.parse(day.date).toLocaleDateString(Qt.locale(S.language === "en" ? "en_US" : S.language.replace("-", "_")), "ddd")
                case "requests": return H.grouped(day.requests)
                case "output": return H.compact(day.output)
                case "input": return H.compact(day.input)
                case "cost": return day.hasCost ? (day.unpriced > 0 ? "≥" : "") + win.money(day.cost) : "–"
                case "cacheHit": return day.input > 0 ? Math.round(day.cachedInput / day.input * 100) + "%" : "–"
                case "speed": var sp = H.speed(day.measuredOutput, day.measuredMs); return sp >= 0 ? Format.speed(sp) : S.tr2(S.rev, "history.noSpeed")
                case "peak": return day.peak ? Format.speed(day.peak) : "–"
                case "agents": return String(day.agents)
                case "models":
                    var list = Object.keys(day.models).map(function(k) { return day.models[k] }).sort(function(a, b) { return b.output - a.output })
                    var names = list.slice(0, 2).map(function(m) { return m.name })
                    return names.join(", ") + (list.length > 2 ? " +" + (list.length - 2) : "")
                }
                return ""
            }

            Row {
                id: headerRow
                x: 16
                y: 10
                height: 26
                Repeater {
                    model: parent.parent.columns
                    Text {
                        required property var modelData
                        width: modelData.width * headerRow.parent.unit
                        height: headerRow.height
                        verticalAlignment: Text.AlignVCenter
                        horizontalAlignment: modelData.align === undefined ? Text.AlignRight : modelData.align
                        rightPadding: modelData.align === undefined ? 10 : 0
                        text: modelData.title
                        color: Theme.ink3
                        font.pixelSize: 11
                        font.weight: Font.DemiBold
                        elide: Text.ElideRight
                    }
                }
            }
            Rectangle {
                anchors.top: headerRow.bottom
                anchors.left: parent.left
                anchors.right: parent.right
                height: 1
                color: Theme.hairline
            }
            ListView {
                id: table
                anchors.top: headerRow.bottom
                anchors.topMargin: 2
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.bottom: parent.bottom
                anchors.bottomMargin: 6
                clip: true
                boundsBehavior: Flickable.StopAtBounds
                model: win.dayList.slice().reverse()
                property string flash: ""
                function highlight(date) {
                    for (var i = 0; i < count; ++i) if (model[i].date === date) { positionViewAtIndex(i, ListView.Center); break }
                    flash = date
                    flashTimer.restart()
                }
                Timer { id: flashTimer; interval: 1200; onTriggered: table.flash = "" }
                ScrollBar.vertical: ScrollBar { policy: ScrollBar.AsNeeded }
                delegate: Rectangle {
                    id: rowItem
                    required property var modelData
                    required property int index
                    width: table.width
                    height: 32
                    color: table.flash === modelData.date ? Qt.rgba(Theme.accent.r, Theme.accent.g, Theme.accent.b, 0.14)
                           : rowArea.containsMouse ? Theme.hover : index % 2 === 1 ? Qt.rgba(0, 0, 0, Theme.dark ? 0.08 : 0.018) : "transparent"
                    Behavior on color { ColorAnimation { duration: 200 } }
                    Row {
                        x: 16
                        height: parent.height
                        Repeater {
                            model: table.parent.columns
                            Text {
                                required property var modelData
                                width: modelData.width * table.parent.unit
                                height: rowItem.height
                                verticalAlignment: Text.AlignVCenter
                                horizontalAlignment: modelData.align === undefined ? Text.AlignRight : modelData.align
                                rightPadding: modelData.align === undefined ? 10 : 0
                                text: table.parent.cell(rowItem.modelData, modelData.key)
                                color: modelData.key === "date" ? Theme.ink : modelData.key === "models" ? Theme.ink2 : Theme.ink
                                font.pixelSize: 12
                                font.features: Theme.tabular
                                font.weight: modelData.key === "date" ? Font.Medium : Font.Normal
                                elide: Text.ElideRight
                            }
                        }
                    }
                    MouseArea { id: rowArea; anchors.fill: parent; hoverEnabled: true }
                }
            }
        }

        // Footer
        RowLayout {
            Layout.fillWidth: true
            spacing: 10
            Rectangle {
                visible: history.analyzing
                Layout.preferredWidth: 80
                Layout.preferredHeight: 3
                radius: 1.5
                color: Theme.field
                clip: true
                Rectangle {
                    width: parent.width * 0.35
                    height: parent.height
                    radius: 1.5
                    color: Theme.accent
                    SequentialAnimation on x {
                        running: history.analyzing
                        loops: Animation.Infinite
                        NumberAnimation { from: -28; to: 80; duration: 1100; easing.type: Easing.InOutQuad }
                    }
                }
            }
            Text {
                text: history.analyzing ? Format.fill(S.tr2(S.rev, "history.analyzing"), [""]).replace(" %", "").replace("%", "").trim()
                                        : history.updatedMs > 0 ? Format.fill(S.tr2(S.rev, "history.updated"), [new Date(history.updatedMs).toLocaleTimeString(Qt.locale(), "HH:mm")]) : ""
                color: Theme.ink3
                font.pixelSize: 11
            }
            Item { Layout.fillWidth: true }
            Text {
                text: width < 300 ? S.tr2(S.rev, "history.privacy.short") : S.tr2(S.rev, "history.privacy")
                color: Theme.ink3
                font.pixelSize: 11
                elide: Text.ElideRight
                Layout.maximumWidth: 520
            }
        }
    }
}
