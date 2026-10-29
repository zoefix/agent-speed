import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 10 dashboard section: this computer's live rates and last minute, the sources' traffic today and now.
ColumnLayout {
    id: section
    property var traffic: ({})
    property bool expanded: false
    readonly property var rows: traffic.sources || []
    readonly property var shownRows: expanded ? rows : rows.slice(0, 3)
    spacing: 0

    SectionHeader {
        text: S.tr2(S.rev, "traffic.title")
        trailing: "↓ " + (section.traffic.machineRxText || "–") + "   ↑ " + (section.traffic.machineTxText || "–")
    }

    // The last 60 s of this computer, mirrored: download above the midline, upload below.
    Canvas {
        id: spark
        Layout.fillWidth: true
        Layout.preferredHeight: 28
        Layout.bottomMargin: 8
        readonly property var series: section.traffic.series || []
        property real scale: 10000
        Behavior on scale { NumberAnimation { duration: 300; easing.type: Easing.OutCubic } }
        onSeriesChanged: {
            var top = 0
            for (var i = 0; i < series.length; ++i) top = Math.max(top, series[i][0], series[i][1])
            scale = niceStep(Math.max(top, 10000))
            requestPaint()
        }
        onScaleChanged: requestPaint()
        onWidthChanged: requestPaint()
        function niceStep(v) {
            var p = Math.pow(10, Math.floor(Math.log(v) / Math.LN10))
            var steps = [1, 2, 5, 10]
            for (var i = 0; i < steps.length; ++i) if (steps[i] * p >= v) return steps[i] * p
            return 10 * p
        }
        onPaint: {
            var ctx = getContext("2d")
            ctx.reset()
            var n = series.length
            if (n < 2) return
            var mid = height / 2, w = width, step = w / (n - 1)
            var ink = Theme.ink
            function area(index, sign, fill, line) {
                ctx.beginPath()
                ctx.moveTo(0, mid)
                for (var i = 0; i < n; ++i) ctx.lineTo(i * step, mid - sign * Math.min(1, series[i][index] / spark.scale) * (mid - 1))
                ctx.lineTo(w, mid)
                ctx.closePath()
                ctx.fillStyle = Qt.rgba(ink.r, ink.g, ink.b, fill)
                ctx.fill()
                ctx.beginPath()
                for (var j = 0; j < n; ++j) {
                    var y = mid - sign * Math.min(1, series[j][index] / spark.scale) * (mid - 1)
                    if (j === 0) ctx.moveTo(0, y)
                    else ctx.lineTo(j * step, y)
                }
                ctx.strokeStyle = Qt.rgba(ink.r, ink.g, ink.b, line)
                ctx.lineWidth = 1
                ctx.stroke()
            }
            area(0, 1, 0.16, 0.55)
            area(1, -1, 0.10, 0.40)
            ctx.fillStyle = Theme.hairline
            ctx.fillRect(0, mid, w, 1)
        }
        HoverHandler { id: sparkHover }
        ToolTip.visible: sparkHover.hovered
        ToolTip.delay: 450
        ToolTip.text: S.tr2(S.rev, "traffic.machine.hint")
    }

    Repeater {
        model: section.traffic.available === false ? [] : section.shownRows
        Item {
            required property var modelData
            Layout.fillWidth: true
            implicitHeight: 36
            Rectangle {
                id: dot
                anchors.verticalCenter: parent.verticalCenter
                width: 8
                height: 8
                radius: 4
                color: Theme.sourceColor(parent.modelData.source)
            }
            Column {
                anchors.left: dot.right
                anchors.leftMargin: 10
                anchors.right: rates.left
                anchors.rightMargin: 10
                anchors.verticalCenter: parent.verticalCenter
                spacing: 1
                Text {
                    width: parent.width
                    text: parent.parent.modelData.name
                    color: Theme.ink
                    font.pixelSize: 13
                    elide: Text.ElideRight
                }
                Text {
                    width: parent.width
                    text: parent.parent.modelData.todayText
                    color: Theme.ink2
                    font.pixelSize: 11
                    font.features: Theme.tabular
                    elide: Text.ElideRight
                }
            }
            Column {
                id: rates
                anchors.right: parent.right
                anchors.verticalCenter: parent.verticalCenter
                Text {
                    anchors.right: parent.right
                    text: "↓ " + parent.parent.modelData.liveRxText
                    color: parent.parent.modelData.liveRxText === "–" ? Theme.ink3 : Theme.ink2
                    font.pixelSize: 11
                    font.features: Theme.tabular
                }
                Text {
                    anchors.right: parent.right
                    text: "↑ " + parent.parent.modelData.liveTxText
                    color: parent.parent.modelData.liveTxText === "–" ? Theme.ink3 : Theme.ink2
                    font.pixelSize: 11
                    font.features: Theme.tabular
                }
            }
        }
    }
    Text {
        visible: section.rows.length > 3 && !section.expanded
        Layout.topMargin: 2
        text: S.tr2(S.rev, "traffic.expand").replace("%d", section.rows.length - 3)
        color: Theme.ink2
        font.pixelSize: 12
        TapHandler { onTapped: section.expanded = true }
        HoverHandler { cursorShape: Qt.PointingHandCursor }
    }
    Text {
        Layout.fillWidth: true
        Layout.topMargin: 4
        text: section.traffic.available === false ? S.tr2(S.rev, "traffic.unavailable.sampler")
              : section.rows.length === 0 ? S.tr2(S.rev, "traffic.empty")
              : section.traffic.includeTools ? S.tr2(S.rev, "traffic.includesTools") : S.tr2(S.rev, "traffic.excludesTools")
        color: Theme.ink3
        font.pixelSize: 11
        wrapMode: Text.WordWrap
    }
}
