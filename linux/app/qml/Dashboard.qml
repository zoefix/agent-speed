import QtQuick
import QtQuick.Controls
import QtQuick.Effects
import QtQuick.Layouts
import QtCore
import "Format.js" as Format
import "Heat.js" as Heat
import "Sync.js" as Sync

// DESIGN 4: the dashboard, a 368-wide flyout beside the tray (or a plain window where there is no tray).
Window {
    id: root
    property bool flyout: true
    readonly property int margin: flyout ? 24 : 0
    // Set by the flyout from the work area; taller content scrolls inside the panel.
    property int maxPanelHeight: 100000
    readonly property int wantedHeight: Math.ceil(frame.height) + margin * 2
    readonly property var snap: appState.snapshot
    readonly property int collapsedRows: 6

    width: 368 + margin * 2
    color: flyout ? "transparent" : Theme.panel
    title: S.tr2(S.rev, "app.name")
    flags: flyout ? (Qt.Tool | Qt.FramelessWindowHint | Qt.WindowStaysOnTopHint | Qt.NoDropShadowWindowHint) : Qt.Window

    signal dismissed()
    signal settingsRequested()
    signal historyRequested()
    readonly property bool moneyLive: prefs.liveUnit === "cost"
    readonly property bool bothUnits: prefs.liveUnit === "both"

    Settings {
        id: prefs
        category: "dashboard"
        property bool agentsExpanded: false
    }

    ListModel { id: sourceModel }
    ListModel { id: agentModel }

    function applySnapshot() {
        var s = appState.snapshot
        var sources = []
        var rawSources = s.sources || []
        for (var i = 0; i < rawSources.length; ++i) {
            var src = rawSources[i]
            if (!src.enabled || !(src.live > 0 || src.today > 0)) continue
            sources.push({ key: src.source, source: src.source, name: src.name, todayText: src.todayText, live: src.live })
        }
        Sync.sync(sourceModel, sources, "key")

        var raw = s.agents || []
        var shown = prefs.agentsExpanded ? raw : raw.slice(0, collapsedRows)
        var facts = Sync.tree(shown)
        var agents = []
        for (var j = 0; j < shown.length; ++j) {
            var a = shown[j]
            var detail = a.model ? a.model + " · " + a.sourceName : a.sourceName
            agents.push({
                key: a.id, source: a.source, project: a.project || a.sourceName, detail: detail, phase: a.phase,
                live: a.live, depth: a.depth, continues: facts[j].continues, hasChildren: facts[j].hasChildren,
                rails: facts[j].rails, effort: a.effort || "", effortLevel: a.effortLevel, effortTop: a.effortTop,
                bolts: a.bolts, iq: a.iq, iqForm: a.iqForm, iqProof: a.iqProof, looping: a.looping
            })
        }
        Sync.sync(agentModel, agents, "key")
        // The capsule column is as wide as the widest capsule among the visible rows (DESIGN 4).
        var widest = 0, deepest = 0
        for (var c = 0; c < shown.length; ++c) {
            widest = Math.max(widest, capsuleWidth(shown[c], true))
            deepest = Math.max(deepest, shown[c].depth)
        }
        // The name block keeps 72 px at the deepest indent, or every capsule drops its word.
        var room = 328 - deepest * 12 - 16 - 10 - 10 - iqCellWidth - 6 - 52 - (widest > 0 ? 8 : 0)
        capsuleWords = room - widest >= 72
        if (!capsuleWords) {
            widest = 0
            for (var w = 0; w < shown.length; ++w) widest = Math.max(widest, capsuleWidth(shown[w], false))
        }
        capsuleColumn = widest
        if (scoreCard.visible) {
            var info = cardInfo(cardKey)
            if (info) scoreCard.info = info
            else hideCard()
        }
    }

    property real capsuleColumn: 0
    property bool capsuleWords: true
    FontMetrics { id: capsuleMetrics; font.pixelSize: 11; font.weight: Font.Medium }
    FontMetrics { id: iqMetrics; font.pixelSize: 12; font.weight: Font.Medium }
    FontMetrics { id: captionMetrics; font.pixelSize: 11; font.weight: Font.Medium }
    readonly property real iqCellWidth: Math.ceil(Math.max(iqMetrics.advanceWidth("888"), captionMetrics.advanceWidth(S.tr2(S.rev, "intel.glyph"))))

    function capsuleWidth(a, withWord) {
        var parts = []
        if (a.effortLevel >= 0) parts.push(18.5)
        if (withWord && a.effort) parts.push(capsuleMetrics.advanceWidth(a.effort.toLowerCase()))
        if (a.bolts > 0) parts.push(a.bolts > 1 ? 15 : 9)
        if (parts.length === 0) return 0
        var sum = 12 + 5 * (parts.length - 1)
        for (var i = 0; i < parts.length; ++i) sum += parts[i]
        return Math.ceil(sum)
    }

    // The score card (DESIGN 12): 0.45 s of hover shows it beside the panel; another row switches at once; it hides
    // 0.15 s after the pointer leaves.
    property string cardKey: ""
    property string hoverKey: ""
    property Item hoverItem: null
    function cardInfo(key) {
        var rows = (appState.snapshot.agents || [])
        for (var i = 0; i < rows.length; ++i) {
            if (rows[i].id === key) return rows[i].card
        }
        return null
    }
    function rowHover(key, item, hovered) {
        if (hovered) {
            hoverKey = key
            hoverItem = item
            cardHide.stop()
            if (scoreCard.visible) openCard()
            else cardShow.restart()
        } else if (hoverKey === key) {
            hoverKey = ""
            cardShow.stop()
            if (scoreCard.visible) cardHide.restart()
        }
    }
    function openCard() {
        var info = cardInfo(hoverKey)
        if (!info || !hoverItem) return
        const changed = cardKey !== hoverKey
        cardKey = hoverKey
        scoreCard.info = info
        var top = hoverItem.mapToItem(null, 0, 0).y
        var scr = root.screen
        scoreCard.availableHeight = scr ? scr.desktopAvailableHeight : 10000
        var towardLeft = !scr || (root.x + root.width / 2 > scr.virtualX + scr.width / 2)
        scoreCard.x = towardLeft ? root.x + frame.x - 8 - 300 - scoreCard.pad : root.x + frame.x + frame.width + 8 - scoreCard.pad
        var y = root.y + top - scoreCard.pad
        if (scr) {
            var minY = scr.virtualY + 8 - scoreCard.pad
            var maxY = scr.virtualY + scr.desktopAvailableHeight - 8 - scoreCard.height + scoreCard.pad
            y = Math.max(minY, Math.min(y, maxY))
        }
        scoreCard.y = y
        scoreCard.visible = true
        if (changed) scoreCard.fit()
    }
    function hideCard() {
        cardShow.stop()
        cardHide.stop()
        scoreCard.visible = false
        cardKey = ""
    }
    Timer { id: cardShow; interval: 450; onTriggered: root.openCard() }
    Timer { id: cardHide; interval: 150; onTriggered: root.hideCard() }
    onVisibleChanged: if (!visible) hideCard()
    ScoreCard { id: scoreCard; visible: false }
    property alias card: scoreCard
    // Development aid (--card-snapshot): the card of the row at `index` without hovering.
    function showCardFor(index) {
        var rows = appState.snapshot.agents || []
        if (index >= rows.length) return
        hoverKey = rows[index].id
        hoverItem = agentList.itemAtIndex(index)
        openCard()
    }

    Connections {
        target: appState
        function onSnapshotChanged() { root.applySnapshot() }
    }
    Component.onCompleted: applySnapshot()

    Shortcut {
        sequence: "Escape"
        onActivated: root.dismissed()
    }

    Item {
        id: frame
        x: root.margin
        y: root.margin
        width: 368
        height: Math.min(column.implicitHeight + 40, root.maxPanelHeight)

        // The shadow is drawn from a hidden copy under the panel, so the panel still shows where effects cannot run.
        Rectangle {
            id: shadowCaster
            anchors.fill: parent
            radius: 14
            color: "black"
            visible: false
        }
        // Without a GPU the effect cannot run: a few faint stacked outlines stand in for the blur.
        Repeater {
            model: root.flyout && softwareRendering ? 7 : 0
            Rectangle {
                required property int index
                x: -(index + 1) * 2
                y: -(index + 1) * 2 + 4
                width: frame.width + (index + 1) * 4
                height: frame.height + (index + 1) * 4
                radius: 14 + (index + 1) * 2
                color: "transparent"
                border.width: 2
                border.color: Qt.rgba(0, 0, 0, (Theme.dark ? 0.09 : 0.035) * (1 - index / 7))
            }
        }
        MultiEffect {
            anchors.fill: shadowCaster
            source: shadowCaster
            visible: root.flyout && !softwareRendering
            shadowEnabled: true
            shadowColor: "#000000"
            shadowOpacity: Theme.dark ? 0.5 : 0.18
            shadowBlur: 1.0
            blurMax: 20
            shadowVerticalOffset: 5
        }
        Rectangle {
            anchors.fill: parent
            radius: root.flyout ? 14 : 0
            color: Theme.panel
            border.width: root.flyout ? 1 : 0
            border.color: Theme.border
        }

        Flickable {
            id: scroller
            anchors.fill: parent
            anchors.margins: 1
            contentWidth: width
            contentHeight: column.implicitHeight + 38
            interactive: contentHeight > height
            boundsBehavior: Flickable.StopAtBounds
            clip: interactive
            ScrollBar.vertical: ScrollBar {
                id: panelBar
                policy: scroller.interactive ? ScrollBar.AlwaysOn : ScrollBar.AlwaysOff
                padding: 3
                background: null
                contentItem: Rectangle {
                    implicitWidth: panelBar.hovered || panelBar.pressed ? 6 : 4
                    radius: width / 2
                    color: Theme.ink3
                    opacity: panelBar.hovered || panelBar.pressed || scroller.moving ? 0.9 : 0.45
                    Behavior on opacity { NumberAnimation { duration: 150 } }
                }
            }

            ColumnLayout {
                id: column
                x: 19
                y: 19
                width: 328
                spacing: 0

                // Hero
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 18

                    Gauge {
                        heat: root.snap.heat || 0
                        Layout.preferredWidth: 112
                        Layout.preferredHeight: 112
                    }

                    ColumnLayout {
                        Layout.fillWidth: true
                        Layout.alignment: Qt.AlignVCenter
                        spacing: 4

                        Item {
                            id: valueRow
                            property real shown: root.snap.liveOutput || 0
                            Layout.fillWidth: true
                            implicitHeight: bigValue.implicitHeight
                            Text {
                                id: bigValue
                                text: root.moneyLive ? (root.snap.costLiveNumber || "0") : Format.speed(valueRow.shown)
                                color: Theme.ink
                                font.pixelSize: 44
                                font.weight: Font.Bold
                                font.features: Theme.tabular
                                font.letterSpacing: -0.5
                            }
                            Text {
                                anchors.left: bigValue.right
                                anchors.leftMargin: 6
                                anchors.baseline: bigValue.baseline
                                text: root.moneyLive ? (root.snap.costSymbol || "$") + "/s" : S.tr2(S.rev, "unit.tokS")
                                color: Theme.ink2
                                font.pixelSize: 14
                                font.weight: Font.Medium
                            }
                        }
                        Text {
                            visible: root.bothUnits
                            Layout.fillWidth: true
                            text: (root.snap.costLiveText || "") + " · " + S.tr2(S.rev, "cost.apiEquivalent")
                            color: Theme.ink2
                            font.pixelSize: 13
                            font.features: Theme.tabular
                            elide: Text.ElideRight
                        }
                        Text {
                            Layout.fillWidth: true
                            text: Format.fill(S.tr2(S.rev, "agents.context"), [root.snap.generating || 0, root.snap.tools || 0])
                            color: Theme.ink2
                            font.pixelSize: 13
                            font.features: Theme.tabular
                            elide: Text.ElideRight
                        }
                        Text {
                            Layout.fillWidth: true
                            text: Format.fill(S.tr2(S.rev, "today.summary"), [root.snap.todayOutputText || "0", (root.snap.agents || []).length])
                            color: Theme.ink3
                            font.pixelSize: 12
                            font.features: Theme.tabular
                            elide: Text.ElideRight
                        }
                    }
                }

                Hairline {}

                // Live chart
                SectionHeader {
                    text: S.tr2(S.rev, "dashboard.live")
                    trailing: (root.snap.estimatedShare || 0) > 0 ? S.tr2(S.rev, "chart.estimated") : ""
                }
                LiveChart {
                    Layout.fillWidth: true
                    Layout.preferredHeight: 96
                    values: root.snap.history || []
                    peak: root.snap.peak || 0
                    peakCaption: S.tr2(S.rev, "today.peak")
                    unit: S.tr2(S.rev, "unit.tokS")
                }

                Hairline {}

                // Sources
                SectionHeader {
                    text: S.tr2(S.rev, "dashboard.sources")
                    trailing: sourceModel.count > 0 ? S.tr2(S.rev, "unit.tokS") : ""
                }
                Text {
                    visible: sourceModel.count === 0
                    Layout.fillWidth: true
                    text: S.tr2(S.rev, "dashboard.sources.empty")
                    color: Theme.ink2
                    font.pixelSize: 13
                    wrapMode: Text.Wrap
                }
                ListView {
                    id: sourceList
                    Layout.fillWidth: true
                    Layout.preferredHeight: contentHeight
                    visible: sourceModel.count > 0
                    interactive: false
                    model: sourceModel
                    delegate: SourceRow { width: sourceList.width }
                    add: Transition { NumberAnimation { property: "opacity"; from: 0; to: 1; duration: 220 } }
                    remove: Transition { NumberAnimation { property: "opacity"; to: 0; duration: 180 } }
                    displaced: Transition { NumberAnimation { property: "y"; duration: 220; easing.type: Easing.OutCubic } }
                }

                Hairline {}

                // Agents
                SectionHeader {
                    text: S.tr2(S.rev, "dashboard.agents")
                    trailing: agentModel.count > 0 ? S.tr2(S.rev, "unit.tokS") : ""
                    secondary: agentModel.count > 0 ? S.tr2(S.rev, "intel.glyph") : ""
                    secondaryTip: S.tr2(S.rev, "agents.intel.hint")
                    // The 智力 caption's right edge sits on the 智力 column's: the live slot (52) and the 6 px gap.
                    secondaryEdge: 58
                }
                RowLayout {
                    visible: agentModel.count === 0
                    Layout.fillWidth: true
                    Layout.topMargin: 2
                    Layout.bottomMargin: 2
                    spacing: 8
                    Glyph { name: "gauge"; color: Theme.ink3; Layout.preferredWidth: 16; Layout.preferredHeight: 16 }
                    Text {
                        Layout.fillWidth: true
                        text: S.tr2(S.rev, "agents.empty")
                        color: Theme.ink2
                        font.pixelSize: 13
                        elide: Text.ElideRight
                    }
                }
                ListView {
                    id: agentList
                    Layout.fillWidth: true
                    Layout.preferredHeight: Math.min(contentHeight, 12 * 40)
                    visible: agentModel.count > 0
                    interactive: contentHeight > height
                    boundsBehavior: Flickable.StopAtBounds
                    clip: interactive
                    model: agentModel
                    ScrollBar.vertical: ScrollBar { policy: agentList.interactive ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff }
                    delegate: AgentRow {
                        width: agentList.width
                        capsuleColumn: root.capsuleColumn
                        capsuleWords: root.capsuleWords
                        iqWidth: root.iqCellWidth
                        onHoverChanged: hovered => root.rowHover(key, this, hovered)
                    }
                    add: Transition { NumberAnimation { property: "opacity"; from: 0; to: 1; duration: 220 } }
                    remove: Transition { NumberAnimation { property: "opacity"; to: 0; duration: 180 } }
                    displaced: Transition { NumberAnimation { property: "y"; duration: 220; easing.type: Easing.OutCubic } }
                }
                Item {
                    id: disclosure
                    readonly property int total: (root.snap.agents || []).length
                    visible: total > root.collapsedRows
                    Layout.fillWidth: true
                    implicitHeight: 30
                    Rectangle {
                        anchors.fill: parent
                        anchors.leftMargin: -6
                        anchors.rightMargin: -6
                        radius: 7
                        color: disclosureArea.containsMouse ? Theme.hover : "transparent"
                    }
                    Row {
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 6
                        Glyph {
                            anchors.verticalCenter: parent.verticalCenter
                            width: 12
                            height: 12
                            name: "chevron"
                            color: Theme.ink2
                            rotation: prefs.agentsExpanded ? 180 : 0
                            Behavior on rotation { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
                        }
                        Text {
                            anchors.verticalCenter: parent.verticalCenter
                            text: prefs.agentsExpanded ? S.tr2(S.rev, "agents.collapse")
                                                       : Format.fill(S.tr2(S.rev, "agents.expand"), [disclosure.total - root.collapsedRows])
                            color: Theme.ink2
                            font.pixelSize: 12
                            font.weight: Font.Medium
                        }
                    }
                    MouseArea {
                        id: disclosureArea
                        anchors.fill: parent
                        hoverEnabled: true
                        cursorShape: Qt.PointingHandCursor
                        onClicked: {
                            prefs.agentsExpanded = !prefs.agentsExpanded
                            root.applySnapshot()
                        }
                    }
                    Accessible.role: Accessible.Button
                    Accessible.name: prefs.agentsExpanded ? S.tr2(S.rev, "agents.collapse")
                                                          : Format.fill(S.tr2(S.rev, "agents.expand.a11y"), [disclosure.total])
                }

                Hairline {}

                // Today
                SectionHeader { text: S.tr2(S.rev, "dashboard.today") }
                GridLayout {
                    Layout.fillWidth: true
                    columns: 2
                    columnSpacing: 16
                    rowSpacing: 12
                    StatCell { caption: S.tr2(S.rev, "today.output"); value: root.snap.todayOutputText || "0" }
                    StatCell { caption: S.tr2(S.rev, "today.input"); value: root.snap.todayInputText || "0" }
                    StatCell { caption: S.tr2(S.rev, "today.requests"); value: root.snap.todayRequestsText || "0" }
                    StatCell {
                        caption: S.tr2(S.rev, "today.average")
                        value: root.snap.todayAverageText || "–"
                        unit: root.snap.todayAverageText && root.snap.todayAverageText !== "–" ? S.tr2(S.rev, "unit.tokS") : ""
                    }
                    StatCell {
                        Layout.columnSpan: 2
                        caption: S.tr2(S.rev, "today.cost") + " · " + S.tr2(S.rev, "cost.apiEquivalent")
                        value: root.snap.costTodayText || "–"
                    }
                }
                RowLayout {
                    Layout.fillWidth: true
                    Layout.topMargin: 12
                    spacing: 8
                    Rectangle {
                        readonly property color tone: Heat.color(Heat.heatOf(root.snap.peak || 0, 2000))
                        Layout.preferredWidth: 8
                        Layout.preferredHeight: 8
                        radius: 4
                        color: tone
                    }
                    Text {
                        text: S.tr2(S.rev, "today.peak")
                        color: Theme.ink2
                        font.pixelSize: 12
                    }
                    Item { Layout.fillWidth: true }
                    Text {
                        text: (root.snap.peakText || "0") + " " + S.tr2(S.rev, "unit.tokS")
                        color: Theme.ink
                        font.pixelSize: 13
                        font.weight: Font.Medium
                        font.features: Theme.tabular
                    }
                }

                Hairline {}

                // Footer
                Text {
                    id: explanation
                    visible: info.checked
                    Layout.fillWidth: true
                    Layout.bottomMargin: 10
                    text: S.tr2(S.rev, "metric.info")
                    color: Theme.ink2
                    font.pixelSize: 12
                    lineHeight: 1.3
                    wrapMode: Text.Wrap
                }
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 6
                    Text {
                        text: S.tr2(S.rev, "app.name")
                        color: Theme.ink3
                        font.pixelSize: 12
                        font.weight: Font.Medium
                    }
                    Rectangle {
                        visible: root.snap.demo === true
                        radius: height / 2
                        color: Qt.rgba(0.96, 0.62, 0.04, Theme.dark ? 0.16 : 0.12)
                        implicitHeight: 18
                        implicitWidth: demoLabel.implicitWidth + 14
                        Text {
                            id: demoLabel
                            anchors.centerIn: parent
                            text: S.language.startsWith("zh") ? (S.language === "zh-Hans" ? "演示模式" : "示範模式") : S.language === "ja" ? "デモモード" : "Demo"
                            color: Theme.amber
                            font.pixelSize: 11
                            font.weight: Font.DemiBold
                        }
                    }
                    Item { Layout.fillWidth: true }
                    IconButton {
                        glyph: "chart"
                        label: S.tr2(S.rev, "history.open")
                        onClicked: {
                            root.dismissed()
                            root.historyRequested()
                        }
                    }
                    IconButton {
                        glyph: "gear"
                        label: S.tr2(S.rev, "menu.settings")
                        onClicked: {
                            root.dismissed()
                            root.settingsRequested()
                        }
                    }
                    IconButton {
                        id: info
                        glyph: "info"
                        label: S.tr2(S.rev, "metric.info")
                        onClicked: info.checked = !info.checked
                    }
                    IconButton {
                        glyph: "power"
                        label: S.tr2(S.rev, "menu.quit")
                        onClicked: Qt.quit()
                    }
                }
            }
        }

        // Fades the last visible line while more of the panel lies below.
        Rectangle {
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.bottom: parent.bottom
            anchors.margins: 1
            height: 32
            bottomLeftRadius: root.flyout ? 13 : 0
            bottomRightRadius: root.flyout ? 13 : 0
            visible: scroller.interactive
            opacity: scroller.atYEnd ? 0 : 1
            Behavior on opacity { NumberAnimation { duration: 180 } }
            gradient: Gradient {
                GradientStop { position: 0; color: Qt.rgba(Theme.panel.r, Theme.panel.g, Theme.panel.b, 0) }
                GradientStop { position: 1; color: Theme.panel }
            }
        }
    }

    component StatCell: ColumnLayout {
        id: cell
        property string caption
        property string value
        property string unit
        Layout.fillWidth: true
        Layout.preferredWidth: 1
        spacing: 2
        Text {
            text: cell.caption
            color: Theme.ink2
            font.pixelSize: 12
            Layout.fillWidth: true
            elide: Text.ElideRight
        }
        Row {
            spacing: 4
            Text {
                id: statValue
                text: cell.value
                color: Theme.ink
                font.pixelSize: 18
                font.weight: Font.DemiBold
                font.features: Theme.tabular
            }
            Text {
                anchors.baseline: statValue.baseline
                text: cell.unit
                color: Theme.ink3
                font.pixelSize: 11
            }
        }
    }
}
