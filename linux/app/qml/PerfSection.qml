import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtCore
import "Format.js" as Format

// DESIGN 12 Usage History: performance (性能表現) — every (source, model) with timed requests in the window's range:
// the selected row's daily trend on the log scale, then the pooled table (wide: one column per component; medium: a
// bar per component; narrow: a list). METRICS 6.11.
Rectangle {
    id: section
    property string first: ""
    property string last: ""
    property string source: "all"

    readonly property var perf: history.perfRevision >= 0 && S.rev >= 0 ? history.perfSection(first, last, source === "all" ? "" : source) : ({})
    readonly property var rows: sorted(perf.rows || [])
    readonly property bool wide: width - 36 >= 860
    readonly property bool narrow: width - 36 < 600

    implicitHeight: content.implicitHeight + 32
    radius: 12
    color: Theme.card
    border.width: 1
    border.color: Theme.border
    Accessible.role: Accessible.Grouping
    Accessible.name: S.tr2(S.rev, "history.perf.title")

    Settings {
        id: remembered
        category: "history"
        property string perfSort: "score"
        property bool perfDescending: true
    }

    // Default sort: score descending, rows without a score after by timed requests; then the model id.
    function sorted(list) {
        var key = remembered.perfSort, down = remembered.perfDescending
        var copy = list.slice()
        copy.sort(function(a, b) {
            var x, y
            if (key === "model") { x = a.modelName; y = b.modelName }
            else if (key === "usual") { x = a.usual === undefined ? -1 : a.usual; y = b.usual === undefined ? -1 : b.usual }
            else if (key === "timed") { x = a.n; y = b.n }
            else { x = a.score; y = b.score }
            if (x !== y) return (x < y ? -1 : 1) * (down ? -1 : 1)
            if (key === "score" && a.n !== b.n) return b.n - a.n
            return a.modelName < b.modelName ? -1 : a.modelName > b.modelName ? 1 : 0
        })
        return copy
    }
    function sortBy(key) {
        if (remembered.perfSort === key) remembered.perfDescending = !remembered.perfDescending
        else {
            remembered.perfSort = key
            remembered.perfDescending = key !== "model"
        }
    }

    // Selection picks the chart's series: kept per (source, model) while the window is open, else the fullest row.
    property string chosen: ""
    readonly property var selected: {
        var fallback = null
        for (var i = 0; i < rows.length; ++i) {
            if (rows[i].key === chosen) return rows[i]
            if (!fallback || rows[i].n > fallback.n) fallback = rows[i]
        }
        return fallback
    }

    ColumnLayout {
        id: content
        x: 18
        y: 16
        width: section.width - 36
        spacing: 0

        // Header: the title and ⓘ.
        RowLayout {
            Layout.fillWidth: true
            spacing: 6
            Text {
                text: S.tr2(S.rev, "history.perf.title")
                color: Theme.ink
                font.pixelSize: 15
                font.weight: Font.DemiBold
                Accessible.role: Accessible.Heading
            }
            IconButton {
                glyph: "info"
                Layout.preferredWidth: 20
                Layout.preferredHeight: 20
                label: S.tr2(S.rev, "history.perf.info.a11y")
                ToolTip.visible: hovered && !info.visible
                ToolTip.text: label
                onClicked: info.open()
                Popup {
                    id: info
                    y: parent.height + 6
                    width: 340
                    padding: 14
                    background: Rectangle { radius: 10; color: Theme.cardFill; border.width: 1; border.color: Theme.cardLine }
                    contentItem: Column {
                        spacing: 8
                        Text {
                            width: 312
                            text: S.tr2(S.rev, "history.perf.info")
                            color: Theme.ink2
                            font.pixelSize: 12
                            lineHeight: 1.3
                            wrapMode: Text.WordWrap
                        }
                        Repeater {
                            model: ["throughput", "latency", "reasoning", "pace", "toolTime", "toolSuccess", "cache"]
                            Column {
                                required property string modelData
                                width: 312
                                Text { text: S.tr2(S.rev, "perf.comp." + parent.modelData); color: Theme.ink; font.pixelSize: 12; font.weight: Font.DemiBold }
                                Text { width: 312; text: S.tr2(S.rev, "perf.comp." + parent.modelData + ".desc"); color: Theme.caption; font.pixelSize: 11; wrapMode: Text.WordWrap }
                            }
                        }
                    }
                }
            }
            Item { Layout.fillWidth: true }
        }

        // States: re-analysing with no records yet, or nothing timed in the range.
        Text {
            visible: section.rows.length === 0 && history.analyzing
            Layout.topMargin: 14
            text: S.tr2(S.rev, "history.perf.analyzing")
            color: Theme.ink2
            font.pixelSize: 12
        }
        Repeater {
            model: section.rows.length === 0 && history.analyzing ? 4 : 0
            Rectangle {
                Layout.fillWidth: true
                Layout.topMargin: 8
                height: 22
                radius: 6
                color: Theme.track
            }
        }
        ColumnLayout {
            visible: section.rows.length === 0 && !history.analyzing
            Layout.fillWidth: true
            Layout.topMargin: 18
            Layout.bottomMargin: 6
            spacing: 6
            Glyph { name: "gauge"; color: Theme.ink3; Layout.alignment: Qt.AlignHCenter; Layout.preferredWidth: 22; Layout.preferredHeight: 22 }
            Text { Layout.alignment: Qt.AlignHCenter; text: S.tr2(S.rev, "history.perf.empty"); color: Theme.ink2; font.pixelSize: 13; font.weight: Font.Medium }
            Text {
                Layout.fillWidth: true
                horizontalAlignment: Text.AlignHCenter
                text: S.tr2(S.rev, "history.perf.empty.hint")
                color: Theme.ink3
                font.pixelSize: 12
                wrapMode: Text.WordWrap
            }
        }

        // Trend: the title line with the legend, then the chart.
        RowLayout {
            visible: section.selected !== null
            Layout.fillWidth: true
            Layout.topMargin: 12
            spacing: 8
            Text {
                Layout.fillWidth: true
                textFormat: Text.StyledText
                text: section.selected ? Format.fill(S.tr2(S.rev, "history.perf.trend"), ["<font color='" + Theme.ink + "'>" + section.selected.modelName + "</font> · " + section.selected.sourceName])
                                         + (section.perf.single ? " · " + Format.fill(S.tr2(S.rev, "history.perf.trend.days"), [section.last]) : "") : ""
                color: Theme.ink2
                font.pixelSize: 12
                elide: Text.ElideRight
            }
            Rectangle { width: 12; height: 1; color: Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.35) }
            Text { text: "1000 " + S.tr2(S.rev, "perf.reference"); color: Theme.ink3; font.pixelSize: 11 }
            Row {
                visible: section.selected !== null && section.selected.usual !== undefined
                spacing: 2
                Layout.alignment: Qt.AlignVCenter
                Repeater { model: 2; Rectangle { width: 4; height: 1; color: section.selected ? Qt.rgba(Theme.sourceColor(section.selected.source).r, Theme.sourceColor(section.selected.source).g, Theme.sourceColor(section.selected.source).b, 0.6) : "transparent" } }
            }
            Text {
                visible: section.selected !== null && section.selected.usual !== undefined
                text: section.selected ? Format.fill(S.tr2(S.rev, "history.perf.usual.line"), [section.selected.usualText]) : ""
                color: Theme.ink3
                font.pixelSize: 11
            }
        }
        PerfTrend {
            visible: section.selected !== null
            Layout.fillWidth: true
            Layout.topMargin: 6
            Layout.preferredHeight: section.wide ? 150 : section.narrow ? 120 : 132
            points: section.selected ? section.selected.trend : []
            tint: section.selected ? Theme.sourceColor(section.selected.source) : Theme.accent
            usual: section.selected && section.selected.usual !== undefined ? section.selected.usual : undefined
            highlight: section.perf.single ? section.last : ""
            emptyText: section.selected ? Format.fill(S.tr2(S.rev, "a11y.history.perf.chart.empty"), [section.selected.modelName + " · " + section.selected.sourceName]) : ""
            // The chart as an image: the lowest, highest and latest daily scores, or the empty sentence.
            Accessible.name: {
                var scored = points.filter(function(p) { return p.score !== null && p.score !== undefined })
                if (scored.length === 0 || !section.selected) return emptyText
                var low = scored[0], high = scored[0]
                for (var i = 1; i < scored.length; ++i) {
                    if (scored[i].score < low.score) low = scored[i]
                    if (scored[i].score > high.score) high = scored[i]
                }
                return Format.fill(S.tr2(S.rev, "a11y.history.perf.chart"), [section.selected.modelName + " · " + section.selected.sourceName,
                                                                            low.text, high.text, scored[scored.length - 1].text])
            }
        }

        // Table.
        Item {
            id: table
            visible: section.rows.length > 0
            Layout.fillWidth: true
            Layout.topMargin: 12
            implicitHeight: (section.narrow ? 0 : 28) + list.contentHeight
            readonly property var parts: ["throughput", "latency", "reasoning", "pace", "toolTime", "toolSuccess", "cache"]
            readonly property real modelWidth: Math.max(150, width - 64 * 3 - (section.wide ? 7 * 66 : 64))
            function columnX(index) { return modelWidth + index * 66 }

            Row {
                id: header
                visible: !section.narrow
                height: 28
                Repeater {
                    model: [{ key: "model", w: table.modelWidth, title: S.tr2(S.rev, "history.perf.column.model") },
                            { key: "score", w: 80, title: S.tr2(S.rev, "history.perf.column.score") },
                            { key: "usual", w: 60, title: S.tr2(S.rev, "history.perf.column.usual") },
                            { key: "timed", w: 64, title: S.tr2(S.rev, "history.perf.column.timed") }]
                    Item {
                        required property var modelData
                        width: modelData.w
                        height: 28
                        Text {
                            anchors.verticalCenter: parent.verticalCenter
                            anchors.right: parent.modelData.key === "model" ? undefined : parent.right
                            anchors.rightMargin: 6
                            text: parent.modelData.title + (remembered.perfSort === parent.modelData.key ? (remembered.perfDescending ? " ▾" : " ▴") : "")
                            color: Theme.ink3
                            font.pixelSize: 11
                            font.weight: Font.DemiBold
                        }
                        MouseArea { anchors.fill: parent; cursorShape: Qt.PointingHandCursor; onClicked: section.sortBy(parent.modelData.key) }
                        ToolTip.visible: headerHover.hovered && modelData.key === "timed"
                        ToolTip.text: S.tr2(S.rev, "history.perf.column.timed.full")
                        HoverHandler { id: headerHover }
                    }
                }
                Repeater {
                    model: section.wide ? table.parts : ["parts"]
                    Item {
                        required property string modelData
                        width: section.wide ? 66 : 64
                        height: 28
                        Text {
                            anchors.verticalCenter: parent.verticalCenter
                            anchors.right: parent.right
                            anchors.rightMargin: 6
                            text: S.tr2(S.rev, "history.perf.column." + parent.modelData)
                            color: Theme.ink3
                            font.pixelSize: 11
                            font.weight: Font.DemiBold
                            elide: Text.ElideRight
                            width: Math.min(implicitWidth, parent.width - 6)
                        }
                        HoverHandler { id: partHover }
                        ToolTip.visible: partHover.hovered && modelData !== "parts"
                        ToolTip.text: modelData === "parts" ? "" : S.tr2(S.rev, "perf.comp." + modelData) + "\n" + S.tr2(S.rev, "perf.comp." + modelData + ".desc")
                    }
                }
            }

            ListView {
                id: list
                y: section.narrow ? 0 : 28
                width: parent.width
                height: contentHeight
                interactive: false
                model: section.rows
                delegate: Rectangle {
                    id: rowItem
                    required property var modelData
                    required property int index
                    readonly property bool isSelected: section.selected !== null && section.selected.key === modelData.key
                    readonly property bool scored: modelData.score >= 0
                    width: list.width
                    height: section.narrow ? narrowColumn.implicitHeight + 14 : 30
                    radius: 6
                    color: isSelected ? Theme.pressed : rowHover.hovered ? Theme.hover : index % 2 ? Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.02) : "transparent"
                    Accessible.role: Accessible.Row
                    Accessible.name: modelData.modelName + ", " + modelData.sourceName + ", " + modelData.scoreText
                    HoverHandler { id: rowHover }
                    TapHandler { onTapped: section.chosen = rowItem.modelData.key }

                    // Wide and medium: one line of cells.
                    Row {
                        visible: !section.narrow
                        anchors.verticalCenter: parent.verticalCenter
                        Item {
                            width: table.modelWidth
                            height: 30
                            Row {
                                anchors.verticalCenter: parent.verticalCenter
                                x: 6
                                spacing: 6
                                Rectangle { anchors.verticalCenter: parent.verticalCenter; width: 7; height: 7; radius: 3.5; color: Theme.sourceColor(rowItem.modelData.source) }
                                Text {
                                    anchors.verticalCenter: parent.verticalCenter
                                    width: Math.min(implicitWidth, table.modelWidth - 120)
                                    text: rowItem.modelData.modelName
                                    color: rowItem.scored ? Theme.ink : Theme.ink2
                                    font.pixelSize: 12
                                    font.weight: Font.Medium
                                    elide: Text.ElideMiddle
                                }
                                Text {
                                    visible: !rowItem.scored
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: S.tr2(S.rev, "history.perf.sample.low")
                                    color: Theme.ink3
                                    font.pixelSize: 11
                                }
                                Text {
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: rowItem.modelData.sourceName
                                    color: Theme.ink2
                                    font.pixelSize: 11
                                }
                            }
                        }
                        Item {
                            width: 80
                            height: 30
                            Row {
                                anchors.right: parent.right
                                anchors.rightMargin: 6
                                anchors.verticalCenter: parent.verticalCenter
                                spacing: 6
                                Text {
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: rowItem.modelData.scoreText
                                    color: rowItem.scored ? Theme.ink : Theme.ink3
                                    font.pixelSize: 13
                                    font.weight: Font.DemiBold
                                    font.features: Theme.tabular
                                    HoverHandler { id: scoreHover }
                                    ToolTip.visible: scoreHover.hovered && !rowItem.scored
                                    ToolTip.text: rowItem.modelData.sampleHint
                                }
                                PerfMeter { anchors.verticalCenter: parent.verticalCenter; width: 36; height: 4; tickHeight: 8; f: rowItem.modelData.f; visible: rowItem.scored }
                            }
                        }
                        Item {
                            width: 60
                            height: 30
                            Text {
                                anchors.right: parent.right
                                anchors.rightMargin: 6
                                anchors.verticalCenter: parent.verticalCenter
                                text: rowItem.modelData.usualText
                                color: rowItem.modelData.usual !== undefined ? Theme.ink2 : Theme.ink3
                                font.pixelSize: 12
                                font.features: Theme.tabular
                                HoverHandler { id: usualHover }
                                ToolTip.visible: usualHover.hovered
                                ToolTip.text: rowItem.modelData.usualHint
                            }
                        }
                        Item {
                            width: 64
                            height: 30
                            Text {
                                anchors.right: parent.right
                                anchors.rightMargin: 6
                                anchors.verticalCenter: parent.verticalCenter
                                text: rowItem.modelData.nText
                                color: rowItem.scored ? Theme.ink : Theme.ink2
                                font.pixelSize: 12
                                font.features: Theme.tabular
                            }
                        }
                        // Wide: the seven components.
                        Repeater {
                            model: section.wide ? rowItem.modelData.parts : []
                            Item {
                                required property var modelData
                                width: 66
                                height: 30
                                Text {
                                    anchors.right: parent.right
                                    anchors.rightMargin: 6
                                    anchors.verticalCenter: parent.verticalCenter
                                    text: parent.modelData.value
                                    color: parent.modelData.present ? (rowItem.scored ? Theme.ink : Theme.ink2) : Theme.ink3
                                    font.pixelSize: 12
                                    font.features: Theme.tabular
                                    width: Math.min(implicitWidth, 60)
                                    elide: Text.ElideRight
                                    HoverHandler { id: partValueHover }
                                    ToolTip.visible: partValueHover.hovered && !parent.modelData.present
                                    ToolTip.text: parent.modelData.hint || ""
                                }
                            }
                        }
                        // Medium: seven bars, a dashed 1000 line across them.
                        Item {
                            visible: !section.wide
                            width: 64
                            height: 30
                            Accessible.role: Accessible.StaticText
                            Accessible.name: {
                                var parts = []
                                for (var i = 0; i < rowItem.modelData.parts.length; ++i) parts.push(rowItem.modelData.parts[i].column + " " + rowItem.modelData.parts[i].sub)
                                return Format.fill(S.tr2(S.rev, "a11y.history.perf.parts"), [parts.join(S.tr2(S.rev, "list.separator"))])
                            }
                            Row {
                                id: bars
                                anchors.right: parent.right
                                anchors.rightMargin: 6
                                anchors.verticalCenter: parent.verticalCenter
                                spacing: 2
                                Repeater {
                                    model: rowItem.modelData.parts
                                    Item {
                                        required property var modelData
                                        width: 5
                                        height: 20
                                        Rectangle { visible: parent.modelData.present; anchors.fill: parent; radius: 1; color: Theme.track }
                                        Rectangle {
                                            visible: parent.modelData.present
                                            anchors.bottom: parent.bottom
                                            width: 5
                                            height: Math.max(1, parent.modelData.f * 20)
                                            radius: 1
                                            color: Theme.scoreInk
                                        }
                                        Column {
                                            visible: !parent.modelData.present
                                            anchors.bottom: parent.bottom
                                            anchors.horizontalCenter: parent.horizontalCenter
                                            spacing: 2
                                            Repeater { model: 2; Rectangle { width: 1; height: 1; color: Theme.ink3 } }
                                        }
                                        HoverHandler { id: barHover }
                                        ToolTip.visible: barHover.hovered
                                        ToolTip.text: modelData.name + "  " + modelData.value + (modelData.present ? "  ·  " + modelData.sub : "")
                                    }
                                }
                            }
                            Canvas {
                                anchors.fill: bars
                                onPaint: {
                                    var ctx = getContext("2d")
                                    ctx.reset()
                                    ctx.strokeStyle = Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.35)
                                    ctx.setLineDash([2, 2])
                                    ctx.beginPath(); ctx.moveTo(0, height / 2 + 0.5); ctx.lineTo(width, height / 2 + 0.5); ctx.stroke()
                                }
                                Component.onCompleted: requestPaint()
                            }
                        }
                    }

                    // Narrow: a list row of three lines.
                    Column {
                        id: narrowColumn
                        visible: section.narrow
                        x: 8
                        y: 7
                        width: parent.width - 16
                        spacing: 2
                        Item {
                            width: parent.width
                            height: 18
                            Row {
                                spacing: 6
                                anchors.verticalCenter: parent.verticalCenter
                                Rectangle { anchors.verticalCenter: parent.verticalCenter; width: 7; height: 7; radius: 3.5; color: Theme.sourceColor(rowItem.modelData.source) }
                                Text { text: rowItem.modelData.modelName; color: Theme.ink; font.pixelSize: 12; font.weight: Font.Medium; elide: Text.ElideMiddle; width: Math.min(implicitWidth, narrowColumn.width - 110) }
                            }
                            Row {
                                anchors.right: parent.right
                                anchors.verticalCenter: parent.verticalCenter
                                spacing: 6
                                Text { text: rowItem.modelData.scoreText; color: rowItem.scored ? Theme.ink : Theme.ink3; font.pixelSize: 13; font.weight: Font.DemiBold; font.features: Theme.tabular }
                                PerfMeter { anchors.verticalCenter: parent.verticalCenter; width: 36; height: 4; tickHeight: 8; f: rowItem.modelData.f; visible: rowItem.scored }
                            }
                        }
                        Text {
                            width: parent.width
                            text: rowItem.modelData.sourceName + " · " + Format.fill(S.tr2(S.rev, "history.perf.requests"), [rowItem.modelData.nText]) + " · "
                                  + rowItem.modelData.usualText + " " + S.tr2(S.rev, "history.perf.column.usual")
                            color: Theme.ink2
                            font.pixelSize: 11
                            elide: Text.ElideRight
                        }
                        Text {
                            width: parent.width
                            visible: text.length > 0
                            text: rowItem.modelData.partsText
                            color: Theme.ink2
                            font.pixelSize: 11
                            wrapMode: Text.WordWrap
                        }
                    }
                }
            }
        }

        Text {
            visible: section.rows.length > 0
            Layout.fillWidth: true
            Layout.topMargin: 10
            text: S.tr2(S.rev, "history.perf.footnote")
            color: Theme.ink3
            font.pixelSize: 11
            wrapMode: Text.WordWrap
        }
    }
}
