import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format
import "Sync.js" as Sync

// DESIGN 13 Settings → 遠端主機: one row per host in the order added, Add Host… below them (disabled at 8) and what is
// sent. Without hosts, what the pane is for and the way to add the first one.
SettingsPage {
    id: page
    title: S.tr2(S.rev, "settings.remote")
    readonly property int maximum: 8

    ListModel { id: hostModel }
    function refresh() { Sync.sync(hostModel, hostsView.rows, "hostId") }
    Connections {
        target: hostsView
        function onRowsChanged() { page.refresh() }
    }
    Component.onCompleted: {
        refresh()
        devAid.start()
    }

    function openMenu(index, anchor, x, y) {
        actionsMenu.host = hostModel.get(index)
        actionsMenu.popup(anchor, x, y)
    }

    SettingsGroup {
        title: S.tr2(S.rev, "remote.hosts")
        trailing: hostModel.count > 0 ? Format.fill(S.tr2(S.rev, "orbs.count"), [hostModel.count, page.maximum]) : ""
        footnote: S.tr2(S.rev, "remote.footnote")

        // No host yet.
        ColumnLayout {
            visible: hostModel.count === 0
            Layout.fillWidth: true
            Layout.topMargin: 26
            Layout.bottomMargin: 24
            spacing: 6
            Accessible.role: Accessible.Grouping
            Accessible.name: S.tr2(S.rev, "remote.empty")
            Glyph {
                Layout.alignment: Qt.AlignHCenter
                Layout.preferredWidth: 28
                Layout.preferredHeight: 28
                Layout.bottomMargin: 4
                name: "server"
                color: Theme.ink3
            }
            Text {
                Layout.alignment: Qt.AlignHCenter
                text: S.tr2(S.rev, "remote.empty")
                color: Theme.ink
                font.pixelSize: 13
                font.weight: Font.DemiBold
            }
            Text {
                Layout.fillWidth: true
                Layout.maximumWidth: 440
                Layout.leftMargin: 24
                Layout.rightMargin: 24
                Layout.alignment: Qt.AlignHCenter
                horizontalAlignment: Text.AlignHCenter
                text: S.tr2(S.rev, "remote.empty.hint.computer")
                color: Theme.ink2
                font.pixelSize: 12
                lineHeight: 1.25
                wrapMode: Text.Wrap
            }
            PillButton {
                Layout.alignment: Qt.AlignHCenter
                Layout.topMargin: 8
                accent: true
                text: S.tr2(S.rev, "remote.add")
                onClicked: sheet.openAdd()
            }
        }

        Repeater {
            model: hostModel
            delegate: RemoteHostRow {
                divider: true
                onMenuRequested: (anchor, x, y) => page.openMenu(index, anchor, x, y)
            }
        }

        // Add Host…, disabled at the limit, which then also reads beside it.
        Item {
            visible: hostModel.count > 0
            Layout.fillWidth: true
            implicitHeight: 46
            RowLayout {
                anchors.fill: parent
                anchors.leftMargin: 14
                anchors.rightMargin: 14
                spacing: 10
                Text {
                    Layout.fillWidth: true
                    opacity: remote.canAdd ? 0 : 1
                    Behavior on opacity { NumberAnimation { duration: 180 } }
                    text: Format.fill(S.tr2(S.rev, "remote.limit"), [page.maximum])
                    color: Theme.ink3
                    font.pixelSize: 11
                    elide: Text.ElideRight
                }
                PillButton {
                    id: addButton
                    text: S.tr2(S.rev, "remote.add")
                    enabled: remote.canAdd
                    onClicked: sheet.openAdd()
                    HoverHandler { id: addHover; enabled: !remote.canAdd }
                    ToolTip.visible: addHover.hovered && !remote.canAdd
                    ToolTip.text: Format.fill(S.tr2(S.rev, "remote.limit"), [page.maximum])
                }
            }
        }
    }

    RemoteHostMenu {
        id: actionsMenu
        onEditRequested: sheet.openEdit(host)
        onRemoveRequested: removeDialog.ask(host.hostId, host.name)
    }
    RemoteHostSheet { id: sheet }
    RemoveHostDialog { id: removeDialog }

    // Development aid: --remote-ui=<step>[,<step>…] drives this pane as a user would, for snapshots and the end-to-end
    // check: add[:<destination>], name:<text>, folder:<path>, advanced, test, save, edit:<n>, off:<n>, remove:<n>,
    // menu:<n>, bridge, askBridge. Each step waits for the last one (a test for its result).
    QtObject {
        id: devAid
        property var steps: []
        function start() {
            for (let i = 0; i < Qt.application.arguments.length; ++i) {
                const arg = Qt.application.arguments[i]
                if (arg.indexOf("--remote-ui=") === 0) steps = arg.substr(12).split(",")
            }
            if (steps.length > 0) devTimer.start()
        }
        function next() {
            if (sheet.testState === "running") return
            if (steps.length === 0) { devTimer.stop(); return }
            const step = steps.shift()
            const colon = step.indexOf(":")
            const verb = colon < 0 ? step : step.substr(0, colon)
            const arg = colon < 0 ? "" : step.substr(colon + 1)
            if (verb === "add") {
                sheet.openAdd()
                if (arg.length > 0) sheet.typeDestination(arg)
            } else if (verb === "test") {
                sheet.runTest()
            } else if (verb === "save") {
                sheet.save()
            } else if (verb === "name") {
                sheet.typeName(arg)
            } else if (verb === "folder") {
                sheet.typeFolder(arg)
            } else if (verb === "advanced") {
                sheet.advanced = true
            } else if (verb === "bridge") {
                sheet.quotaBridge = true
            } else if (verb === "askBridge") {
                sheet.askBridge()
            } else if (verb === "edit" && +arg < hostModel.count) {
                sheet.openEdit(hostModel.get(+arg))
            } else if (verb === "off" && +arg < hostModel.count) {
                remote.setHostEnabled(hostModel.get(+arg).hostId, false)
            } else if (verb === "remove" && +arg < hostModel.count) {
                removeDialog.ask(hostModel.get(+arg).hostId, hostModel.get(+arg).name)
            } else if (verb === "menu" && +arg < hostModel.count) {
                page.openMenu(+arg, page, page.width - 120, 150 + 70 * +arg)
            }
        }
    }
    Timer {
        id: devTimer
        interval: 700
        repeat: true
        onTriggered: devAid.next()
    }
}
