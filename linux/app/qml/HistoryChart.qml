import QtQuick
import "History.js" as H
import "Format.js" as Format

// DESIGN 4b daily chart: output per day (stacked by source for all sources), the average speed line on a secondary axis,
// today hatched, a crosshair with a tooltip card on hover.
Item {
    id: chart
    property var series: []          // [{date, output, bySource, measuredOutput, measuredMs, requests, input, cost}]
    property var sourceOrder: []
    property string source: "all"
    property string today: ""
    property string currencyNote: ""
    property int hover: -1
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
    onWidthChanged: canvas.requestPaint()
    onHoverChanged: canvas.requestPaint()

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
            var max = 0, maxSpeed = 0
            for (var i = 0; i < s.length; ++i) {
                max = Math.max(max, s[i].output)
                var sp = H.speed(s[i].measuredOutput, s[i].measuredMs)
                if (sp > maxSpeed) maxSpeed = sp
            }
            if (max <= 0) max = 1
            ctx.fillStyle = Theme.ink3
            ctx.font = "10px sans-serif"
            ctx.textAlign = "right"
            ctx.fillText(H.compact(max), width - 2, plotTop + 4)
            var lastMonth = ""
            var step = Math.max(1, Math.ceil(s.length / 8))
            for (var j = 0; j < s.length; ++j) {
                var d = s[j]
                var r = chart.barRect(j)
                var base = plotBottom
                var isToday = d.date === chart.today
                ctx.globalAlpha = chart.hover >= 0 && chart.hover !== j ? 0.45 : (isToday ? 0.6 : 1)
                if (chart.source === "all") {
                    for (var k = 0; k < chart.sourceOrder.length; ++k) {
                        var src = chart.sourceOrder[k]
                        var v = d.bySource[src] || 0
                        if (v <= 0) continue
                        var h = v / max * plotH
                        ctx.fillStyle = Theme.sourceColor(src)
                        ctx.fillRect(r.x, base - h, r.w, h)
                        base -= h
                    }
                } else {
                    var hh = d.output / max * plotH
                    var grad = ctx.createLinearGradient(0, plotBottom - hh, 0, plotBottom)
                    grad.addColorStop(0, Theme.sourceColor(chart.source))
                    grad.addColorStop(1, Qt.rgba(Theme.sourceColor(chart.source).r, Theme.sourceColor(chart.source).g, Theme.sourceColor(chart.source).b, 0.55))
                    ctx.fillStyle = grad
                    ctx.fillRect(r.x, plotBottom - hh, r.w, hh)
                }
                ctx.globalAlpha = 1
                var month = d.date.substr(0, 7)
                if (month !== lastMonth && j > 0) {
                    ctx.strokeStyle = Theme.hairline
                    ctx.beginPath(); ctx.moveTo(r.x - 1.5, plotTop); ctx.lineTo(r.x - 1.5, plotBottom); ctx.stroke()
                }
                lastMonth = month
                if (j % step === 0) {
                    ctx.fillStyle = Theme.ink3
                    ctx.textAlign = "center"
                    ctx.fillText(+d.date.substr(5, 2) + "/" + +d.date.substr(8, 2), r.x + r.w / 2, height - 8)
                }
            }
            if (maxSpeed > 0) {
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
        width: 210
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
                text: tip.day ? tip.day.date + (tip.day.date === chart.today ? " · " + S.tr2(S.rev, "history.inProgress") : "") : ""
                color: Theme.ink
                font.pixelSize: 12
                font.weight: Font.DemiBold
            }
            Repeater {
                model: tip.day ? [
                    [S.tr2(S.rev, "history.table.output"), H.grouped(tip.day.output)],
                    [S.tr2(S.rev, "history.table.input"), H.grouped(tip.day.input)],
                    [S.tr2(S.rev, "history.table.requests"), H.grouped(tip.day.requests)],
                    [S.tr2(S.rev, "history.table.speed"), H.speed(tip.day.measuredOutput, tip.day.measuredMs) >= 0
                        ? Format.speed(H.speed(tip.day.measuredOutput, tip.day.measuredMs)) + " tok/s" : S.tr2(S.rev, "history.noSpeed")]
                ] : []
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
        }
    }
}
