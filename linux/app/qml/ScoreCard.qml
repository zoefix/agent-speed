import QtQuick
import QtQuick.Effects
import QtQuick.Layouts

// DESIGN 12 score card: the breakdown behind an agent's 智力, shown beside the dashboard while its row is hovered. It
// never takes focus and lets the pointer through, like a tooltip.
Window {
    id: card
    property var info: ({})
    property real availableHeight: 10000
    readonly property int pad: 18
    // Fit: a card taller than the screen drops the dimension meanings first, then the footnote. Decided when the card
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
    readonly property bool supported: info.supported === true

    flags: Qt.ToolTip | Qt.FramelessWindowHint | Qt.WindowTransparentForInput | Qt.WindowDoesNotAcceptFocus | Qt.NoDropShadowWindowHint
    color: "transparent"
    width: 300 + pad * 2
    height: Math.ceil(surface.height) + pad * 2

    function chipColor(state) { return state === "passing" ? Theme.passInk : state === "failing" ? Theme.failInk : Theme.ink2 }
    function chipFill(state) { return state === "passing" ? Theme.passFill : state === "failing" ? Theme.failFill : "transparent" }
    function chipGlyph(state) {
        return state === "passing" ? "checkCircle" : state === "failing" ? "alertCircle" : state === "pending" ? "clock" : state === "unverified" ? "dotRing" : ""
    }

    Item {
        id: holder
        x: card.pad
        y: card.pad
        width: 300
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
            width: 300
            height: content.implicitHeight + 28
            radius: 12
            color: Theme.cardFill
            border.width: 1
            border.color: Theme.cardLine

            ColumnLayout {
                id: content
                x: 14
                y: 14
                width: 272
                spacing: 0

                // 1. Header: source dot, project, state chip; model · source.
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
                        visible: card.supported
                        Layout.preferredHeight: 18
                        Layout.preferredWidth: chipRow.implicitWidth + 13
                        Rectangle {
                            anchors.fill: parent
                            radius: 9
                            color: card.chipFill(card.info.state)
                            border.width: card.info.state === "passing" || card.info.state === "failing" ? 0 : 1
                            border.color: Qt.rgba(Theme.ink2.r, Theme.ink2.g, Theme.ink2.b, 0.35)
                        }
                        Row {
                            id: chipRow
                            anchors.centerIn: parent
                            spacing: 4
                            Glyph {
                                visible: name.length > 0
                                anchors.verticalCenter: parent.verticalCenter
                                width: 10
                                height: 10
                                name: card.chipGlyph(card.info.state)
                                color: card.chipColor(card.info.state)
                            }
                            Text {
                                anchors.verticalCenter: parent.verticalCenter
                                text: card.info.chip || ""
                                color: card.chipColor(card.info.state)
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

                // A source without scores.
                Rectangle { visible: !card.supported; Layout.fillWidth: true; Layout.topMargin: 12; height: 1; color: Theme.hairline }
                Text {
                    visible: !card.supported
                    Layout.fillWidth: true
                    Layout.topMargin: 12
                    text: card.info.unsupported || ""
                    color: Theme.ink2
                    font.pixelSize: 12
                    lineHeight: 1.3
                    wrapMode: Text.WordWrap
                }

                // 2. Score: eyebrow, the number over /100, the status line and the composition bar.
                Rectangle { visible: card.supported; Layout.fillWidth: true; Layout.topMargin: 12; height: 1; color: Theme.hairline }
                Text {
                    visible: card.supported && (card.info.eyebrow || "").length > 0
                    Layout.topMargin: 12
                    text: card.info.eyebrow || ""
                    color: Theme.ink2
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                }
                Item {
                    visible: card.supported
                    Layout.fillWidth: true
                    Layout.topMargin: (card.info.eyebrow || "").length > 0 ? 2 : 12
                    implicitHeight: big.implicitHeight
                    Text {
                        id: big
                        text: card.info.number || "–"
                        color: card.info.number === "–" ? Theme.ink3 : card.info.form === "resting" ? Theme.ink2 : Theme.ink
                        font.pixelSize: 34
                        font.weight: card.info.number === "–" ? Font.Medium : Font.DemiBold
                        font.letterSpacing: -0.6
                        font.features: Theme.tabular
                    }
                    Text {
                        anchors.left: big.right
                        anchors.leftMargin: 3
                        anchors.baseline: big.baseline
                        text: "/100"
                        color: Theme.ink3
                        font.pixelSize: 13
                        font.weight: Font.Medium
                        font.features: Theme.tabular
                    }
                    ProofDots {
                        x: 0
                        y: big.baselineOffset + 0.22 * 34 - height / 2
                        textWidth: big.advance.width > 0 ? big.advance.width : big.implicitWidth
                        pixelSize: 34
                        color: big.color
                        shown: card.info.proof === true
                    }
                }
                Text {
                    visible: card.supported
                    Layout.fillWidth: true
                    Layout.topMargin: 6
                    text: card.info.status || ""
                    color: Theme.ink2
                    font.pixelSize: 12
                    lineHeight: 1.25
                    wrapMode: Text.WordWrap
                    maximumLineCount: 2
                    elide: Text.ElideRight
                }
                Item {
                    id: comp
                    visible: card.supported
                    Layout.fillWidth: true
                    Layout.topMargin: 10
                    implicitHeight: 6
                    readonly property var parts: card.info.comp || []
                    Rectangle { anchors.fill: parent; radius: 3; color: Theme.track }
                    Row {
                        anchors.fill: parent
                        spacing: 0
                        Repeater {
                            model: comp.parts
                            Item {
                                required property var modelData
                                required property int index
                                readonly property real span: Math.max(modelData.value, modelData.ghost) / 100 * comp.width
                                width: span > 0 ? span + (index < 4 ? 2 : 0) : 0
                                height: 6
                                Rectangle {
                                    visible: parent.modelData.value > 0
                                    width: parent.modelData.value / 100 * comp.width
                                    height: 6
                                    radius: 1
                                    color: Theme.scoreInk
                                }
                                ProofDots {
                                    visible: parent.modelData.ghost > 0 && parent.modelData.value === 0
                                    anchors.verticalCenter: parent.verticalCenter
                                    textWidth: parent.span
                                    pixelSize: 26
                                    color: Theme.scoreInk
                                    shown: true
                                }
                            }
                        }
                    }
                }

                // 3. 評分構成: five dimensions.
                Text {
                    visible: card.supported
                    Layout.topMargin: 14
                    Layout.bottomMargin: 8
                    text: (card.info.dimsTitle || "").toUpperCase()
                    color: Theme.ink3
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                    font.letterSpacing: 0.6
                }
                Repeater {
                    model: card.supported ? (card.info.dims || []) : []
                    ColumnLayout {
                        id: dimRow
                        required property var modelData
                        required property int index
                        Layout.fillWidth: true
                        Layout.topMargin: index > 0 ? 9 : 0
                        spacing: 0
                        readonly property string mode: modelData.mode
                        RowLayout {
                            Layout.fillWidth: true
                            spacing: 8
                            Text {
                                Layout.fillWidth: true
                                text: dimRow.modelData.label
                                color: Theme.ink
                                font.pixelSize: 12
                                font.weight: Font.DemiBold
                                elide: Text.ElideRight
                            }
                            Text {
                                visible: dimRow.mode === "value"
                                textFormat: Text.StyledText
                                text: dimRow.modelData.value + " <font color='" + Theme.ink3 + "'>/ " + dimRow.modelData.max + "</font>"
                                color: Theme.ink
                                font.pixelSize: 12
                                font.weight: Font.DemiBold
                                font.features: Theme.tabular
                            }
                            Text {
                                visible: dimRow.mode === "none"
                                text: "–"
                                color: Theme.ink3
                                font.pixelSize: 12
                            }
                            Text {
                                visible: dimRow.mode === "unverified" || dimRow.mode === "pending"
                                text: dimRow.modelData.note
                                color: dimRow.mode === "pending" ? Theme.ink2 : Theme.ink3
                                font.pixelSize: 11
                                font.weight: Font.Medium
                            }
                        }
                        Item {
                            Layout.fillWidth: true
                            Layout.topMargin: 5
                            implicitHeight: 4
                            Rectangle {
                                anchors.fill: parent
                                radius: 2
                                color: dimRow.mode === "value" || dimRow.mode === "none" ? Theme.track : "transparent"
                            }
                            ProofDots {
                                visible: dimRow.mode === "unverified" || (dimRow.mode === "pending")
                                anchors.verticalCenter: parent.verticalCenter
                                textWidth: dimRow.mode === "pending" ? parent.width * dimRow.modelData.ghost : parent.width
                                pixelSize: 30
                                color: dimRow.mode === "pending" ? Theme.scoreInk : Theme.ink3
                                shown: true
                            }
                            Rectangle {
                                visible: dimRow.mode === "value" && dimRow.modelData.value > 0
                                width: parent.width * Math.min(1, dimRow.modelData.value / dimRow.modelData.max)
                                height: 4
                                radius: 2
                                color: Theme.scoreInk
                            }
                        }
                        Text {
                            visible: !card.dropDesc
                            Layout.fillWidth: true
                            Layout.topMargin: 4
                            text: dimRow.modelData.desc
                            color: Theme.caption
                            font.pixelSize: 11
                            lineHeight: 1.2
                            wrapMode: Text.WordWrap
                        }
                    }
                }

                // 4. 預測, only while the agent is live with a score.
                Text {
                    visible: card.supported && (card.info.predict || []).length > 0
                    Layout.topMargin: 14
                    Layout.bottomMargin: 8
                    text: (card.info.predictTitle || "").toUpperCase()
                    color: Theme.ink3
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                    font.letterSpacing: 0.6
                }
                RowLayout {
                    visible: card.supported && (card.info.predict || []).length > 0
                    Layout.fillWidth: true
                    spacing: 8
                    Repeater {
                        model: card.supported ? (card.info.predict || []) : []
                        Rectangle {
                            required property var modelData
                            Layout.fillWidth: true
                            Layout.preferredWidth: 1
                            implicitHeight: cellColumn.implicitHeight + 16
                            radius: 8
                            color: Theme.cellFill
                            Column {
                                id: cellColumn
                                x: 9
                                y: 8
                                width: parent.width - 16
                                spacing: 2
                                Text {
                                    text: parent.parent.modelData.pct
                                    color: Theme.ink
                                    font.pixelSize: 17
                                    font.weight: Font.DemiBold
                                    font.features: Theme.tabular
                                }
                                Text {
                                    width: parent.width
                                    text: parent.parent.modelData.label
                                    color: Theme.ink2
                                    font.pixelSize: 11
                                    elide: Text.ElideRight
                                }
                                Text {
                                    width: parent.width
                                    text: parent.parent.modelData.flag
                                    color: Theme.ink3
                                    font.pixelSize: 10
                                    font.features: Theme.tabular
                                    elide: Text.ElideRight
                                }
                            }
                        }
                    }
                }
                Text {
                    visible: card.supported && (card.info.predict || []).length > 0
                    Layout.fillWidth: true
                    Layout.topMargin: 7
                    text: card.info.predictNote || ""
                    color: Theme.caption
                    font.pixelSize: 11
                    lineHeight: 1.2
                    wrapMode: Text.WordWrap
                }

                // 5. 行為.
                Text {
                    visible: card.supported && (card.info.signals || []).length > 0
                    Layout.topMargin: 14
                    Layout.bottomMargin: 8
                    text: (card.info.signalsTitle || "").toUpperCase()
                    color: Theme.ink3
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                    font.letterSpacing: 0.6
                }
                Repeater {
                    model: card.supported ? (card.info.signals || []) : []
                    RowLayout {
                        required property var modelData
                        required property int index
                        Layout.fillWidth: true
                        Layout.topMargin: index > 0 ? 6 : 0
                        spacing: 7
                        Glyph {
                            Layout.alignment: Qt.AlignTop
                            Layout.topMargin: 2
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

                // 6. Facts and 7. the footnote.
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
                    visible: card.supported && !card.dropFootnote
                    Layout.fillWidth: true
                    Layout.topMargin: 10
                    text: card.info.footnote || ""
                    color: Theme.caption
                    font.pixelSize: 11
                    lineHeight: 1.25
                    wrapMode: Text.WordWrap
                }
            }
        }
    }
}
