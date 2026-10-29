import QtQuick
import QtQuick.Effects
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 12 score card (性能分析卡): the breakdown behind an agent's 性能, shown beside the dashboard or a board while
// its row is hovered. It never takes focus and lets the pointer through, like a tooltip.
Window {
    id: card
    property var info: ({})
    property real availableHeight: 10000
    readonly property int pad: 18
    // Fit: a card taller than the screen drops the components' meanings first, then the footnote. Decided when the card
    // opens (fit()), so the layout never feeds back into itself.
    property bool dropDesc: false
    property bool dropFootnote: false
    function fit() {
        dropDesc = false
        dropFootnote = false
        Qt.callLater(function() {
            if (content.implicitHeight + 28 > availableHeight - 16) dropDesc = true
            Qt.callLater(function() { if (content.implicitHeight + 28 > availableHeight - 16) dropFootnote = true })
        })
    }
    readonly property bool scored: (info.shown || -1) >= 0
    readonly property var usual: info.usual || ({})

    flags: Qt.ToolTip | Qt.FramelessWindowHint | Qt.WindowTransparentForInput | Qt.WindowDoesNotAcceptFocus | Qt.NoDropShadowWindowHint
    color: "transparent"
    width: 320 + pad * 2
    height: Math.ceil(surface.height) + pad * 2

    // The shown number counts to each new value (DESIGN 12 Motion); the scale follows it.
    property real shownValue: info.shown === undefined ? -1 : info.shown
    property real target: info.shown === undefined ? -1 : info.shown
    onTargetChanged: {
        const from = Math.round(shownValue)
        if (visible && Format.perfCounts(from, target)) {
            count.stop()
            count.from = from
            count.to = target
            count.duration = Format.perfCountMs(from, target)
            count.start()
        } else {
            count.stop()
            shownValue = target
        }
    }
    NumberAnimation { id: count; target: card; property: "shownValue"; easing.type: Easing.OutCubic }

    Item {
        id: holder
        x: card.pad
        y: card.pad
        width: 320
        height: surface.height
        opacity: card.visible ? 1 : 0
        Behavior on opacity { NumberAnimation { duration: 150 } }

        Rectangle {
            id: caster
            anchors.fill: surface
            radius: 12
            color: "black"
            visible: false
        }
        MultiEffect {
            anchors.fill: caster
            source: caster
            visible: !softwareRendering
            shadowEnabled: true
            shadowColor: "#000000"
            shadowOpacity: Theme.dark ? 0.55 : 0.2
            shadowBlur: 1.0
            blurMax: 24
            shadowVerticalOffset: 6
        }
        Rectangle {
            id: surface
            width: 320
            height: content.implicitHeight + 28
            radius: 12
            color: Theme.cardFill
            border.width: 1
            border.color: Theme.cardLine

            ColumnLayout {
                id: content
                x: 14
                y: 14
                width: 292
                spacing: 0

                // 1. Header: source dot, project, the provisional chip; model · source.
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 7
                    Rectangle {
                        Layout.preferredWidth: 7
                        Layout.preferredHeight: 7
                        radius: 3.5
                        color: Theme.sourceColor(card.info.source || "")
                    }
                    Text {
                        Layout.fillWidth: true
                        text: card.info.project || ""
                        color: Theme.ink
                        font.pixelSize: 13
                        font.weight: Font.DemiBold
                        elide: Text.ElideRight
                    }
                    Item {
                        visible: card.info.provisional === true
                        Layout.preferredHeight: 18
                        Layout.preferredWidth: provisionalRow.implicitWidth + 14
                        Canvas {
                            anchors.fill: parent
                            onPaint: card.dottedOutline(getContext("2d"), width, height)
                            Component.onCompleted: requestPaint()
                        }
                        Row {
                            id: provisionalRow
                            anchors.centerIn: parent
                            spacing: 4
                            Canvas {
                                anchors.verticalCenter: parent.verticalCenter
                                width: 10
                                height: 10
                                onPaint: {
                                    const ctx = getContext("2d")
                                    ctx.reset()
                                    ctx.fillStyle = Theme.ink2
                                    for (let k = 0; k < 8; ++k) {
                                        const a = k * Math.PI / 4
                                        ctx.beginPath()
                                        ctx.arc(5 + 3.6 * Math.cos(a), 5 + 3.6 * Math.sin(a), 0.9, 0, 2 * Math.PI)
                                        ctx.fill()
                                    }
                                }
                                Component.onCompleted: requestPaint()
                            }
                            Text {
                                anchors.verticalCenter: parent.verticalCenter
                                text: card.info.provisionalChip || ""
                                color: Theme.ink2
                                font.pixelSize: 11
                                font.weight: Font.DemiBold
                            }
                        }
                    }
                }
                Text {
                    Layout.fillWidth: true
                    Layout.leftMargin: 14
                    Layout.topMargin: 3
                    text: card.info.meta || ""
                    color: Theme.ink2
                    font.pixelSize: 11
                    elide: Text.ElideRight
                }

                // 2. Score: eyebrow, the number with the usual chip, the scale, the summary.
                Rectangle { Layout.fillWidth: true; Layout.topMargin: 12; height: 1; color: Theme.hairline }
                Text {
                    visible: (card.info.eyebrow || "").length > 0
                    Layout.topMargin: 12
                    text: card.info.eyebrow || ""
                    color: Theme.ink2
                    font.pixelSize: 11
                }
                Item {
                    Layout.fillWidth: true
                    Layout.topMargin: (card.info.eyebrow || "").length > 0 ? 2 : 12
                    implicitHeight: big.implicitHeight
                    Text {
                        id: big
                        text: card.shownValue >= 0 ? Format.perfFull(Math.round(card.shownValue)) : "–"
                        color: !card.scored ? Theme.ink3 : card.info.form === "resting" ? Theme.ink2 : Theme.ink
                        font.pixelSize: 34
                        font.weight: card.scored ? Font.DemiBold : Font.Medium
                        font.letterSpacing: -0.68
                        font.features: Theme.tabular
                    }
                    ProvisionalDots {
                        x: 0
                        y: big.baselineOffset + 0.22 * 34 - height / 2
                        textWidth: big.advance.width > 0 ? big.advance.width : big.implicitWidth
                        pixelSize: 34
                        color: big.color
                        shown: card.info.provisional === true
                    }
                    Item {
                        id: usualChip
                        visible: card.scored && (card.usual.text || "").length > 0
                        anchors.right: parent.right
                        // Centred on the digits' cap height.
                        y: big.baselineOffset - 0.35 * 34 - height / 2
                        height: 18
                        width: usualText.implicitWidth + 14
                        Rectangle {
                            anchors.fill: parent
                            radius: 9
                            visible: card.usual.kind !== "none"
                            color: card.usual.kind === "up" ? Theme.passFill : Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.06)
                        }
                        Canvas {
                            anchors.fill: parent
                            visible: card.usual.kind === "none"
                            onPaint: card.dottedOutline(getContext("2d"), width, height)
                            onVisibleChanged: requestPaint()
                        }
                        Text {
                            id: usualText
                            anchors.centerIn: parent
                            text: card.usual.text || ""
                            color: card.usual.kind === "up" ? Theme.passInk : Theme.ink2
                            font.pixelSize: 11
                            font.weight: Font.DemiBold
                            font.features: Theme.tabular
                        }
                    }
                }
                // The scale: the log scale over the content width, 500 · 1000 · 2000 ticks, the pearl gradient fill.
                Item {
                    id: scale
                    Layout.fillWidth: true
                    Layout.topMargin: 10
                    implicitHeight: 6 + 4 + labels.implicitHeight
                    readonly property real f: card.shownValue >= 0 ? Format.perfScale(card.shownValue) : 0
                    Rectangle { id: track; width: parent.width; height: 6; radius: 3; color: Theme.track }
                    Rectangle {
                        visible: card.scored && card.info.provisional !== true && scale.f > 0
                        width: Math.max(6, scale.f * track.width)
                        height: 6
                        radius: 3
                        clip: true
                        opacity: card.info.form === "resting" ? 0.45 : 1
                        Rectangle {
                            width: track.width
                            height: 6
                            gradient: Gradient {
                                orientation: Gradient.Horizontal
                                GradientStop { position: 0; color: Theme.pearlC }
                                GradientStop { position: 0.5; color: Theme.pearlA }
                                GradientStop { position: 1; color: Theme.pearlB }
                            }
                        }
                    }
                    // A provisional fill: round dots of its height on a 2.2 × pitch.
                    Repeater {
                        model: card.scored && card.info.provisional === true ? Math.max(1, Math.floor((scale.f * track.width - 6) / 13.2) + 1) : 0
                        Rectangle {
                            required property int index
                            x: index * 13.2
                            width: 6
                            height: 6
                            radius: 3
                            opacity: card.info.form === "resting" ? 0.45 : 1
                            color: index * 13.2 / track.width < 0.5 ? Theme.pearlC : Theme.pearlA
                        }
                    }
                    Repeater {
                        model: [500, 1000, 2000]
                        Rectangle {
                            required property int modelData
                            x: Math.round(Format.perfScale(modelData) * track.width)
                            y: modelData === 1000 ? -4 : -2
                            width: 1
                            height: modelData === 1000 ? 14 : 10
                            color: Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, modelData === 1000 ? 0.45 : 0.18)
                        }
                    }
                    Item {
                        id: labels
                        y: 10
                        width: parent.width
                        implicitHeight: tick1000.implicitHeight
                        Repeater {
                            model: [500, 1000, 2000]
                            Text {
                                id: label
                                required property int modelData
                                x: Math.round(Format.perfScale(modelData) * track.width - implicitWidth / 2)
                                text: String(modelData)
                                color: modelData === 1000 ? Theme.ink2 : Theme.ink3
                                font.pixelSize: 9
                                font.weight: modelData === 1000 ? Font.DemiBold : Font.Normal
                                font.features: Theme.tabular
                            }
                        }
                        Text {
                            id: tick1000
                            anchors.right: parent.right
                            text: "4000"
                            color: Theme.ink3
                            font.pixelSize: 9
                            font.features: Theme.tabular
                        }
                    }
                }
                Text {
                    Layout.fillWidth: true
                    Layout.topMargin: 8
                    text: card.info.summary || ""
                    color: Theme.ink2
                    font.pixelSize: 12
                    lineHeight: 1.25
                    wrapMode: Text.WordWrap
                    maximumLineCount: 2
                    elide: Text.ElideRight
                }
                Text {
                    visible: (card.info.provisionalHint || "").length > 0
                    Layout.fillWidth: true
                    Layout.topMargin: 4
                    text: card.info.provisionalHint || ""
                    color: Theme.ink3
                    font.pixelSize: 11
                    lineHeight: 1.2
                    wrapMode: Text.WordWrap
                }

                // 3. Components: 速度 and 效率, each row label · value · mini meter · sub-score, then its meaning.
                Repeater {
                    model: card.info.groups || []
                    ColumnLayout {
                        id: group
                        required property var modelData
                        required property int index
                        Layout.fillWidth: true
                        Layout.topMargin: 14
                        spacing: 0
                        Text {
                            Layout.bottomMargin: 8
                            text: S.language === "en" ? group.modelData.title.toUpperCase() : group.modelData.title
                            color: Theme.ink3
                            font.pixelSize: S.language === "en" ? 10 : 11
                            font.weight: Font.DemiBold
                            font.letterSpacing: 0.66
                        }
                        Repeater {
                            model: group.modelData.rows
                            ColumnLayout {
                                id: compRow
                                required property var modelData
                                required property int index
                                Layout.fillWidth: true
                                Layout.topMargin: index > 0 ? 9 : 0
                                spacing: 0
                                RowLayout {
                                    Layout.fillWidth: true
                                    spacing: 0
                                    Text {
                                        Layout.fillWidth: true
                                        text: compRow.modelData.label
                                        color: Theme.ink
                                        font.pixelSize: 12
                                        font.weight: Font.DemiBold
                                        elide: Text.ElideRight
                                    }
                                    Text {
                                        Layout.preferredWidth: Math.max(64, card.valueColumn)
                                        horizontalAlignment: Text.AlignRight
                                        text: compRow.modelData.value
                                        color: compRow.modelData.present ? Theme.ink : Theme.ink3
                                        font.pixelSize: compRow.modelData.present ? 12 : 11.5
                                        font.weight: compRow.modelData.present ? Font.Medium : Font.Normal
                                        font.features: Theme.tabular
                                    }
                                    PerfMeter {
                                        Layout.leftMargin: 10
                                        Layout.preferredWidth: 44
                                        Layout.preferredHeight: 4
                                        tickHeight: 8
                                        f: compRow.modelData.f
                                        present: compRow.modelData.present
                                    }
                                    Text {
                                        Layout.leftMargin: 8
                                        Layout.preferredWidth: Math.max(34, card.subColumn)
                                        horizontalAlignment: Text.AlignRight
                                        text: compRow.modelData.sub
                                        color: compRow.modelData.present ? Theme.ink : Theme.ink3
                                        font.pixelSize: 12
                                        font.weight: Font.DemiBold
                                        font.features: Theme.tabular
                                    }
                                }
                                Text {
                                    visible: !card.dropDesc
                                    Layout.fillWidth: true
                                    Layout.topMargin: 3
                                    text: compRow.modelData.desc
                                    color: Theme.caption
                                    font.pixelSize: 10
                                    lineHeight: 1.2
                                    wrapMode: Text.WordWrap
                                }
                            }
                        }
                    }
                }

                // 4. 行為.
                Text {
                    visible: (card.info.signals || []).length > 0
                    Layout.topMargin: 14
                    Layout.bottomMargin: 8
                    text: S.language === "en" ? (card.info.signalsTitle || "").toUpperCase() : (card.info.signalsTitle || "")
                    color: Theme.ink3
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                    font.letterSpacing: 0.66
                }
                Repeater {
                    model: card.info.signals || []
                    RowLayout {
                        required property var modelData
                        required property int index
                        Layout.fillWidth: true
                        Layout.topMargin: index > 0 ? 6 : 0
                        spacing: 6
                        Glyph {
                            Layout.alignment: Qt.AlignTop
                            Layout.topMargin: 1
                            Layout.preferredWidth: 13
                            Layout.preferredHeight: 13
                            name: parent.modelData.warn ? "loop" : "sparkle"
                            color: parent.modelData.warn ? Theme.amber : Theme.ink3
                        }
                        Text {
                            Layout.fillWidth: true
                            text: parent.modelData.text
                            color: parent.modelData.warn ? Theme.ink : Theme.ink2
                            font.pixelSize: 12
                            lineHeight: 1.2
                            wrapMode: Text.WordWrap
                        }
                    }
                }

                // 5. Facts and 6. the footnote.
                Rectangle {
                    visible: (card.info.facts || []).length > 0
                    Layout.fillWidth: true
                    Layout.topMargin: 12
                    height: 1
                    color: Theme.hairline
                }
                Repeater {
                    model: card.info.facts || []
                    Text {
                        required property var modelData
                        required property int index
                        Layout.fillWidth: true
                        Layout.topMargin: index === 0 ? 9 : 2
                        text: modelData
                        color: Theme.ink2
                        font.pixelSize: 11
                        elide: Text.ElideRight
                    }
                }
                Text {
                    visible: !card.dropFootnote
                    Layout.fillWidth: true
                    Layout.topMargin: 10
                    text: card.info.footnote || ""
                    color: Theme.caption
                    font.pixelSize: 10
                    lineHeight: 1.25
                    wrapMode: Text.WordWrap
                }
            }
        }
    }

    // The component columns are as wide as the widest value and sub-score shown.
    FontMetrics { id: valueMetrics; font.pixelSize: 12; font.weight: Font.Medium }
    FontMetrics { id: subMetrics; font.pixelSize: 12; font.weight: Font.DemiBold }
    readonly property real valueColumn: widest(valueMetrics, "value")
    readonly property real subColumn: widest(subMetrics, "sub")
    function widest(metrics, field) {
        let w = 0
        const groups = info.groups || []
        for (let g = 0; g < groups.length; ++g)
            for (let r = 0; r < groups[g].rows.length; ++r) w = Math.max(w, metrics.advanceWidth(groups[g].rows[r][field]))
        return Math.ceil(w)
    }
    // A 1 pt dotted outline of the label color at 35 % around a fully rounded chip.
    function dottedOutline(ctx, w, h) {
        ctx.reset()
        ctx.strokeStyle = Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.35)
        ctx.lineWidth = 1
        ctx.setLineDash([1, 2])
        const r = (h - 1) / 2
        ctx.beginPath()
        ctx.moveTo(0.5 + r, 0.5)
        ctx.lineTo(w - 0.5 - r, 0.5)
        ctx.arc(w - 0.5 - r, h / 2, r, -Math.PI / 2, Math.PI / 2)
        ctx.lineTo(0.5 + r, h - 0.5)
        ctx.arc(0.5 + r, h / 2, r, Math.PI / 2, 3 * Math.PI / 2)
        ctx.stroke()
    }
}
