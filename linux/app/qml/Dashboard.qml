import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import QtQuick.Window

Window {
    id: root
    width: 440
    height: 680
    minimumWidth: 360
    minimumHeight: 480
    title: S.t("app.name")
    color: pal.window

    SystemPalette { id: pal; colorGroup: SystemPalette.Active }

    readonly property var snap: appState.snapshot
    readonly property bool dark: pal.window.hslLightness < 0.5
    readonly property color ink: pal.windowText
    readonly property color ink2: Qt.rgba(ink.r, ink.g, ink.b, 0.64)
    readonly property color ink3: Qt.rgba(ink.r, ink.g, ink.b, 0.44)
    readonly property color card: dark ? Qt.lighter(pal.window, 1.22) : Qt.darker(pal.window, 1.04)
    readonly property color hairline: Qt.rgba(ink.r, ink.g, ink.b, 0.09)
    readonly property string unitText: S.t("unit.tokS")

    function fmt(text, args) {
        var i = 0
        return text.replace(/%[d@]/g, function() { return String(args[i++]) })
    }

    // DESIGN source colors.
    function sourceColor(id) {
        var colors = {
            codex: "#34D399", claudeCode: "#FF8A65", claudeDesktop: "#B39DFF", deepseek: "#4D6BFE",
            opencode: "#F4B13E", gemini: "#34B8F0", omp: "#84CC16", antigravity: "#EC4899",
            workbuddy: "#11A68B", workbuddyAi: "#007AB4", zcode: "#466BFF", kimi: "#E16F8A",
            grokBuild: "#8290A6", pi: "#A855F7", openclaw: "#DE2E1F", hermes: "#B8860B", droid: "#9E5F46"
        }
        return colors[id] || "#7C8798"
    }

    function phaseColor(phase) {
        if (phase === "generating") return "#22C55E"
        if (phase === "tools") return "#F59E0B"
        return ink3
    }

    ScrollView {
        id: scroll
        anchors.fill: parent
        contentWidth: availableWidth
        clip: true

        ColumnLayout {
            width: scroll.availableWidth
            spacing: 18

            Item { Layout.preferredHeight: 2 }

            RowLayout {
                Layout.leftMargin: 22
                Layout.rightMargin: 22
                spacing: 10
                Text {
                    text: S.t("app.name")
                    color: root.ink
                    font.pixelSize: 15
                    font.weight: Font.DemiBold
                }
                Rectangle {
                    visible: snap.demo === true
                    radius: 9
                    color: Qt.rgba(0.96, 0.62, 0.04, 0.16)
                    implicitHeight: 20
                    implicitWidth: demoLabel.implicitWidth + 16
                    Text {
                        id: demoLabel
                        anchors.centerIn: parent
                        text: uiLanguage.startsWith("zh") ? "示範模式" : uiLanguage === "ja" ? "デモモード" : "Demo"
                        color: dark ? "#FBBF24" : "#B45309"
                        font.pixelSize: 11
                        font.weight: Font.DemiBold
                    }
                }
                Item { Layout.fillWidth: true }
            }

            Rectangle {
                Layout.fillWidth: true
                Layout.leftMargin: 18
                Layout.rightMargin: 18
                implicitHeight: hero.implicitHeight + 40
                radius: 20
                color: root.card

                ColumnLayout {
                    id: hero
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.verticalCenter: parent.verticalCenter
                    anchors.leftMargin: 22
                    anchors.rightMargin: 22
                    spacing: 6

                    RowLayout {
                        spacing: 8
                        Rectangle {
                            id: liveDot
                            width: 8
                            height: 8
                            radius: 4
                            color: (snap.generating || 0) > 0 ? "#22C55E" : root.ink3
                            SequentialAnimation on opacity {
                                running: (snap.generating || 0) > 0
                                loops: Animation.Infinite
                                NumberAnimation { to: 0.35; duration: 700; easing.type: Easing.InOutSine }
                                NumberAnimation { to: 1.0; duration: 700; easing.type: Easing.InOutSine }
                            }
                        }
                        Text {
                            text: S.t("dashboard.live")
                            color: root.ink2
                            font.pixelSize: 13
                        }
                    }
                    RowLayout {
                        spacing: 8
                        Text {
                            id: liveValue
                            text: snap.liveText || "0"
                            color: root.ink
                            font.pixelSize: 64
                            font.weight: Font.DemiBold
                            font.features: { "tnum": 1 }
                        }
                        Text {
                            text: root.unitText
                            color: root.ink3
                            font.pixelSize: 17
                            Layout.alignment: Qt.AlignBaseline
                        }
                    }
                    Text {
                        text: fmt(S.t("agents.context"), [snap.generating || 0, snap.tools || 0])
                        color: root.ink2
                        font.pixelSize: 13
                    }
                }
            }

            ColumnLayout {
                Layout.leftMargin: 18
                Layout.rightMargin: 18
                spacing: 10
                Text {
                    text: S.t("dashboard.today")
                    color: root.ink2
                    font.pixelSize: 13
                    font.weight: Font.DemiBold
                    Layout.leftMargin: 4
                }
                GridLayout {
                    Layout.fillWidth: true
                    columns: 2
                    columnSpacing: 10
                    rowSpacing: 10
                    StatTile { label: S.t("today.output"); value: snap.todayOutputText || "0"; color: root.card; ink: root.ink; ink2: root.ink2; ink3: root.ink3 }
                    StatTile { label: S.t("today.requests"); value: snap.todayRequestsText || "0"; color: root.card; ink: root.ink; ink2: root.ink2; ink3: root.ink3 }
                    StatTile { label: S.t("today.average"); value: snap.todayAverageText || "–"; unit: root.unitText; color: root.card; ink: root.ink; ink2: root.ink2; ink3: root.ink3 }
                    StatTile { label: S.t("today.peak"); value: snap.peakText || "0"; unit: root.unitText; color: root.card; ink: root.ink; ink2: root.ink2; ink3: root.ink3 }
                }
            }

            ColumnLayout {
                Layout.leftMargin: 18
                Layout.rightMargin: 18
                spacing: 8
                visible: (snap.sources || []).length > 0
                Text {
                    text: S.t("dashboard.sources")
                    color: root.ink2
                    font.pixelSize: 13
                    font.weight: Font.DemiBold
                    Layout.leftMargin: 4
                }
                Rectangle {
                    Layout.fillWidth: true
                    radius: 14
                    color: root.card
                    implicitHeight: sourceColumn.implicitHeight + 12
                    Column {
                        id: sourceColumn
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.top: parent.top
                        anchors.topMargin: 6
                        Repeater {
                            model: snap.sources || []
                            delegate: Item {
                                width: sourceColumn.width
                                height: 40
                                Rectangle {
                                    id: sourceDot
                                    width: 9
                                    height: 9
                                    radius: 4.5
                                    color: root.sourceColor(modelData.source)
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.left: parent.left
                                    anchors.leftMargin: 14
                                }
                                Text {
                                    text: modelData.name
                                    color: root.ink
                                    font.pixelSize: 14
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.left: sourceDot.right
                                    anchors.leftMargin: 10
                                }
                                Text {
                                    text: modelData.todayText
                                    color: root.ink3
                                    font.pixelSize: 12
                                    font.features: { "tnum": 1 }
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.right: sourceLive.left
                                    anchors.rightMargin: 14
                                }
                                Text {
                                    id: sourceLive
                                    text: modelData.liveText + " " + root.unitText
                                    color: root.ink
                                    font.pixelSize: 14
                                    font.weight: Font.Medium
                                    font.features: { "tnum": 1 }
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.right: parent.right
                                    anchors.rightMargin: 14
                                }
                            }
                        }
                    }
                }
            }

            ColumnLayout {
                Layout.leftMargin: 18
                Layout.rightMargin: 18
                spacing: 8
                Text {
                    text: S.t("dashboard.agents")
                    color: root.ink2
                    font.pixelSize: 13
                    font.weight: Font.DemiBold
                    Layout.leftMargin: 4
                }
                Rectangle {
                    Layout.fillWidth: true
                    radius: 14
                    color: root.card
                    implicitHeight: (snap.agents || []).length > 0 ? agentColumn.implicitHeight + 12 : 64
                    Text {
                        visible: (snap.agents || []).length === 0
                        anchors.centerIn: parent
                        text: S.t("agents.empty")
                        color: root.ink3
                        font.pixelSize: 13
                    }
                    Column {
                        id: agentColumn
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.top: parent.top
                        anchors.topMargin: 6
                        Repeater {
                            model: snap.agents || []
                            delegate: Item {
                                width: agentColumn.width
                                height: 50
                                readonly property real indent: 14 + modelData.depth * 18
                                Rectangle {
                                    visible: index > 0
                                    height: 1
                                    color: root.hairline
                                    anchors.top: parent.top
                                    anchors.left: parent.left
                                    anchors.right: parent.right
                                    anchors.leftMargin: 14
                                    anchors.rightMargin: 14
                                }
                                Rectangle {
                                    id: phaseDot
                                    width: 8
                                    height: 8
                                    radius: 4
                                    color: root.phaseColor(modelData.phase)
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.left: parent.left
                                    anchors.leftMargin: parent.indent
                                    SequentialAnimation on opacity {
                                        running: modelData.phase === "generating"
                                        loops: Animation.Infinite
                                        NumberAnimation { to: 0.35; duration: 650; easing.type: Easing.InOutSine }
                                        NumberAnimation { to: 1.0; duration: 650; easing.type: Easing.InOutSine }
                                    }
                                }
                                Column {
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.left: phaseDot.right
                                    anchors.leftMargin: 10
                                    anchors.right: agentLive.left
                                    anchors.rightMargin: 12
                                    spacing: 2
                                    Text {
                                        width: parent.width
                                        text: modelData.project.length > 0 ? modelData.project : modelData.sourceName
                                        color: root.ink
                                        font.pixelSize: 14
                                        font.weight: modelData.depth > 0 ? Font.Normal : Font.DemiBold
                                        elide: Text.ElideRight
                                    }
                                    Row {
                                        spacing: 6
                                        Rectangle {
                                            width: 6
                                            height: 6
                                            radius: 3
                                            color: root.sourceColor(modelData.source)
                                            anchors.verticalCenter: parent.verticalCenter
                                        }
                                        Text {
                                            text: modelData.model.length > 0 ? modelData.model : modelData.sourceName
                                            color: root.ink3
                                            font.pixelSize: 12
                                            elide: Text.ElideRight
                                        }
                                    }
                                }
                                Text {
                                    id: agentLive
                                    text: modelData.phase === "idle" && modelData.live < 0.05 ? S.t("phase.idle")
                                          : modelData.liveText + " " + root.unitText
                                    color: modelData.phase === "idle" ? root.ink3 : root.ink
                                    font.pixelSize: 14
                                    font.weight: Font.Medium
                                    font.features: { "tnum": 1 }
                                    anchors.verticalCenter: parent.verticalCenter
                                    anchors.right: parent.right
                                    anchors.rightMargin: 14
                                }
                            }
                        }
                    }
                }
            }

            Item { Layout.preferredHeight: 10 }
        }
    }
}
