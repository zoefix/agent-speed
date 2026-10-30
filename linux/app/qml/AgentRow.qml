import QtQuick

// One agent: phase badge, project over "model · source", effort capsule, 性能 and the live value; sub-agents indent 12
// with connectors (DESIGN 4).
Item {
    id: row
    required property string key
    required property string project
    required property string detail
    required property string phase
    required property string source
    required property real live
    required property int depth
    required property bool continues
    required property bool hasChildren
    required property int rails
    required property string effort
    required property int effortLevel
    required property bool effortTop
    required property bool effortConfigured
    required property bool speedConfigured
    required property string modesA11y
    required property int bolts
    required property real perf
    required property string perfForm
    required property bool perfProvisional
    required property string perfA11y
    required property bool looping
    required property string trafficSuffix
    required property string status
    required property string liveText
    // Set by the list: the shared capsule column width and the 性能 cell width (DESIGN 4, 12).
    property real capsuleColumn: 0
    property real perfWidth: 35
    property bool capsuleWords: true
    signal hoverChanged(bool hovered)
    implicitHeight: 40
    signal cardRequested()
    readonly property string separator: S.language === "ja" ? "、" : S.language === "en" ? ", " : "，"
    Accessible.role: Accessible.ListItem
    Accessible.name: [depth > 0 ? project + separator + S.tr2(S.rev, "agents.subagent") : project, detail, status,
                      live >= 0.05 ? liveText + " " + S.tr2(S.rev, "unit.tokS") : "", perf >= 0 ? perfA11y : "", modesA11y]
                     .filter(function(t) { return t.length > 0 }).join(separator)
    Accessible.description: S.tr2(S.rev, "a11y.perf.card")
    Accessible.onPressAction: cardRequested()
    activeFocusOnTab: true
    Keys.onSpacePressed: cardRequested()

    readonly property real indent: depth * 12
    readonly property real railX: (depth - 1) * 12 + 7.5

    Rectangle {
        anchors.fill: parent
        anchors.leftMargin: -6
        anchors.rightMargin: -6
        radius: 7
        color: hover.hovered ? Theme.hover : "transparent"
        border.width: row.activeFocus ? 1.5 : 0
        border.color: Theme.accent
        Behavior on color { ColorAnimation { duration: 120 } }
    }
    HoverHandler {
        id: hover
        onHoveredChanged: row.hoverChanged(hovered)
    }

    Repeater {
        model: row.depth > 1 ? row.depth - 1 : 0
        Rectangle {
            required property int index
            visible: (row.rails & (1 << (index + 1))) !== 0
            x: index * 12 + 7.5
            width: 1
            height: row.height
            color: Theme.connector
        }
    }
    Rectangle {
        visible: row.depth > 0
        x: row.railX
        width: 1
        height: row.continues ? row.height : row.height / 2
        color: Theme.connector
    }
    Rectangle {
        visible: row.depth > 0
        x: row.railX
        y: row.height / 2
        width: row.indent + 2 - row.railX
        height: 1
        color: Theme.connector
    }
    Rectangle {
        visible: row.hasChildren
        x: row.indent + 7.5
        y: row.height / 2 + 7
        width: 1
        height: row.height / 2 - 7
        color: Theme.connector
    }

    PhaseBadge {
        id: badge
        x: row.indent
        anchors.verticalCenter: parent.verticalCenter
        phase: row.phase
        tint: Theme.sourceColor(row.source)
    }

    Column {
        anchors.left: badge.right
        anchors.leftMargin: 10
        anchors.right: capsuleSlot.left
        anchors.rightMargin: 10
        anchors.verticalCenter: parent.verticalCenter
        spacing: 1
        Row {
            width: parent.width
            spacing: 4
            Glyph {
                id: loopMark
                anchors.verticalCenter: parent.verticalCenter
                width: row.looping || opacity > 0 ? 11 : 0
                height: 11
                name: "loop"
                color: Theme.amber
                opacity: row.looping ? 1 : 0
                visible: width > 0
                Behavior on opacity { NumberAnimation { duration: row.looping ? 250 : 600 } }
            }
            Text {
                width: parent.width - (loopMark.visible ? loopMark.width + 4 : 0)
                text: row.project
                color: Theme.ink
                font.pixelSize: 13
                font.weight: Font.DemiBold
                elide: Text.ElideRight
            }
        }
        // The detail truncates first; the traffic suffix stays whole (DESIGN 4).
        Row {
            width: parent.width
            Text {
                width: Math.min(implicitWidth, parent.width - suffix.width)
                text: row.detail
                color: Theme.ink2
                font.pixelSize: 12
                elide: Text.ElideRight
            }
            Text {
                id: suffix
                visible: row.trafficSuffix.length > 0
                width: visible ? implicitWidth : 0
                text: " · " + row.trafficSuffix
                color: Theme.ink2
                font.pixelSize: 12
                font.features: Theme.tabular
            }
        }
    }

    Item {
        id: capsuleSlot
        anchors.right: perfCell.left
        anchors.rightMargin: row.capsuleColumn > 0 ? 8 : 6
        anchors.verticalCenter: parent.verticalCenter
        width: row.capsuleColumn
        height: 18
        Behavior on width { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
        EffortCapsule {
            anchors.left: parent.left
            anchors.verticalCenter: parent.verticalCenter
            level: row.effortLevel
            word: row.effort
            topRung: row.effortTop
            bolts: row.bolts
            showWord: row.capsuleWords
            effortConfigured: row.effortConfigured
            speedConfigured: row.speedConfigured
            mark: S.tr2(S.rev, "mode.configured.mark")
        }
    }

    PerfCell {
        id: perfCell
        anchors.right: slot.left
        anchors.rightMargin: 6
        anchors.verticalCenter: parent.verticalCenter
        width: row.perfWidth
        value: row.perf
        form: row.perfForm
        provisional: row.perfProvisional
    }

    LiveSlot {
        id: slot
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        value: row.live
        phase: row.phase
        pixelSize: 14
    }
}
