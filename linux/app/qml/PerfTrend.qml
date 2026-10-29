import QtQuick
import "Format.js" as Format
import "History.js" as H

// DESIGN 12 Usage History, trend: the selected row's pooled score of each day on a log scale (250 at the bottom),
// gridlines at every power-of-two step from 500, the 1000 line, a 2 pt line in the source color over its area, a gap
// for a day below 20 timed requests, and the usual line dashed. Hover or the arrow keys show the day's card.
Item {
    id: chart
    property var points: []        // [{day, score|null, text, tier, n, requests, hint}]
    property color tint: Theme.accent
    property var usual: undefined  // the baseline, or undefined
    property string highlight: ""  // a single-day range: that day's slot is tinted
    property string emptyText: ""
    property int hover: -1
    activeFocusOnTab: true
    Accessible.role: Accessible.Graphic

    readonly property real leftPad: 40
    readonly property real plotTop: 10
    readonly property real plotBottom: height - 22
    readonly property real ceiling: {
        var max = 0
        for (var i = 0; i < points.length; ++i) if (points[i].score !== null && points[i].score !== undefined) max = Math.max(max, points[i].score)
        if (usual !== undefined) max = Math.max(max, usual)
        return Math.max(4000, 250 * Math.pow(2, Math.ceil(Math.log(Math.max(max, 250) / 250) / Math.LN2)))
    }
    readonly property bool empty: {
        for (var i = 0; i < points.length; ++i) if (points[i].score !== null && points[i].score !== undefined) return false
        return true
    }
    function yOf(v) {
        var f = Math.log(Math.max(v, 250) / 250) / Math.log(ceiling / 250)
        return plotBottom - Math.min(1, Math.max(0, f)) * (plotBottom - plotTop)
    }
    function slot(i) {
        var w = (width - leftPad - 8) / Math.max(1, points.length)
        return { x: leftPad + i * w, w: w }
    }

    onPointsChanged: canvas.requestPaint()
    onUsualChanged: canvas.requestPaint()
    onHoverChanged: canvas.requestPaint()
    onWidthChanged: canvas.requestPaint()
    onHeightChanged: canvas.requestPaint()
    Connections {
        target: Theme
        function onDarkChanged() { canvas.requestPaint() }
    }

    Keys.onLeftPressed: hover = Math.max(0, (hover < 0 ? points.length : hover) - 1)
    Keys.onRightPressed: hover = Math.min(points.length - 1, hover + 1)
    onActiveFocusChanged: if (!activeFocus) hover = -1

    Canvas {
        id: canvas
        anchors.fill: parent
        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()
            var p = chart.points
            ctx.font = "9.5px sans-serif"
            ctx.textAlign = "right"
            ctx.textBaseline = "middle"
            // Gridlines at 500, 1000, 2000, … with their values; the 1000 line in the label color at 35 %.
            for (var v = 500; v <= chart.ceiling; v *= 2) {
                var gy = Math.round(chart.yOf(v)) + 0.5
                ctx.strokeStyle = v === 1000 ? Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.35) : Theme.hairline
                ctx.lineWidth = 1
                ctx.beginPath(); ctx.moveTo(chart.leftPad, gy); ctx.lineTo(chart.width - 8, gy); ctx.stroke()
                ctx.fillStyle = Theme.ink3
                ctx.fillText(Format.perfFull(v), chart.leftPad - 6, gy)
            }
            if (p.length === 0) return
            // Month rules and day labels, as the daily chart.
            ctx.textAlign = "center"
            ctx.textBaseline = "alphabetic"
            var step = Math.max(1, Math.ceil(p.length / 8))
            var lastMonth = ""
            for (var j = 0; j < p.length; ++j) {
                var r = chart.slot(j)
                if (p[j].day === chart.highlight) {
                    ctx.fillStyle = Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.05)
                    ctx.fillRect(r.x, chart.plotTop, r.w, chart.plotBottom - chart.plotTop)
                }
                var month = p[j].day.substr(0, 7)
                if (month !== lastMonth && j > 0) {
                    ctx.strokeStyle = Theme.hairline
                    ctx.beginPath(); ctx.moveTo(Math.round(r.x) + 0.5, chart.plotTop); ctx.lineTo(Math.round(r.x) + 0.5, chart.plotBottom); ctx.stroke()
                }
                lastMonth = month
                if (j % step === 0) {
                    ctx.fillStyle = Theme.ink3
                    ctx.fillText(+p[j].day.substr(5, 2) + "/" + +p[j].day.substr(8, 2), r.x + r.w / 2, chart.height - 6)
                }
            }
            if (chart.empty) return
            // The usual line: dashed 4 · 3 in the source color at 60 %.
            if (chart.usual !== undefined) {
                var uy = Math.round(chart.yOf(chart.usual)) + 0.5
                ctx.strokeStyle = Qt.rgba(chart.tint.r, chart.tint.g, chart.tint.b, 0.6)
                ctx.setLineDash([4, 3])
                ctx.beginPath(); ctx.moveTo(chart.leftPad, uy); ctx.lineTo(chart.width - 8, uy); ctx.stroke()
                ctx.setLineDash([])
            }
            // Runs of days with a score: the area, then the line.
            var runs = [], run = []
            for (var k = 0; k < p.length; ++k) {
                if (p[k].score === null || p[k].score === undefined) {
                    if (run.length) runs.push(run)
                    run = []
                } else {
                    var s = chart.slot(k)
                    run.push({ x: s.x + s.w / 2, y: chart.yOf(p[k].score), i: k })
                }
            }
            if (run.length) runs.push(run)
            for (var a = 0; a < runs.length; ++a) {
                var pts = runs[a]
                if (pts.length < 2) continue
                var grad = ctx.createLinearGradient(0, chart.plotTop, 0, chart.plotBottom)
                grad.addColorStop(0, Qt.rgba(chart.tint.r, chart.tint.g, chart.tint.b, 0.12))
                grad.addColorStop(1, Qt.rgba(chart.tint.r, chart.tint.g, chart.tint.b, 0))
                ctx.fillStyle = grad
                ctx.beginPath()
                ctx.moveTo(pts[0].x, chart.plotBottom)
                for (var q = 0; q < pts.length; ++q) ctx.lineTo(pts[q].x, pts[q].y)
                ctx.lineTo(pts[pts.length - 1].x, chart.plotBottom)
                ctx.closePath()
                ctx.fill()
                ctx.strokeStyle = chart.tint
                ctx.lineWidth = 2
                ctx.lineJoin = "round"
                ctx.lineCap = "round"
                ctx.beginPath()
                ctx.moveTo(pts[0].x, pts[0].y)
                for (var u = 1; u < pts.length; ++u) ctx.lineTo(pts[u].x, pts[u].y)
                ctx.stroke()
            }
            // Dots: 4.5 pt with a 1.5 pt ring in the background, the latest day (and a single-day range's day) 6 pt.
            var latest = -1
            for (var d = p.length - 1; d >= 0; --d) if (p[d].score !== null && p[d].score !== undefined) { latest = d; break }
            for (var b = 0; b < runs.length; ++b) {
                for (var c = 0; c < runs[b].length; ++c) {
                    var dot = runs[b][c]
                    var big = dot.i === latest || p[dot.i].day === chart.highlight
                    var rad = (big ? 6 : 4.5) / 2
                    ctx.fillStyle = Theme.card.a > 0.5 ? Theme.card : Theme.panel
                    ctx.beginPath(); ctx.arc(dot.x, dot.y, rad + 1.5, 0, 2 * Math.PI); ctx.fill()
                    ctx.fillStyle = chart.tint
                    ctx.beginPath(); ctx.arc(dot.x, dot.y, rad, 0, 2 * Math.PI); ctx.fill()
                }
            }
            // The crosshair.
            if (chart.hover >= 0 && chart.hover < p.length) {
                var hs = chart.slot(chart.hover)
                var hx = Math.round(hs.x + hs.w / 2) + 0.5
                ctx.strokeStyle = Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.3)
                ctx.lineWidth = 1
                ctx.beginPath(); ctx.moveTo(hx, chart.plotTop); ctx.lineTo(hx, chart.plotBottom); ctx.stroke()
            }
        }
    }

    Text {
        visible: chart.empty && chart.points.length > 0
        anchors.centerIn: parent
        text: chart.emptyText
        color: Theme.ink3
        font.pixelSize: 12
    }

    MouseArea {
        anchors.fill: parent
        hoverEnabled: true
        onPositionChanged: (mouse) => {
            var w = (chart.width - chart.leftPad - 8) / Math.max(1, chart.points.length)
            var i = Math.floor((mouse.x - chart.leftPad) / w)
            chart.hover = i >= 0 && i < chart.points.length ? i : -1
        }
        onExited: chart.hover = -1
    }

    // The day's card: date with weekday, the score, the timed requests and the tier word; a gap day says why.
    Rectangle {
        id: tip
        readonly property var point: chart.hover >= 0 && chart.hover < chart.points.length ? chart.points[chart.hover] : null
        visible: point !== null
        readonly property real anchorX: point ? chart.slot(chart.hover).x + chart.slot(chart.hover).w / 2 : 0
        x: Math.max(0, Math.min(chart.width - width, anchorX + 10 + width > chart.width ? anchorX - 10 - width : anchorX + 10))
        y: chart.plotTop
        width: tipColumn.implicitWidth + 20
        height: tipColumn.implicitHeight + 14
        radius: 8
        color: Theme.cardFill
        border.width: 1
        border.color: Theme.cardLine
        Column {
            id: tipColumn
            x: 10
            y: 7
            spacing: 2
            Text {
                text: tip.point ? H.parse(tip.point.day).toLocaleDateString(Qt.locale(S.language === "en" ? "en_US" : S.language.replace("-", "_")), "M/d ddd") : ""
                color: Theme.ink2
                font.pixelSize: 11
            }
            Text {
                visible: tip.point !== null && tip.point.score !== null && tip.point.score !== undefined
                text: tip.point ? tip.point.text + "  " + tip.point.tier : ""
                color: Theme.ink
                font.pixelSize: 13
                font.weight: Font.DemiBold
                font.features: Theme.tabular
            }
            Text {
                text: tip.point ? (tip.point.score !== null && tip.point.score !== undefined ? tip.point.requests : tip.point.hint) : ""
                color: Theme.ink3
                font.pixelSize: 11
            }
        }
    }
}
