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
        id: remembered
        category: "history"
        property alias range: win.range
        property alias windowWidth: win.width
        property alias windowHeight: win.height
        property alias customFirst: win.customFirst
        property alias customLast: win.customLast
        property var includeSubscriptions: null
    }

    property string source: "all"
    property string range: "30d"
    // DESIGN 4b: 自訂 keeps its own dates.
    property string customFirst: ""
    property string customLast: ""
    // DESIGN 9 Subscriptions: the window's own choice, initially the setting.
    readonly property bool includeSubscriptions: remembered.includeSubscriptions === null || remembered.includeSubscriptions === undefined
                                                 ? prefs.includeSubscriptions : remembered.includeSubscriptions === true
    readonly property var subscribed: prefs.subscriptions
    readonly property var excluded: includeSubscriptions ? [] : subscribed
    // DESIGN 9 / 10 Usage history: the chart shows output, cost or traffic.
    property string metric: "output"
    readonly property var trafficDays: H.trafficByDate(history.trafficRows, source, span[0], span[1], prefs.disabledSources, prefs.trafficIncludeTools)
    readonly property var trafficTotal: {
        var t = { rx: 0, tx: 0 }
        for (var date in trafficDays) { t.rx += trafficDays[date].rx; t.tx += trafficDays[date].tx }
        return t
    }
    // Opening on traffic: a range with agent traffic but no token usage never opens on an empty chart.
    function openOnTraffic() {
        if (metric !== "traffic" && total.output <= 0 && trafficTotal.rx + trafficTotal.tx > 0) metric = "traffic"
    }
    onSourceChanged: openOnTraffic()
    onRangeChanged: openOnTraffic()
    Component.onCompleted: {
        // Development aids: --history-metric=<output|cost|traffic>, --history-range=<today|…|all>, --history-popover (the
        // unknown-prices popover), --history-hover (the chart's tooltip on the day before today).
        for (var i = 0; i < Qt.application.arguments.length; ++i) {
            var arg = Qt.application.arguments[i]
            if (arg.indexOf("--history-metric=") === 0) metric = arg.substr(17)
            if (arg.indexOf("--history-range=") === 0) range = arg.substr(16)
            if (arg === "--history-popover") devPopover = true
            if (arg === "--history-hover") devHover = true
        }
        openOnTraffic()
    }
    property bool devPopover: false
    property bool devHover: false
    Timer {
        interval: 300
        repeat: true
        running: win.devPopover || win.devHover
        onTriggered: {
            if (win.devPopover && win.total.unpriced > 0) { unpricedPopup.open(); win.devPopover = false }
            if (win.devHover && win.chartSeries.length > 1) { dailyChart.hover = win.chartSeries.length - 2; win.devHover = false }
        }
    }
    readonly property string today: H.iso(new Date())
    readonly property string currency: prefs.currency
    readonly property string rate: S.rev >= 0 && catalog.revision >= 0 ? catalog.rateFor(currency) : "1"
    readonly property var rows: history.rows

    readonly property string firstDate: rows.length > 0 ? rows[0].date : today
    readonly property var span: range === "custom" && customFirst.length > 0 && customLast.length > 0 ? [customFirst, customLast] : H.bounds(range, firstDate)
    readonly property var dayList: H.days(rows, source, span[0], span[1], history.trafficRows, prefs.disabledSources, prefs.trafficIncludeTools, excluded)
    readonly property var total: H.totals(dayList)
    readonly property var previousSpan: {
        var n = H.dayCount(span[0], span[1])
        var end = H.addDays(H.parse(span[0]), -1)
        return [H.iso(H.addDays(end, -(n - 1))), H.iso(end)]
    }
    readonly property var previousTotal: H.totals(H.days(rows, source, previousSpan[0], previousSpan[1], [], [], false, excluded))
    // DESIGN 4b Summary: the change against the previous period (the day before for a single day).
    function versus(now, before) {
        const change = H.delta(now, before)
        return change.length > 0 ? Format.fill(S.tr2(S.rev, singleDay ? "history.summary.vsPreviousDay" : "history.summary.vsPrevious"), [change]) : ""
    }
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
                        list.push({ value: s, label: S.tr2(S.rev, "source." + s)
                                           + (win.subscribed.indexOf(s) >= 0 ? "  · " + S.tr2(S.rev, "cost.subscription.tag") : "") })
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
                    { value: "all", label: S.tr2(S.rev, "history.range.all") },
                    { value: "custom", label: S.tr2(S.rev, "history.range.custom") }
                ]
                onActivated: (v) => {
                    if (v === "custom") customRange.openWith(win.span)
                    else win.range = v
                }
                // 自訂: two dates clamped to the data, the end never before the start, and 套用.
                Popup {
                    id: customRange
                    y: parent.height + 6
                    x: parent.width - width
                    width: 260
                    padding: 14
                    background: Rectangle { radius: 10; color: Theme.cardFill; border.width: 1; border.color: Theme.cardLine }
                    property string fromText: ""
                    property string toText: ""
                    function openWith(span) {
                        fromText = span[0]
                        toText = span[1]
                        open()
                    }
                    function valid(text) { return /^\d{4}-\d{2}-\d{2}$/.test(text) && !isNaN(H.parse(text).getTime()) && H.iso(H.parse(text)) === text }
                    readonly property bool ok: valid(fromText) && valid(toText)
                    function apply() {
                        if (!ok) return
                        var first = fromText < win.firstDate ? win.firstDate : fromText
                        var last = toText > win.today ? win.today : toText
                        if (last < first) last = first
                        win.customFirst = first
                        win.customLast = last
                        win.range = "custom"
                        close()
                    }
                    contentItem: GridLayout {
                        columns: 2
                        columnSpacing: 10
                        rowSpacing: 8
                        Text { text: S.tr2(S.rev, "history.range.from"); color: Theme.ink2; font.pixelSize: 12 }
                        FieldInput {
                            id: fromField
                            Layout.fillWidth: true
                            placeholder: "YYYY-MM-DD"
                            accessibleName: S.tr2(S.rev, "history.range.from")
                            text: customRange.fromText
                            onEdited: (t) => customRange.fromText = t
                            onCommitted: (t) => { customRange.fromText = t; if (focused) customRange.apply() }
                        }
                        Text { text: S.tr2(S.rev, "history.range.to"); color: Theme.ink2; font.pixelSize: 12 }
                        FieldInput {
                            id: toField
                            Layout.fillWidth: true
                            placeholder: "YYYY-MM-DD"
                            accessibleName: S.tr2(S.rev, "history.range.to")
                            text: customRange.toText
                            onEdited: (t) => customRange.toText = t
                            onCommitted: (t) => { customRange.toText = t; if (focused) customRange.apply() }
                        }
                        Item { Layout.preferredWidth: 1 }
                        PillButton {
                            Layout.alignment: Qt.AlignRight
                            accent: true
                            enabled: customRange.ok
                            text: S.tr2(S.rev, "history.range.apply")
                            onClicked: customRange.apply()
                        }
                    }
                }
            }
            // DESIGN 9 Subscriptions: shown while a source is marked.
            ToggleSwitch {
                visible: win.subscribed.length > 0
                label: S.tr2(S.rev, "history.includeSubscriptions")
                checked: win.includeSubscriptions
                onToggled: (on) => remembered.includeSubscriptions = on
            }
            Text {
                visible: win.subscribed.length > 0
                text: S.tr2(S.rev, "history.includeSubscriptions")
                color: Theme.ink2
                font.pixelSize: 12
                TapHandler { onTapped: remembered.includeSubscriptions = !win.includeSubscriptions }
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

        // Summary, chart, task performance and the daily table scroll together.
        Flickable {
            id: page
            Layout.fillWidth: true
            Layout.fillHeight: true
            contentWidth: width
            contentHeight: body.implicitHeight
            boundsBehavior: Flickable.StopAtBounds
            clip: true
            ScrollBar.vertical: ScrollBar { policy: page.contentHeight > page.height ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff }
            ColumnLayout {
                id: body
                width: page.width
                spacing: 18
            // Summary
            RowLayout {
                Layout.fillWidth: true
                spacing: 12
                readonly property var outputs: win.chartSeries.map(function(d) { return d.output })
                SummaryCard {
                    title: S.tr2(S.rev, "history.summary.output")
                    value: H.compact(win.total.output)
                    deltaText: win.versus(win.total.output, win.previousTotal.output)
                    deltaGood: win.total.output >= win.previousTotal.output
                    detail: win.total.best ? Format.fill(S.tr2(S.rev, "history.summary.bestDay"), [win.total.best.date.substr(5).replace("-", "/")]) : ""
                    series: parent.outputs
                    tone: win.source === "all" ? Theme.accent : Theme.sourceColor(win.source)
                }
                SummaryCard {
                    title: width < 170 ? S.tr2(S.rev, "history.summary.input.short") : S.tr2(S.rev, "history.summary.input")
                    value: H.compact(win.total.input)
                    deltaText: win.versus(win.total.input, win.previousTotal.input)
                    deltaGood: true
                    series: win.chartSeries.map(function(d) { return d.input })
                    tone: Theme.ink2
                }
                SummaryCard {
                    title: S.tr2(S.rev, "history.summary.requests")
                    value: H.grouped(win.total.requests)
                    deltaText: win.versus(win.total.requests, win.previousTotal.requests)
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
                    deltaText: win.versus(avg, H.speed(win.previousTotal.measuredOutput, win.previousTotal.measuredMs))
                    deltaGood: avg >= H.speed(win.previousTotal.measuredOutput, win.previousTotal.measuredMs)
                    series: win.chartSeries.map(function(d) { return Math.max(0, H.speed(d.measuredOutput, d.measuredMs)) })
                    tone: Theme.ink2
                }
                SummaryCard {
                    // A subscription source chosen directly shows its API-equivalent cost; a day's cost never changes.
                    title: win.source !== "all" && win.subscribed.indexOf(win.source) >= 0 ? S.tr2(S.rev, "history.summary.costApi") : S.tr2(S.rev, "history.summary.cost")
                    HoverHandler { id: costCardHover }
                    ToolTip.visible: costCardHover.hovered
                    ToolTip.delay: 600
                    ToolTip.text: S.tr2(S.rev, "history.costFrozen")
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
                        : win.metric === "traffic" ? S.tr2(S.rev, "history.chart.traffic") + "  ·  "
                                                     + Format.fill(S.tr2(S.rev, "traffic.pair"), [history.bytes(win.trafficTotal.rx), history.bytes(win.trafficTotal.tx)])
                        : win.metric === "cost" ? S.tr2(S.rev, "history.chart.cost") : S.tr2(S.rev, "history.chart.output")
                    color: Theme.ink2
                    font.pixelSize: 12
                    font.weight: Font.DemiBold
                }
                Segmented {
                    visible: !win.singleDay
                    anchors.right: parent.right
                    anchors.rightMargin: 12
                    y: 8
                    value: win.metric
                    options: [
                        { value: "output", label: S.tr2(S.rev, "history.chart.output") },
                        { value: "cost", label: S.tr2(S.rev, "history.chart.cost") },
                        { value: "traffic", label: S.tr2(S.rev, "history.chart.traffic") }
                    ]
                    onActivated: (v) => win.metric = v
                }
                HistoryChart {
                    id: dailyChart
                    visible: !win.singleDay && win.dayList.length > 0
                    anchors.fill: parent
                    anchors.topMargin: 34
                    anchors.margins: 10
                    series: win.chartSeries
                    source: win.source
                    metric: win.metric
                    traffic: win.trafficDays
                    trafficSince: history.trafficSince
                    includeTools: prefs.trafficIncludeTools
                    formatMoney: function(v) { return win.money(v) }
                    formatBytes: function(v) { return history.bytes(v) }
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

            // Effort and speed (DESIGN 4b, 3b)
            ModeSection {
                Layout.fillWidth: true
                first: win.span[0]
                last: win.span[1]
                source: win.source
            }

            // Performance (DESIGN 12, 性能表現)
            PerfSection {
                Layout.fillWidth: true
                first: win.span[0]
                last: win.span[1]
                source: win.source
            }

            // Daily table
            Rectangle {
                Layout.fillWidth: true
                Layout.preferredHeight: Math.max(240, Math.min(52 + win.dayList.length * 30, page.height - 24))
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
                    { key: "traffic", title: S.tr2(S.rev, "history.table.traffic"), width: 2.0 },
                    { key: "cacheHit", title: S.tr2(S.rev, "history.table.cacheHit"), width: 0.8 },
                    { key: "speed", title: S.tr2(S.rev, "history.table.speed"), width: 0.9 },
                    { key: "peak", title: S.tr2(S.rev, "history.table.peak"), width: 0.8 },
                    { key: "agents", title: S.tr2(S.rev, "history.table.agents"), width: 0.6 },
                    { key: "models", title: S.tr2(S.rev, "history.table.models"), width: 1.5, align: Text.AlignLeft }
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
                    case "traffic":
                        var t = history.trafficRows.length >= 0 ? history.trafficOf(day.date, win.source, prefs.trafficIncludeTools) : ({})
                        return t.recorded ? Format.fill(S.tr2(S.rev, "traffic.pair"), [t.rxText, t.txText]) : "–"
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
                // Cell tooltips: unpriced or missing cost data, traffic not yet recorded or partial.
                function hint(day, key) {
                    if (key === "cost") {
                        if (!day.hasCost && day.requests > 0) return S.tr2(S.rev, "history.costUnavailable")
                        return day.unpriced > 0 ? Format.fill(S.tr2(S.rev, "history.unpricedDay"), [day.unpriced]) : ""
                    }
                    if (key === "traffic") {
                        if (history.trafficSince.length === 0 || day.date < history.trafficSince) return S.tr2(S.rev, "history.traffic.notRecorded")
                        var t = win.trafficDays[day.date]
                        if (H.trafficPartial(t, day.date, win.today))
                            return Format.fill(S.tr2(S.rev, "history.traffic.partial"), [H.duration(H.recordedMs(t), function(k) { return S.tr2(S.rev, k) })])
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
                                    readonly property string hint: table.parent.hint(rowItem.modelData, modelData.key)
                                    HoverHandler { id: cellHover; enabled: parent.hint.length > 0 }
                                    ToolTip.visible: cellHover.hovered && hint.length > 0
                                    ToolTip.delay: 500
                                    ToolTip.text: hint
                                }
                            }
                        }
                        MouseArea { id: rowArea; anchors.fill: parent; hoverEnabled: true }
                    }
                }
            }
            }
        }

        // Footer
        RowLayout {
            id: footer
            Layout.fillWidth: true
            spacing: 10
            readonly property bool counted: history.analyzing && history.filesTotal > 0
            readonly property real fraction: counted ? Math.min(1, history.filesDone / history.filesTotal) : 0
            Rectangle {
                visible: history.analyzing
                Layout.preferredWidth: 80
                Layout.preferredHeight: 3
                radius: 1.5
                color: Theme.field
                clip: true
                Rectangle {
                    visible: !footer.counted
                    width: parent.width * 0.35
                    height: parent.height
                    radius: 1.5
                    color: Theme.accent
                    SequentialAnimation on x {
                        running: history.analyzing && !footer.counted
                        loops: Animation.Infinite
                        NumberAnimation { from: -28; to: 80; duration: 1100; easing.type: Easing.InOutQuad }
                    }
                }
                Rectangle {
                    visible: footer.counted
                    width: parent.width * footer.fraction
                    height: parent.height
                    radius: 1.5
                    color: Theme.accent
                    Behavior on width { NumberAnimation { duration: 220; easing.type: Easing.OutCubic } }
                }
            }
            Text {
                text: history.analyzing
                      ? (footer.counted ? Format.fill(S.tr2(S.rev, "history.analyzing"), [Math.floor(footer.fraction * 100)]).replace("%%", "%") + "  ·  "
                                          + Format.fill(S.tr2(S.rev, "history.analyzingFiles"), [H.grouped(history.filesDone), H.grouped(history.filesTotal)])
                                        : Format.fill(S.tr2(S.rev, "history.analyzing"), [""]).replace(" %%", "").replace("%%", "").replace(" %", "").replace("%", "").trim())
                      : history.updatedMs > 0 ? Format.fill(S.tr2(S.rev, "history.updated"), [new Date(history.updatedMs).toLocaleTimeString(Qt.locale(), "HH:mm")]) : ""
                color: Theme.ink3
                font.pixelSize: 11
                font.features: Theme.tabular
            }
            // DESIGN 10: the range reaches back before traffic was recorded.
            Text {
                visible: history.trafficSince.length > 0 && win.span[0] < history.trafficSince
                text: "·  " + Format.fill(S.tr2(S.rev, "history.traffic.since"), [history.trafficSince.replace(/-/g, "/")])
                color: Theme.ink3
                font.pixelSize: 11
            }
            // DESIGN 9 Unknown prices: the notice and its popover.
            Text {
                visible: win.total.unpriced > 0
                text: "·  " + Format.fill(S.tr2(S.rev, "history.unpricedNotice"), [H.grouped(win.total.unpriced)])
                color: Theme.ink2
                font.pixelSize: 11
            }
            LinkButton {
                id: unpricedLink
                visible: win.total.unpriced > 0
                text: S.tr2(S.rev, "history.unpricedNotice.show")
                onClicked: unpricedPopup.opened ? unpricedPopup.close() : unpricedPopup.open()
                UnpricedPopup {
                    id: unpricedPopup
                    y: -height - 8
                    x: Math.min(0, win.width - 48 - width - unpricedLink.mapToItem(win.contentItem, 0, 0).x)
                    first: win.span[0]
                    last: win.span[1]
                    source: win.source
                    excluded: win.excluded
                    view: win.range
                    onClosed: unpricedLink.forceActiveFocus()
                }
            }
            Item { Layout.fillWidth: true }
            Text {
                visible: win.currency !== "USD"
                text: Format.fill(S.tr2(S.rev, "rates.historyNote"), [catalog.revision >= 0 ? catalog.ratesDate() : ""]) + "  ·"
                color: Theme.ink3
                font.pixelSize: 11
            }
            Text {
                id: privacy
                text: footer.width - x < 300 ? S.tr2(S.rev, "history.privacy.short") : S.tr2(S.rev, "history.privacy")
                color: Theme.ink3
                font.pixelSize: 11
                elide: Text.ElideRight
                Layout.maximumWidth: 520
                HoverHandler { id: privacyHover }
                ToolTip.visible: privacyHover.hovered && text !== S.tr2(S.rev, "history.privacy")
                ToolTip.text: S.tr2(S.rev, "history.privacy")
                Accessible.name: S.tr2(S.rev, "history.privacy")
            }
        }
    }
}
