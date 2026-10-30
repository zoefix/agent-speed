import QtQuick
import "History.js" as H
import "Format.js" as Format

// DESIGN 4b daily chart: output or cost per day (stacked by source for all sources), the average speed line on a
// secondary axis (output only), or the mirrored traffic bars of DESIGN 10 (download above the axis, upload below at 55 %,
// this computer's total as an outline), today hatched, a crosshair with a tooltip card on hover.
Item {
    id: chart
    property var series: []          // [{date, output, bySource, measuredOutput, measuredMs, requests, input, cost}]
    property var sourceOrder: []
    property string source: "all"
    property string today: ""
    property string currencyNote: ""
    property int hover: -1
    property string metric: "output"
    property var traffic: ({})        // date -> {rx, tx, rxTools, txTools, machineRx, machineTx, bySource}
    property string trafficSince: ""
    property bool includeTools: true
    property var formatMoney: function(v) { return String(v) }
    property var formatBytes: function(v) { return String(v) }
    signal picked(string date)
    implicitHeight: 260

    function barRect(i) {
        var n = Math.max(1, series.length)
        var plotW = width - 54
        var slot = plotW / n
        var w = Math.max(2, Math.min(28, slot * 0.68))
        return { x: 10 + slot * i + (slot - w) / 2, w: w, slot: slot }
    }

    onSeriesChanged: canvas.requestPaint()
    onMetricChanged: canvas.requestPaint()
    onTrafficChanged: canvas.requestPaint()
    onWidthChanged: canvas.requestPaint()
    onHoverChanged: canvas.requestPaint()
    readonly property int rev: S.rev
    onRevChanged: canvas.requestPaint()

    // The tooltip's date with its weekday (`history.weekdayDate`).
    function titleOf(date) {
        var locale = Qt.locale(S.language === "en" ? "en_US" : S.language.replace("-", "_"))
        var weekday = H.parse(date).toLocaleDateString(locale, "ddd")
        return Format.fill(S.tr2(S.rev, "history.weekdayDate"), [date.replace(/-/g, "/"), weekday])
    }
    // The day's top three models by the chart's metric, with their share and cost.
    function topModels(day) {
        var list = Object.keys(day.models).map(function(k) { return day.models[k] })
        var total = list.reduce(function(a, m) { return a + m.output }, 0)
        list.sort(function(a, b) { return b.output - a.output })
        return list.slice(0, 3).map(function(m) {
            var cost = m.unknown ? S.tr2(S.rev, "cost.unknown") : (m.cost || 0) === 0 ? S.tr2(S.rev, "cost.free") : chart.formatMoney(m.cost)
            return { name: m.name, share: total > 0 ? m.output / total : 0, cost: cost }
        })
    }

    Canvas {
        id: canvas
        anchors.fill: parent
        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()
            var s = chart.series
            var plotTop = 14, plotBottom = height - 26, plotH = plotBottom - plotTop
            ctx.strokeStyle = Theme.hairline
            ctx.lineWidth = 1
            for (var g = 0; g <= 3; ++g) {
                var gy = Math.round(plotTop + plotH * g / 3) + 0.5
                ctx.beginPath(); ctx.moveTo(10, gy); ctx.lineTo(width - 44, gy); ctx.stroke()
            }
            if (s.length === 0) return
            ctx.font = "10px sans-serif"
            var step = Math.max(1, Math.ceil(s.length / 8))
            var lastMonth = ""
            var dateLabels = function(j, r) {
                var month = s[j].date.substr(0, 7)
                if (month !== lastMonth && j > 0) {
                    ctx.strokeStyle = Theme.hairline
                    ctx.beginPath(); ctx.moveTo(r.x - 1.5, plotTop); ctx.lineTo(r.x - 1.5, plotBottom); ctx.stroke()
                }
                lastMonth = month
                if (j % step === 0) {
                    ctx.fillStyle = Theme.ink3
                    ctx.textAlign = "center"
                    ctx.fillText(+s[j].date.substr(5, 2) + "/" + +s[j].date.substr(8, 2), r.x + r.w / 2, height - 8)
                }
            }
            var faded = function(j) { return chart.hover >= 0 && chart.hover !== j ? 0.45 : (s[j].date === chart.today ? 0.6 : 1) }

            if (chart.metric === "traffic") {
                // Download above the axis, upload below; days before measuring began: a faint hatched band.
                var tmax = 0
                for (var ti = 0; ti < s.length; ++ti) {
                    var td = chart.traffic[s[ti].date]
                    if (!td) continue
                    tmax = Math.max(tmax, td.rx, td.tx, chart.source === "all" ? Math.max(td.machineRx, td.machineTx) : 0)
                }
                if (tmax <= 0) tmax = 1
                var axisY = plotTop + plotH / 2, half = plotH / 2
                ctx.strokeStyle = Theme.ink3
                ctx.beginPath(); ctx.moveTo(10, Math.round(axisY) + 0.5); ctx.lineTo(width - 44, Math.round(axisY) + 0.5); ctx.stroke()
                ctx.fillStyle = Theme.ink3
                ctx.textAlign = "right"
                ctx.fillText("↓ " + chart.formatBytes(tmax), width - 2, plotTop + 4)
                ctx.fillText("↑ " + chart.formatBytes(tmax), width - 2, plotBottom)
                var notRecordedLabel = false
                for (var tj = 0; tj < s.length; ++tj) {
                    var tr = chart.barRect(tj)
                    var date = s[tj].date
                    if (chart.trafficSince.length === 0 || date < chart.trafficSince) {
                        ctx.fillStyle = Qt.rgba(Theme.ink3.r, Theme.ink3.g, Theme.ink3.b, 0.06)
                        ctx.fillRect(tr.x - (tr.slot - tr.w) / 2, plotTop, tr.slot, plotH)
                        if (!notRecordedLabel && tr.slot * (s.length - tj) > 120) {
                            notRecordedLabel = true
                            ctx.fillStyle = Theme.ink3
                            ctx.textAlign = "left"
                            ctx.fillText(S.tr2(S.rev, "history.traffic.notRecorded").split(/[。.]/)[0], tr.x, plotTop + 12)
                        }
                        dateLabels(tj, tr)
                        continue
                    }
                    var t = chart.traffic[date]
                    ctx.globalAlpha = faded(tj)
                    if (t && chart.source === "all" && (t.machineRx > 0 || t.machineTx > 0)) {
                        ctx.strokeStyle = Theme.connector
                        ctx.lineWidth = 1
                        ctx.strokeRect(tr.x - 0.5, axisY - t.machineRx / tmax * half, tr.w + 1, t.machineRx / tmax * half)
                        ctx.strokeRect(tr.x - 0.5, axisY, tr.w + 1, t.machineTx / tmax * half)
                    }
                    if (t) {
                        var up = axisY, down = axisY
                        var keys = chart.source === "all" ? chart.sourceOrder.concat(Object.keys(t.bySource).filter(function(k) { return chart.sourceOrder.indexOf(k) < 0 }))
                                                          : [chart.source]
                        for (var tk = 0; tk < keys.length; ++tk) {
                            var part = t.bySource[keys[tk]]
                            if (!part) continue
                            var c = Theme.sourceColor(keys[tk])
                            var hu = part.rx / tmax * half, hd = part.tx / tmax * half
                            ctx.fillStyle = c
                            ctx.fillRect(tr.x, up - hu, tr.w, hu)
                            up -= hu
                            ctx.fillStyle = Qt.rgba(c.r, c.g, c.b, 0.55)
                            ctx.fillRect(tr.x, down, tr.w, hd)
                            down += hd
                        }
                    }
                    ctx.globalAlpha = 1
                    dateLabels(tj, tr)
                }
            } else {
                var cost = chart.metric === "cost"
                var max = 0, maxSpeed = 0
                for (var i = 0; i < s.length; ++i) {
                    max = Math.max(max, cost ? (s[i].cost || 0) : s[i].output)
                    var sp = H.speed(s[i].measuredOutput, s[i].measuredMs)
                    if (sp > maxSpeed) maxSpeed = sp
                }
                if (max <= 0) max = 1
                ctx.fillStyle = Theme.ink3
                ctx.textAlign = "right"
                ctx.fillText(cost ? chart.formatMoney(max) : H.compact(max), width - 2, plotTop + 4)
                for (var j = 0; j < s.length; ++j) {
                    var d = s[j]
                    var r = chart.barRect(j)
                    var base = plotBottom
                    ctx.globalAlpha = faded(j)
                    var bySource = cost ? (d.costBySource || {}) : d.bySource
                    if (chart.source === "all") {
                        for (var k = 0; k < chart.sourceOrder.length; ++k) {
                            var src = chart.sourceOrder[k]
                            var v = bySource[src] || 0
                            if (v <= 0) continue
                            var h = v / max * plotH
                            ctx.fillStyle = Theme.sourceColor(src)
                            ctx.fillRect(r.x, base - h, r.w, h)
                            base -= h
                        }
                    } else {
                        var hh = (cost ? (d.cost || 0) : d.output) / max * plotH
                        var col = Theme.sourceColor(chart.source)
                        var grad = ctx.createLinearGradient(0, plotBottom - hh, 0, plotBottom)
                        grad.addColorStop(0, col)
                        grad.addColorStop(1, Qt.rgba(col.r, col.g, col.b, 0.55))
                        ctx.fillStyle = grad
                        ctx.fillRect(r.x, plotBottom - hh, r.w, hh)
                    }
                    ctx.globalAlpha = 1
                    dateLabels(j, r)
                }
                if (!cost && maxSpeed > 0) {
                    ctx.strokeStyle = Theme.ink2
                    ctx.fillStyle = Theme.ink2
                    ctx.lineWidth = 1.5
                    var drawing = false
                    ctx.beginPath()
                    for (var p = 0; p < s.length; ++p) {
                        var spd = H.speed(s[p].measuredOutput, s[p].measuredMs)
                        var rr = chart.barRect(p)
                        if (spd < 0) { drawing = false; continue }
                        var y = plotBottom - spd / maxSpeed * plotH * 0.92
                        if (!drawing) ctx.moveTo(rr.x + rr.w / 2, y); else ctx.lineTo(rr.x + rr.w / 2, y)
                        drawing = true
                    }
                    ctx.stroke()
                    for (var q = 0; q < s.length; ++q) {
                        var sq = H.speed(s[q].measuredOutput, s[q].measuredMs)
                        if (sq < 0) continue
                        var rq = chart.barRect(q)
                        ctx.beginPath(); ctx.arc(rq.x + rq.w / 2, plotBottom - sq / maxSpeed * plotH * 0.92, 2, 0, Math.PI * 2); ctx.fill()
                    }
                    ctx.textAlign = "right"
                    ctx.fillStyle = Theme.ink3
                    ctx.fillText(Format.speed(maxSpeed) + " tok/s", width - 2, plotTop + 18)
                    ctx.fillText(S.tr2(S.rev, "history.chart.speed"), width - 2, plotTop + 31)
                }
            }
            if (chart.hover >= 0 && chart.hover < s.length) {
                var hr = chart.barRect(chart.hover)
                ctx.strokeStyle = Theme.ink3
                ctx.setLineDash([3, 3])
                ctx.beginPath(); ctx.moveTo(hr.x + hr.w / 2 + 0.5, plotTop); ctx.lineTo(hr.x + hr.w / 2 + 0.5, plotBottom); ctx.stroke()
            }
        }
    }

    MouseArea {
        anchors.fill: parent
        hoverEnabled: true
        onPositionChanged: (mouse) => {
            var n = chart.series.length
            if (n === 0) { chart.hover = -1; return }
            var slot = (chart.width - 54) / n
            var i = Math.floor((mouse.x - 10) / slot)
            chart.hover = i >= 0 && i < n ? i : -1
        }
        onExited: chart.hover = -1
        onClicked: if (chart.hover >= 0) chart.picked(chart.series[chart.hover].date)
    }

    Rectangle {
        id: tip
        readonly property var day: chart.hover >= 0 && chart.hover < chart.series.length ? chart.series[chart.hover] : null
        visible: day !== null
        width: 264
        height: tipColumn.implicitHeight + 20
        radius: 10
        color: Theme.dark ? "#22252C" : "#FFFFFF"
        border.width: 1
        border.color: Theme.border
        x: {
            if (!day) return 0
            var r = chart.barRect(chart.hover)
            var cx = r.x + r.w / 2 + 12
            return cx + width > chart.width ? r.x - width - 8 : cx
        }
        y: 8
        Column {
            id: tipColumn
            x: 10
            y: 10
            width: parent.width - 20
            spacing: 3
            Text {
                text: tip.day ? chart.titleOf(tip.day.date) + (tip.day.date === chart.today ? " · " + S.tr2(S.rev, "history.inProgress") : "") : ""
                color: Theme.ink
                font.pixelSize: 12
                font.weight: Font.DemiBold
            }
            Repeater {
                model: !tip.day ? [] : chart.metric === "traffic" ? (function() {
                    var t = chart.traffic[tip.day.date]
                    if (chart.trafficSince.length === 0 || tip.day.date < chart.trafficSince || !t)
                        return [[S.tr2(S.rev, "history.table.traffic"), "–"]]
                    var rows = [[S.tr2(S.rev, "history.table.traffic"), Format.fill(S.tr2(S.rev, "traffic.pair"), [chart.formatBytes(t.rx), chart.formatBytes(t.tx)])]]
                    if (chart.includeTools) rows.push(["", Format.fill(S.tr2(S.rev, "history.traffic.tools"), [chart.formatBytes(t.rxTools + t.txTools)])])
                    if (chart.source === "all") rows.push(["", Format.fill(S.tr2(S.rev, "history.traffic.machine"), [chart.formatBytes(t.machineRx + t.machineTx)])])
                    return rows
                })() : [
                    [S.tr2(S.rev, "history.table.cost"), tip.day.hasCost ? (tip.day.unpriced > 0 ? "≥" : "") + chart.formatMoney(tip.day.cost) : "–"],
                    [S.tr2(S.rev, "history.table.output"), H.grouped(tip.day.output)],
                    [S.tr2(S.rev, "history.table.input"), H.grouped(tip.day.input)],
                    [S.tr2(S.rev, "history.table.requests"), H.grouped(tip.day.requests)],
                    [S.tr2(S.rev, "history.table.speed"), H.speed(tip.day.measuredOutput, tip.day.measuredMs) >= 0
                        ? Format.speed(H.speed(tip.day.measuredOutput, tip.day.measuredMs)) + " tok/s" : S.tr2(S.rev, "history.noSpeed")]
                ].concat(chart.source === "all" && tip.day.peak ? [[S.tr2(S.rev, "history.table.peak"), Format.speed(tip.day.peak) + " tok/s"]] : [])
                Row {
                    required property var modelData
                    width: tipColumn.width
                    Text { width: parent.width * 0.55; text: modelData[0]; color: Theme.ink2; font.pixelSize: 11 }
                    Text {
                        width: parent.width * 0.45
                        horizontalAlignment: Text.AlignRight
                        text: modelData[1]
                        color: Theme.ink
                        font.pixelSize: 11
                        font.features: Theme.tabular
                    }
                }
            }
            // Notes: unpriced and unfinalized requests, a partially recorded traffic day.
            Repeater {
                model: !tip.day ? [] : (function() {
                    var notes = []
                    if (chart.metric === "traffic") {
                        var t = chart.traffic[tip.day.date]
                        if (t && chart.trafficSince.length > 0 && tip.day.date >= chart.trafficSince && H.trafficPartial(t, tip.day.date, chart.today))
                            notes.push(Format.fill(S.tr2(S.rev, "history.traffic.partial"), [H.duration(H.recordedMs(t), function(k) { return S.tr2(S.rev, k) })]))
                        return notes
                    }
                    if (tip.day.unpriced > 0) notes.push(Format.fill(S.tr2(S.rev, "history.unpricedDay"), [tip.day.unpriced]))
                    if (tip.day.unfinalized > 0) notes.push(Format.fill(S.tr2(S.rev, "history.unfinalized"), [tip.day.unfinalized]))
                    return notes
                })()
                Text {
                    required property string modelData
                    width: tipColumn.width
                    text: modelData
                    color: Theme.ink3
                    font.pixelSize: 11
                    wrapMode: Text.WordWrap
                }
            }
            // The top three models with share bars.
            Item { width: 1; height: 3; visible: topList.count > 0 }
            Repeater {
                id: topList
                model: tip.day && chart.metric !== "traffic" ? chart.topModels(tip.day) : []
                Row {
                    required property var modelData
                    width: tipColumn.width
                    spacing: 6
                    Text {
                        width: parent.width - 86 - costText.width - 6
                        text: modelData.name
                        color: Theme.ink2
                        font.pixelSize: 11
                        elide: Text.ElideMiddle
                        anchors.verticalCenter: parent.verticalCenter
                    }
                    Rectangle {
                        width: 46
                        height: 4
                        radius: 2
                        color: Theme.track
                        anchors.verticalCenter: parent.verticalCenter
                        Rectangle {
                            width: parent.width * modelData.share
                            height: parent.height
                            radius: 2
                            color: chart.source === "all" ? Theme.ink2 : Theme.sourceColor(chart.source)
                        }
                    }
                    Text {
                        id: costText
                        text: modelData.cost
                        color: Theme.ink3
                        font.pixelSize: 11
                        font.features: Theme.tabular
                        anchors.verticalCenter: parent.verticalCenter
                    }
                    Text {
                        width: 28
                        horizontalAlignment: Text.AlignRight
                        text: Math.round(modelData.share * 100) + "%"
                        color: Theme.ink2
                        font.pixelSize: 11
                        font.features: Theme.tabular
                        anchors.verticalCenter: parent.verticalCenter
                    }
                }
            }
        }
    }
}
