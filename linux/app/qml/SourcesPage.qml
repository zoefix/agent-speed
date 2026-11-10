import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 5 資料來源: every source reads a location list (METRICS 2.1). The pane caption, then one group per list — the
// sources' switches, one row per entry, Add Folder… and Reset, the group's caption — then the history actions.
SettingsPage {
    id: page
    title: S.tr2(S.rev, "settings.sources")

    property var names: ({})
    property var states: ({})
    readonly property var groupList: locations.groups()

    function refresh() {
        var raw = appState.snapshot.sources || []
        // DESIGN 13: with remote hosts the source rows add every host; this page shows this computer's own cells.
        var cells = appState.snapshot.hostSources || []
        var local = null
        if (cells.length > 0) {
            local = {}
            for (var c = 0; c < cells.length; ++c) if (cells[c].host === "local") local[cells[c].source] = cells[c]
        }
        var nextNames = {}, nextStates = {}
        for (var i = 0; i < raw.length; ++i) {
            var s = raw[i]
            nextNames[s.source] = s.source === "claudeCode" ? "Claude Code · Claude Desktop" : s.name
            var own = local ? local[s.source] : s
            nextStates[s.source] = { available: own ? own.available === true : false, sessions: own ? own.sessions || 0 : 0 }
        }
        if (JSON.stringify(nextNames) !== JSON.stringify(names)) names = nextNames
        if (JSON.stringify(nextStates) !== JSON.stringify(states)) states = nextStates
    }
    Connections {
        target: appState
        function onSnapshotChanged() { if (page.visible) page.refresh() }
    }
    onVisibleChanged: if (visible) {
        refresh()
        // QUOTAS 5.4: the bridge's status is checked when Settings opens.
        quotas.settingsShown()
    }
    Component.onCompleted: refresh()

    Text {
        Layout.fillWidth: true
        Layout.topMargin: -10
        Layout.leftMargin: 4
        Layout.rightMargin: 4
        text: S.tr2(S.rev, "settings.sources.locationsHint")
        color: Theme.ink2
        font.pixelSize: 12
        lineHeight: 1.3
        wrapMode: Text.Wrap
    }

    Repeater {
        model: page.groupList
        SourceGroup {
            required property var modelData
            list: modelData.list
            sources: modelData.sources
            hint: modelData.hint
            names: page.names
            states: page.states
            live: page.visible
        }
    }

    RowLayout {
        Layout.fillWidth: true
        spacing: 8
        PillButton {
            text: S.tr2(S.rev, "history.open")
            onClicked: appState.historyRequested("")
        }
        PillButton {
            text: S.tr2(S.rev, "history.clear")
            onClicked: confirmClear.open()
        }
        Item { Layout.fillWidth: true }
    }

    Popup {
        id: confirmClear
        parent: Overlay.overlay
        anchors.centerIn: parent
        width: 380
        modal: true
        padding: 20
        background: Rectangle {
            radius: 12
            color: Theme.dark ? "#22252C" : "#FFFFFF"
            border.width: 1
            border.color: Theme.border
        }
        contentItem: ColumnLayout {
            spacing: 16
            Text {
                Layout.fillWidth: true
                // DESIGN 10: traffic history is cleared too and cannot be rebuilt from logs.
                text: S.tr2(S.rev, "history.clear.confirm") + (history.trafficSince.length > 0 ? "\n\n" + S.tr2(S.rev, "history.clear.traffic") : "")
                color: Theme.ink
                font.pixelSize: 13
                lineHeight: 1.3
                wrapMode: Text.Wrap
            }
            RowLayout {
                Layout.alignment: Qt.AlignRight
                spacing: 8
                PillButton { text: S.tr2(S.rev, "history.cancel"); onClicked: confirmClear.close() }
                PillButton {
                    text: S.tr2(S.rev, "history.clear.action")
                    accent: true
                    onClicked: {
                        history.clearRequested()
                        confirmClear.close()
                    }
                }
            }
        }
    }

    LocationNotice { id: notice }
    Connections {
        target: locations
        function onRefused(list, reason, path) { notice.show(reason, path) }
    }
}
