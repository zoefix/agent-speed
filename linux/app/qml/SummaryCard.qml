import QtQuick
import QtQuick.Layouts

// DESIGN 4b summary card: title, large value, a secondary line and a small sparkline of the range.
Rectangle {
    id: card
    property string title
    property string value
    property string unit
    property string detail
    property string deltaText
    property bool deltaGood: true
    property var series: []
    property color tone: Theme.accent
    Layout.fillWidth: true
    Layout.preferredWidth: 1
    implicitHeight: 108
    radius: 12
    color: Theme.card
    border.width: 1
    border.color: Theme.border
    Accessible.role: Accessible.StaticText
    Accessible.name: title + " " + value + " " + unit

    ColumnLayout {
        anchors.fill: parent
        anchors.margins: 14
        spacing: 4
        Text {
            text: card.title
            color: Theme.ink2
            font.pixelSize: 12
            elide: Text.ElideRight
            Layout.fillWidth: true
        }
        Row {
            spacing: 4
            Text {
                id: big
                text: card.value
                color: Theme.ink
                font.pixelSize: 24
                font.weight: Font.DemiBold
                font.features: Theme.tabular
            }
            Text {
                anchors.baseline: big.baseline
                text: card.unit
                color: Theme.ink3
                font.pixelSize: 11
            }
        }
        RowLayout {
            Layout.fillWidth: true
            spacing: 6
            Text {
                visible: card.deltaText.length > 0
                text: card.deltaText
                color: card.deltaGood ? (Theme.dark ? "#4ADE80" : "#15803D") : (Theme.dark ? "#F87171" : "#B91C1C")
                font.pixelSize: 11
                font.weight: Font.DemiBold
            }
            Text {
                Layout.fillWidth: true
                text: card.detail
                color: Theme.ink3
                font.pixelSize: 11
                elide: Text.ElideRight
            }
        }
        Canvas {
            id: spark
            Layout.fillWidth: true
            Layout.fillHeight: true
            onWidthChanged: requestPaint()
            Connections {
                target: card
                function onSeriesChanged() { spark.requestPaint() }
            }
            onPaint: {
                var ctx = getContext("2d")
                ctx.reset()
                var s = card.series || []
                if (s.length < 2) return
                var max = 0
                for (var i = 0; i < s.length; ++i) max = Math.max(max, s[i])
                if (max <= 0) return
                ctx.strokeStyle = card.tone
                ctx.lineWidth = 1.4
                ctx.lineJoin = "round"
                ctx.beginPath()
                for (var j = 0; j < s.length; ++j) {
                    var x = j / (s.length - 1) * width
                    var y = height - 1 - (s[j] / max) * (height - 3)
                    if (j === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y)
                }
                ctx.stroke()
            }
        }
    }
}
