import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtCore
import "Format.js" as Format
import "History.js" as H
import "Heat.js" as Heat

// DESIGN 4b, 3b: effort and speed (推理強度與加速) over the window's range — one row per value with its glyph, a
// share-of-output bar, requests, output and average speed; the unknown bucket last so the rows add up to the range.
Rectangle {
    id: section
    property string first: ""
    property string last: ""
    property string source: "all"

    Settings {
        id: remembered
        category: "history"
        property string modeKind: "effort"
    }

    readonly property string kind: remembered.modeKind === "speed" ? "speed" : "effort"
    readonly property var view: history.detailRevision >= 0 && S.rev >= 0 && first.length > 0 ? history.modeSection(first, last, source, kind) : ({ rows: [], known: false })
    readonly property var rows: view.rows || []
    readonly property real requestsWidth: 70
    readonly property real outputWidth: 70
    readonly property real speedWidth: 84

    implicitHeight: content.implicitHeight + 32
    radius: 12
    color: Theme.card
    border.width: 1
    border.color: Theme.border
    Accessible.role: Accessible.Grouping
    Accessible.name: S.tr2(S.rev, "modes.history.title")

    function label(row) {
        if (row.unknown) return S.tr2(S.rev, "modes.unrecorded")
        if (kind === "speed") {
            var key = "speed.value." + row.mode
            var text = S.tr2(S.rev, key)
            return text === key ? row.mode : text
        }
        return row.mode.toLowerCase()
    }
    function barColor(row) {
        if (row.unknown) return Qt.rgba(Theme.ink.r, Theme.ink.g, Theme.ink.b, 0.12)
        if (kind === "speed") return row.bolts > 0 ? Theme.bolt : Theme.ink3
        return row.level >= 5 && !row.top ? Heat.color(0.82) : Theme.ink3
    }

    ColumnLayout {
        id: content
        x: 18
        y: 16
        width: section.width - 36
        spacing: 0

        RowLayout {
            Layout.fillWidth: true
            spacing: 10
            Text {
                text: S.tr2(S.rev, "modes.history.title")
                color: Theme.ink
                font.pixelSize: 15
                font.weight: Font.DemiBold
                Accessible.role: Accessible.Heading
            }
            Segmented {
                value: section.kind
                options: [
                    { value: "effort", label: S.tr2(S.rev, "effort.label") },
                    { value: "speed", label: S.tr2(S.rev, "speed.label") }
                ]
                onActivated: (v) => remembered.modeKind = v
            }
            Item { Layout.fillWidth: true }
            PillButton {
                text: S.tr2(S.rev, "mode.exportCsv")
                enabled: section.rows.some(function(r) { return r.requests > 0 || r.output > 0 })
                onClicked: {
                    var codes = { claudeCode: "claude-code", claudeDesktop: "claude-desktop", workbuddyAi: "workbuddy-ai" }
                    var code = section.source === "all" ? "all" : (codes[section.source] || section.source)
                    var path = history.chooseCsvPath("agent-speed-modes-" + code + "-" + section.first + "-" + section.last + ".csv")
                    if (path.length > 0) history.exportModeCsv(path, section.first, section.last, section.source)
                }
            }
        }

        // Column captions over the numbers.
        RowLayout {
            Layout.fillWidth: true
            Layout.topMargin: 12
            spacing: 0
            Item { Layout.fillWidth: true }
            Repeater {
                model: [
                    { text: S.tr2(S.rev, "history.table.requests"), width: section.requestsWidth },
                    { text: S.tr2(S.rev, "history.table.output"), width: section.outputWidth },
                    { text: S.tr2(S.rev, "history.table.speed"), width: section.speedWidth }
                ]
                Text {
                    required property var modelData
                    Layout.preferredWidth: modelData.width
                    horizontalAlignment: Text.AlignRight
                    text: modelData.text
                    color: Theme.ink3
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                    elide: Text.ElideRight
                }
            }
        }

        Repeater {
            model: section.rows
            Item {
                id: line
                required property var modelData
                required property int index
                Layout.fillWidth: true
                Layout.preferredHeight: 32
                Accessible.role: Accessible.Row
                Accessible.name: section.label(modelData) + ", " + Math.round(modelData.share * 100) + "%, "
                                 + H.grouped(modelData.requests) + " " + S.tr2(S.rev, "history.table.requests")

                Rectangle {
                    anchors.fill: parent
                    anchors.leftMargin: -8
                    anchors.rightMargin: -8
                    radius: 6
                    color: rowHover.hovered ? Theme.hover : "transparent"
                    Behavior on color { ColorAnimation { duration: 140 } }
                }
                HoverHandler { id: rowHover }

                RowLayout {
                    anchors.fill: parent
                    spacing: 0

                    // Glyph: the effort meter, the speed bolts, or a hollow ring for standard and bolt-less values.
                    Item {
                        Layout.preferredWidth: 26
                        Layout.preferredHeight: 12
                        Item {
                            visible: section.kind === "effort" && line.modelData.level >= 0
                            width: 18.5
                            height: 10
                            anchors.verticalCenter: parent.verticalCenter
                            Repeater {
                                model: 5
                                Rectangle {
                                    required property int index
                                    readonly property bool lit: index < line.modelData.level
                                    x: index * 4
                                    width: 2.5
                                    height: [4, 5.5, 7, 8.5, 10][index]
                                    y: 10 - height
                                    radius: 0.75
                                    color: !lit ? Theme.meterOff : line.modelData.top ? Heat.color(0.72 + 0.07 * index)
                                                                 : line.modelData.level >= 5 ? Heat.color(0.82) : Theme.ink2
                                }
                            }
                        }
                        Item {
                            visible: section.kind === "speed" && line.modelData.bolts > 0
                            width: 15
                            height: 10
                            anchors.verticalCenter: parent.verticalCenter
                            Glyph { visible: line.modelData.bolts > 1; width: 9; height: 10; name: "bolt"; color: Theme.bolt; opacity: 0.6 }
                            Glyph { x: line.modelData.bolts > 1 ? 6 : 0; width: 9; height: 10; name: "bolt"; color: Theme.bolt }
                        }
                        Rectangle {
                            visible: section.kind === "speed" && !line.modelData.unknown && line.modelData.bolts === 0
                            x: 1
                            width: 6
                            height: 6
                            radius: 3
                            anchors.verticalCenter: parent.verticalCenter
                            color: "transparent"
                            border.width: 1
                            border.color: Theme.ink3
                        }
                    }
                    Text {
                        Layout.preferredWidth: 96
                        text: section.label(line.modelData)
                        color: line.modelData.unknown ? Theme.ink3 : line.modelData.top || line.modelData.level >= 5 ? Heat.color(0.82) : Theme.ink
                        font.pixelSize: 12
                        font.weight: line.modelData.unknown ? Font.Normal : Font.Medium
                        elide: Text.ElideRight
                    }
                    // Share of the range's output.
                    Item {
                        Layout.fillWidth: true
                        Layout.preferredHeight: 8
                        Layout.rightMargin: 14
                        Rectangle {
                            anchors.fill: parent
                            radius: 4
                            color: Theme.track
                        }
                        Rectangle {
                            width: Math.max(line.modelData.share > 0 ? 3 : 0, parent.width * line.modelData.share)
                            height: parent.height
                            radius: 4
                            color: section.barColor(line.modelData)
                            Behavior on width { SpringAnimation { spring: 3.2; damping: 0.32; epsilon: 0.25 } }
                            // The top rung (ultra, ultracode) fills with the heat gradient.
                            Rectangle {
                                visible: section.kind === "effort" && line.modelData.top
                                anchors.fill: parent
                                radius: parent.radius
                                gradient: Gradient {
                                    orientation: Gradient.Horizontal
                                    GradientStop { position: 0; color: Heat.color(0.72) }
                                    GradientStop { position: 1; color: Heat.color(1) }
                                }
                            }
                        }
                    }
                    Text {
                        Layout.preferredWidth: section.requestsWidth
                        horizontalAlignment: Text.AlignRight
                        text: H.grouped(line.modelData.requests)
                        color: Theme.ink
                        font.pixelSize: 12
                        font.features: Theme.tabular
                    }
                    Text {
                        Layout.preferredWidth: section.outputWidth
                        horizontalAlignment: Text.AlignRight
                        text: H.compact(line.modelData.output)
                        color: Theme.ink
                        font.pixelSize: 12
                        font.features: Theme.tabular
                    }
                    Text {
                        Layout.preferredWidth: section.speedWidth
                        horizontalAlignment: Text.AlignRight
                        text: line.modelData.speed >= 0 ? Format.speed(line.modelData.speed) + " tok/s" : S.tr2(S.rev, "history.noSpeed")
                        color: line.modelData.speed >= 0 ? Theme.ink : Theme.ink3
                        font.pixelSize: 12
                        font.features: Theme.tabular
                    }
                }

                // Hover: the row's top three models with share bars.
                ToolTip {
                    visible: rowHover.hovered && line.modelData.models.length > 0
                    delay: 450
                    padding: 10
                    background: Rectangle { radius: 8; color: Theme.cardFill; border.width: 1; border.color: Theme.cardLine }
                    contentItem: Column {
                        spacing: 6
                        Repeater {
                            model: line.modelData.models
                            Row {
                                required property var modelData
                                spacing: 8
                                Text {
                                    width: 170
                                    text: modelData.name.length > 0 ? modelData.name : S.tr2(S.rev, "cost.noModelName")
                                    color: Theme.ink
                                    font.pixelSize: 11
                                    elide: Text.ElideMiddle
                                    anchors.verticalCenter: parent.verticalCenter
                                }
                                Rectangle {
                                    width: 60
                                    height: 5
                                    radius: 2.5
                                    color: Theme.track
                                    anchors.verticalCenter: parent.verticalCenter
                                    Rectangle { width: parent.width * modelData.share; height: parent.height; radius: 2.5; color: Theme.ink2 }
                                }
                                Text {
                                    width: 34
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
        }

        Text {
            visible: !section.view.known
            Layout.topMargin: 6
            text: S.tr2(S.rev, "modes.history.empty")
            color: Theme.ink3
            font.pixelSize: 12
        }
    }
}
