import QtQuick
import QtQuick.Layouts
import "Sync.js" as Sync

// DESIGN 5 資料來源: one location list — the switches of its sources, one row per entry, then Add Folder… and Reset;
// the group's caption below.
SettingsGroup {
    id: group
    property string list
    property var sources: []
    property string hint
    // Source id → display name, and → {available, sessions} for this computer.
    property var names: ({})
    property var states: ({})
    // While the page is shown, folders that appear or vanish are noticed.
    property bool live: false

    // Rows added after the list was first shown animate in.
    property bool ready: false
    readonly property bool canRemove: locations.revision >= 0 && entryModel.count > 1 && locations.canRemove(list)
    readonly property string pickerTitle: (names[sources[0]] || list) + " — " + S.tr2(S.rev, "settings.sources.add").replace(/[…\.]+$/, "")

    footnote: hint.length > 0 ? S.tr2(S.rev, hint) : ""

    function refresh() {
        var rows = []
        var entries = locations.entries(list)
        for (var i = 0; i < entries.length; ++i) {
            var e = entries[i]
            rows.push({ key: e.key, automatic: e.automatic, linesJson: JSON.stringify(e.lines), path: e.path, exists: e.exists,
                        canReveal: e.canReveal, isFile: e.isFile })
        }
        Sync.sync(entryModel, rows, "key")
        ready = true
    }
    Component.onCompleted: refresh()
    Connections {
        target: locations
        function onChanged() { group.refresh() }
    }

    property ListModel entryModel: ListModel {}
    property Timer removal: Timer {
        property int index: -1
        interval: 190
        onTriggered: locations.remove(group.list, index)
    }
    property Timer resetting: Timer {
        interval: 190
        onTriggered: locations.reset(group.list)
    }
    property Timer watch: Timer {
        interval: 2000
        repeat: true
        running: group.live
        onTriggered: group.refresh()
    }

    Repeater {
        model: group.sources
        SourceToggleRow {
            required property string modelData
            source: modelData
            name: group.names[modelData] || modelData
            info: group.states[modelData] || null
        }
    }
    QuotaGroupRows {
        sources: group.sources
    }
    Repeater {
        id: entryRows
        model: group.entryModel
        LocationRow {
            required property string linesJson
            lines: JSON.parse(linesJson)
            canRemove: group.canRemove
            appear: group.ready
            onRevealRequested: locations.reveal(group.list, index)
            onRemoveRequested: {
                leaving = true
                group.removal.index = index
                group.removal.restart()
            }
        }
    }
    LocationActions {
        canAdd: locations.revision >= 0 && locations.canAdd(group.list)
        canReset: locations.revision >= 0 && locations.canReset(group.list)
        onAddRequested: locations.choose(group.list, group.pickerTitle)
        onResetRequested: {
            for (var i = 0; i < entryRows.count; ++i) {
                var item = entryRows.itemAt(i)
                if (item && !item.automatic) item.leaving = true
            }
            group.resetting.restart()
        }
    }
}
