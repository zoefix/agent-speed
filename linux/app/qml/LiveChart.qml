import QtQuick
import "Heat.js" as Heat
import "Format.js" as Format

// DESIGN 4 live chart: the last 2 minutes as a Catmull-Rom area filled through the heat palette by value, a dashed line
// at today's peak, and a crosshair on hover.
Item {
    id: chart
    property var values: []
    property real peak: 0
    property string peakCaption
    property string unit
    property real redline: 2000
    implicitHeight: 96

    readonly property int gutter: 30
    readonly property real plotTop: 8
    readonly property real plotWidth: Math.max(1, width - gutter)
    readonly property real floorY: height - 0.5
    property real settled: 10
    property real domain: settled
    property int hoverIndex: -1
    Behavior on domain { NumberAnimation { duration: 700; easing.type: Easing.InOutCubic } }

    onValuesChanged: { settle(); canvas.requestPaint() }
    onDomainChanged: canvas.requestPaint()
    onPeakChanged: settle()
    onHoverIndexChanged: canvas.requestPaint()
    onWidthChanged: canvas.requestPaint()

    function nice(v) {
        if (!(v > 0) || !isFinite(v)) return 10
        var magnitude = Math.pow(10, Math.floor(Math.log(v) / Math.LN10))
        var fraction = v / magnitude
        var steps = [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]
        for (var i = 0; i < steps.length; ++i) {
            if (fraction <= steps[i] + 1e-9) return steps[i] * magnitude
        }
        return 10 * magnitude
    }

    // Grows at once, shrinks only once the data clearly fits a smaller scale; a nearby peak still pulls it up.
    function settle() {
        var top = 0
        for (var i = 0; i < values.length; ++i) top = Math.max(top, values[i])
        var target = nice(Math.max(top * 1.12, 10))
        if (peak > target && peak <= target * 1.6) target = nice(peak * 1.1)
        if (target > settled || target <= settled * 0.62) settled = target
    }

    function xAt(i) { return values.length > 1 ? i * plotWidth / (values.length - 1) : 0 }
    function yAt(v) { return floorY - Math.max(0, Math.min(v, domain * 1.04)) / Math.max(domain, 1e-6) * (floorY - plotTop) }
    readonly property bool peakShown: peak > 0 && peak <= domain * 1.001

    Repeater {
        model: [chart.domain, chart.domain / 2]
        Text {
            required property var modelData
            anchors.right: parent.right
            y: chart.yAt(modelData) - height / 2
            text: Format.axis(Math.round(modelData))
            color: Theme.ink3
            font.pixelSize: 10
            font.features: Theme.tabular
        }
    }

    Canvas {
        id: canvas
        width: chart.plotWidth
        height: chart.height
        antialiasing: true

        function curve(ctx, pts) {
            var last = pts.length - 1
            for (var i = 0; i < last; ++i) {
                var p0 = pts[Math.max(i - 1, 0)], p1 = pts[i], p2 = pts[i + 1], p3 = pts[Math.min(i + 2, last)]
                var c1x = Math.min(Math.max(p1.x + (p2.x - p0.x) / 6, p1.x), p2.x)
                var c2x = Math.min(Math.max(p2.x - (p3.x - p1.x) / 6, p1.x), p2.x)
                var c1y = Math.min(p1.y + (p2.y - p0.y) / 6, chart.floorY)
                var c2y = Math.min(p2.y - (p3.y - p1.y) / 6, chart.floorY)
                ctx.bezierCurveTo(c1x, c1y, c2x, c2y, p2.x, p2.y)
            }
        }

        function heatGradient(ctx, alphaLow, alphaHigh) {
            var g = ctx.createLinearGradient(0, chart.floorY, 0, chart.plotTop)
            for (var s = 0; s <= 10; ++s) {
                var f = s / 10
                var c = Heat.color(Heat.heatOf(f * chart.domain, chart.redline))
                g.addColorStop(f, Qt.rgba(c.r, c.g, c.b, alphaLow + (alphaHigh - alphaLow) * f))
            }
            return g
        }

        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()
            ctx.lineWidth = 1
            ctx.strokeStyle = Theme.hairline
            ctx.setLineDash([1, 3])
            var gridValues = [chart.domain, chart.domain / 2]
            for (var g = 0; g < gridValues.length; ++g) {
                var gy = Math.round(chart.yAt(gridValues[g])) + 0.5
                ctx.beginPath(); ctx.moveTo(0, gy); ctx.lineTo(width, gy); ctx.stroke()
            }
            ctx.setLineDash([])
            ctx.beginPath(); ctx.moveTo(0, chart.floorY); ctx.lineTo(width, chart.floorY); ctx.stroke()

            var n = chart.values.length
            if (n < 2) return
            var pts = []
            for (var i = 0; i < n; ++i) pts.push({ x: chart.xAt(i), y: chart.yAt(chart.values[i]) })

            ctx.beginPath()
            ctx.moveTo(pts[0].x, chart.floorY)
            ctx.lineTo(pts[0].x, pts[0].y)
            curve(ctx, pts)
            ctx.lineTo(pts[n - 1].x, chart.floorY)
            ctx.closePath()
            ctx.fillStyle = heatGradient(ctx, Theme.dark ? 0.04 : 0.05, Theme.dark ? 0.42 : 0.36)
            ctx.fill()

            ctx.beginPath()
            ctx.moveTo(pts[0].x, pts[0].y)
            curve(ctx, pts)
            ctx.lineWidth = 1.5
            ctx.strokeStyle = heatGradient(ctx, 1, 1)
            ctx.stroke()

            if (chart.peakShown) {
                var py = Math.round(chart.yAt(chart.peak)) + 0.5
                var pc = Heat.color(Heat.heatOf(chart.peak, chart.redline))
                ctx.strokeStyle = Qt.rgba(pc.r, pc.g, pc.b, 0.75)
                ctx.lineWidth = 1
                ctx.setLineDash([7, 4])
                ctx.beginPath(); ctx.moveTo(0, py); ctx.lineTo(width, py); ctx.stroke()
                ctx.setLineDash([])
            }

            if (chart.hoverIndex >= 0 && chart.hoverIndex < n) {
                var hx = Math.round(pts[chart.hoverIndex].x) + 0.5
                ctx.strokeStyle = Theme.ink3
                ctx.lineWidth = 1
                ctx.beginPath(); ctx.moveTo(hx, chart.plotTop - 4); ctx.lineTo(hx, chart.floorY); ctx.stroke()
            }

            var now = chart.hoverIndex >= 0 ? pts[chart.hoverIndex] : pts[n - 1]
            var nowValue = chart.values[chart.hoverIndex >= 0 ? chart.hoverIndex : n - 1]
            var nc = Heat.color(Heat.heatOf(nowValue, chart.redline))
            ctx.fillStyle = Qt.rgba(nc.r, nc.g, nc.b, 0.22)
            ctx.beginPath(); ctx.arc(now.x, now.y, 5, 0, Math.PI * 2); ctx.fill()
            ctx.fillStyle = nc
            ctx.beginPath(); ctx.arc(now.x, now.y, 2.75, 0, Math.PI * 2); ctx.fill()
        }
    }

    Rectangle {
        id: peakPill
        readonly property real lineY: chart.yAt(chart.peak)
        readonly property color tone: Heat.color(Heat.heatOf(chart.peak, chart.redline))
        visible: chart.peakShown && chart.hoverIndex < 0
        x: 4
        y: lineY - height - 2 >= 0 ? lineY - height - 2 : lineY + 2
        width: peakText.implicitWidth + 12
        height: peakText.implicitHeight + 4
        radius: height / 2
        color: Qt.rgba(Theme.panel.r, Theme.panel.g, Theme.panel.b, 0.92)
        border.width: 0.75
        border.color: Qt.rgba(tone.r, tone.g, tone.b, 0.4)
        Text {
            id: peakText
            anchors.centerIn: parent
            text: chart.peakCaption + " " + Format.speed(chart.peak)
            color: Theme.heatText(peakPill.tone)
            font.pixelSize: 10
            font.weight: Font.Medium
            font.features: Theme.tabular
        }
    }

    Rectangle {
        id: bubble
        visible: chart.hoverIndex >= 0 && chart.hoverIndex < chart.values.length
        readonly property real anchorX: visible ? chart.xAt(chart.hoverIndex) : 0
        x: Math.max(0, Math.min(chart.plotWidth - width, anchorX - width / 2))
        y: -2
        width: bubbleText.implicitWidth + 14
        height: bubbleText.implicitHeight + 6
        radius: height / 2
        color: Theme.dark ? "#2A2E37" : "#FFFFFF"
        border.width: 1
        border.color: Theme.border
        Text {
            id: bubbleText
            anchors.centerIn: parent
            textFormat: Text.StyledText
            text: bubble.visible
                  ? "<b>" + Format.speed(chart.values[chart.hoverIndex]) + "</b> " + chart.unit
                    + "  <font color='" + Theme.ink3 + "'>−" + (chart.values.length - 1 - chart.hoverIndex) + "s</font>"
                  : ""
            color: Theme.ink
            font.pixelSize: 11
            font.features: Theme.tabular
        }
    }

    MouseArea {
        width: chart.plotWidth
        height: chart.height
        hoverEnabled: true
        acceptedButtons: Qt.NoButton
        onPositionChanged: function(mouse) {
            var n = chart.values.length
            chart.hoverIndex = n > 1 ? Math.max(0, Math.min(n - 1, Math.round(mouse.x / chart.plotWidth * (n - 1)))) : -1
        }
        onExited: chart.hoverIndex = -1
    }
}
