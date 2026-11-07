import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format
import "Sync.js" as Sync

// DESIGN 14 Settings → API → 區域網路: Devices Read (the server, its status, key, addresses and port), Send to Devices
// (the targets) and what is sent.
ColumnLayout {
    id: page
    // On screen: the addresses refresh and the status lines tick.
    property bool active: false
    property bool keyCopied: false
    property string portError: ""
    spacing: 22

    readonly property real labelColumn: Math.min(150, Math.ceil(Math.max(keyLabel.advanceWidth, addressLabel.advanceWidth, portLabel.advanceWidth)) + 1)
    TextMetrics { id: keyLabel; font.pixelSize: 13; text: S.tr2(S.rev, "lan.key") }
    TextMetrics { id: addressLabel; font.pixelSize: 13; text: S.tr2(S.rev, "lan.addresses") }
    TextMetrics { id: portLabel; font.pixelSize: 13; text: S.tr2(S.rev, "lan.port") }

    onActiveChanged: lan.setWatching(active)
    Component.onDestruction: if (active) lan.setWatching(false)

    ListModel { id: targetModel }
    function refresh() { Sync.sync(targetModel, lan.targets, "targetId") }
    Connections {
        target: lan
        function onChanged() { page.refresh() }
    }
    Component.onCompleted: {
        refresh()
        devAid.start()
    }

    function openMenu(index, anchor, x, y) {
        targetMenu.targetId = targetModel.get(index).targetId
        targetMenu.targetTitle = targetModel.get(index).title
        targetMenu.popup(anchor, x, y)
    }
    function withoutEllipsis(text) { return text.replace(/(…|\.\.\.)$/, "") }

    // 1. Devices read.
    SettingsGroup {
        title: S.tr2(S.rev, "lan.server")
        SettingsRow {
            label: S.tr2(S.rev, "lan.server.enabled")
            caption: lan.networkEnabled ? S.tr2(S.rev, "lan.server.hint") : S.tr2(S.rev, "settings.network.offHint")
            divider: lan.serverEnabled
            ToggleSwitch {
                label: S.tr2(S.rev, "lan.server.enabled")
                enabled: lan.networkEnabled
                checked: lan.serverEnabled
                onToggled: (on) => lan.setServerEnabled(on)
            }
        }
        // The rows of a running server expand in.
        Item {
            id: serverRows
            readonly property bool shown: lan.serverEnabled
            Layout.fillWidth: true
            implicitHeight: shown ? rows.implicitHeight : 0
            visible: implicitHeight > 0
            clip: true
            Behavior on implicitHeight { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
            ColumnLayout {
                id: rows
                width: parent.width
                spacing: 0
                opacity: serverRows.shown ? 1 : 0
                Behavior on opacity { NumberAnimation { duration: 200 } }

                // Status.
                Item {
                    Layout.fillWidth: true
                    implicitHeight: statusText.implicitHeight + 22
                    Accessible.role: Accessible.StaticText
                    Accessible.name: lan.serverLine
                    HostDot {
                        x: 15
                        y: statusText.y + (statusText.lineHeightPx - height) / 2
                        kind: lan.serverDot
                    }
                    Text {
                        id: statusText
                        readonly property real lineHeightPx: Math.ceil(font.pixelSize * 1.35)
                        x: 33
                        y: 11
                        width: parent.width - x - 14
                        text: lan.serverLine
                        color: Theme.ink
                        font.pixelSize: 13
                        wrapMode: Text.Wrap
                        Accessible.ignored: true
                    }
                    Rectangle {
                        anchors.left: parent.left
                        anchors.leftMargin: 14
                        anchors.right: parent.right
                        anchors.bottom: parent.bottom
                        height: 1
                        color: Theme.hairline
                    }
                }

                // Access key.
                LanRow {
                    label: S.tr2(S.rev, "lan.key")
                    labelWidth: page.labelColumn
                    RowLayout {
                        width: parent.width
                        spacing: 6
                        TextEdit {
                            Layout.fillWidth: true
                            Layout.alignment: Qt.AlignVCenter
                            text: lan.keyDisplay
                            readOnly: true
                            selectByMouse: true
                            activeFocusOnPress: false
                            color: Theme.ink
                            selectionColor: Theme.accent
                            selectedTextColor: "white"
                            font.pixelSize: 13
                            font.family: "monospace"
                            font.weight: Font.Medium
                            font.letterSpacing: 0.4
                            Accessible.role: Accessible.StaticText
                            Accessible.name: S.tr2(S.rev, "lan.key") + ": " + lan.keySpoken
                        }
                        IconButton {
                            id: copyKey
                            Layout.alignment: Qt.AlignVCenter
                            width: 26
                            height: 26
                            glyph: page.keyCopied ? "check" : "copy"
                            label: S.tr2(S.rev, "lan.key.copy")
                            onClicked: {
                                lan.copyKey()
                                page.keyCopied = true
                                copiedTimer.restart()
                            }
                            HoverHandler { id: copyHover }
                            ToolTip.visible: copyHover.hovered || page.keyCopied
                            ToolTip.delay: page.keyCopied ? 0 : 500
                            ToolTip.text: S.tr2(S.rev, page.keyCopied ? "lan.key.copied" : "lan.key.copy")
                            Timer {
                                id: copiedTimer
                                interval: 1500
                                onTriggered: page.keyCopied = false
                            }
                        }
                        LinkButton {
                            Layout.alignment: Qt.AlignVCenter
                            text: S.tr2(S.rev, "lan.key.regenerate")
                            onClicked: regenerateDialog.ask()
                        }
                    }
                }

                // Addresses of this computer.
                LanRow {
                    label: S.tr2(S.rev, "lan.addresses")
                    labelWidth: page.labelColumn
                    ColumnLayout {
                        width: parent.width
                        spacing: 4
                        Text {
                            visible: lan.addresses.length === 0
                            Layout.fillWidth: true
                            Layout.minimumHeight: 28
                            verticalAlignment: Text.AlignVCenter
                            text: S.tr2(S.rev, "lan.addresses.none")
                            color: Theme.ink2
                            font.pixelSize: 12
                            wrapMode: Text.Wrap
                        }
                        Repeater {
                            model: lan.addresses
                            RowLayout {
                                required property var modelData
                                required property int index
                                Layout.fillWidth: true
                                Layout.preferredHeight: 28
                                spacing: 8
                                TextEdit {
                                    Layout.fillWidth: true
                                    Layout.alignment: Qt.AlignVCenter
                                    text: modelData.address
                                    readOnly: true
                                    selectByMouse: true
                                    activeFocusOnPress: false
                                    color: Theme.ink2
                                    selectionColor: Theme.accent
                                    selectedTextColor: "white"
                                    font.pixelSize: 13
                                    font.family: "monospace"
                                    Accessible.role: Accessible.StaticText
                                    Accessible.name: text
                                }
                                LanCopyButton {
                                    objectName: "lanCopy" + index
                                    Layout.alignment: Qt.AlignVCenter
                                    http: modelData.http
                                    ws: modelData.ws
                                    tcp: modelData.tcp
                                }
                            }
                        }
                    }
                }

                // Port.
                LanRow {
                    label: S.tr2(S.rev, "lan.port")
                    labelWidth: page.labelColumn
                    lineHeight: 30
                    divider: false
                    ColumnLayout {
                        width: parent.width
                        spacing: 5
                        FieldInput {
                            id: portField
                            Layout.preferredWidth: 72
                            text: String(lan.port)
                            maximumLength: 5
                            validator: RegularExpressionValidator { regularExpression: /[0-9]*/ }
                            accessibleName: S.tr2(S.rev, "lan.port")
                            accessibleDescription: page.portError
                            error: page.portError.length > 0
                            onEdited: (t) => page.portError = ""
                            onCommitted: (t) => {
                                page.portError = lan.portError(t)
                                if (page.portError.length === 0) lan.setPort(t)
                            }
                        }
                        RowLayout {
                            visible: page.portError.length > 0
                            Layout.fillWidth: true
                            spacing: 4
                            Glyph {
                                Layout.alignment: Qt.AlignTop
                                Layout.topMargin: 1
                                Layout.preferredWidth: 11
                                Layout.preferredHeight: 11
                                name: "alertCircle"
                                color: Theme.danger
                            }
                            Text {
                                Layout.fillWidth: true
                                text: page.portError
                                color: Theme.danger
                                font.pixelSize: 11
                                wrapMode: Text.Wrap
                            }
                        }
                    }
                }
            }
        }
    }

    // 2. Send to devices.
    SettingsGroup {
        title: S.tr2(S.rev, "lan.targets")
        trailing: targetModel.count > 0 ? Format.fill(S.tr2(S.rev, "orbs.count"), [targetModel.count, lan.maxTargets]) : ""

        ColumnLayout {
            visible: targetModel.count === 0
            Layout.fillWidth: true
            Layout.topMargin: 26
            Layout.bottomMargin: 24
            spacing: 6
            Accessible.role: Accessible.Grouping
            Accessible.name: S.tr2(S.rev, "lan.targets.empty")
            Glyph {
                Layout.alignment: Qt.AlignHCenter
                Layout.preferredWidth: 28
                Layout.preferredHeight: 28
                Layout.bottomMargin: 4
                name: "broadcast"
                color: Theme.ink3
            }
            Text {
                Layout.alignment: Qt.AlignHCenter
                text: S.tr2(S.rev, "lan.targets.empty")
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
                text: S.tr2(S.rev, "lan.targets.empty.hint")
                color: Theme.ink2
                font.pixelSize: 12
                lineHeight: 1.25
                wrapMode: Text.Wrap
            }
            PillButton {
                Layout.alignment: Qt.AlignHCenter
                Layout.topMargin: 8
                accent: true
                text: S.tr2(S.rev, "lan.targets.add")
                onClicked: sheet.openAdd()
            }
        }

        Repeater {
            model: targetModel
            delegate: LanTargetRow {
                divider: true
                onMenuRequested: (anchor, x, y) => page.openMenu(index, anchor, x, y)
            }
        }

        // Add Device…, disabled at the limit, which then also reads beside it.
        Item {
            visible: targetModel.count > 0
            Layout.fillWidth: true
            implicitHeight: 46
            RowLayout {
                anchors.fill: parent
                anchors.leftMargin: 14
                anchors.rightMargin: 14
                spacing: 10
                Text {
                    Layout.fillWidth: true
                    opacity: lan.canAdd ? 0 : 1
                    Behavior on opacity { NumberAnimation { duration: 180 } }
                    text: Format.fill(S.tr2(S.rev, "lan.targets.limit"), [lan.maxTargets])
                    color: Theme.ink3
                    font.pixelSize: 11
                    elide: Text.ElideRight
                }
                PillButton {
                    text: S.tr2(S.rev, "lan.targets.add")
                    enabled: lan.canAdd
                    onClicked: sheet.openAdd()
                    HoverHandler { id: addHover; enabled: !lan.canAdd }
                    ToolTip.visible: addHover.hovered && !lan.canAdd
                    ToolTip.text: Format.fill(S.tr2(S.rev, "lan.targets.limit"), [lan.maxTargets])
                }
            }
        }
    }

    // 3. What is sent, and the developer guide.
    ColumnLayout {
        Layout.fillWidth: true
        Layout.leftMargin: 4
        Layout.rightMargin: 4
        Layout.topMargin: -10
        spacing: 6
        Text {
            Layout.fillWidth: true
            text: S.tr2(S.rev, "lan.footnote")
            color: Theme.ink3
            font.pixelSize: 11
            lineHeight: 1.3
            wrapMode: Text.Wrap
        }
        LinkButton {
            Layout.leftMargin: -2
            text: S.tr2(S.rev, "lan.docs")
            onClicked: catalog.openUrl("https://github.com/zoefix/agent-speed/blob/main/spec/LAN.md")
        }
    }

    Menu {
        id: targetMenu
        property string targetId
        property string targetTitle
        MenuItem {
            text: S.tr2(S.rev, "lan.target.edit")
            onTriggered: sheet.openEdit(targetMenu.targetId)
        }
        MenuSeparator {}
        MenuItem {
            text: S.tr2(S.rev, "lan.target.remove")
            palette.text: Theme.danger
            onTriggered: page.askRemove(targetMenu.targetId, targetMenu.targetTitle)
        }
    }
    function askRemove(id, title) {
        removeDialog.targetId = id
        removeDialog.question = Format.fill(S.tr2(S.rev, "lan.target.remove.confirm"), [title])
        removeDialog.ask()
    }
    LanTargetSheet { id: sheet }
    LanConfirmDialog {
        id: regenerateDialog
        question: S.tr2(S.rev, "lan.key.regenerate.confirm")
        detail: S.tr2(S.rev, "lan.key.regenerate.detail")
        action: page.withoutEllipsis(S.tr2(S.rev, "lan.key.regenerate"))
        glyph: "reset"
        onConfirmed: lan.regenerateKey()
    }
    LanConfirmDialog {
        id: removeDialog
        property string targetId
        action: S.tr2(S.rev, "remote.remove.button")
        destructive: true
        onConfirmed: lan.removeTarget(targetId)
    }

    // Development aid: --lan-ui=<step>[,<step>…] drives this pane as a user would, for snapshots and the end-to-end
    // check: server:on|off, network:on|off (the master switch), sleep:<s>, add[:udp|http], address:<text>, name:<text>,
    // key:<text>, test, save, edit:<n>, off:<n>, remove:<n>, menu:<n>, copy:<n>, regenerate. Each step waits for the last
    // one (a test for its result).
    QtObject {
        id: devAid
        property var steps: []
        property int idle: 0
        function start() {
            for (let i = 0; i < Qt.application.arguments.length; ++i) {
                const arg = Qt.application.arguments[i]
                if (arg.indexOf("--lan-ui=") === 0) steps = arg.substr(9).split(",").filter(s => s.length > 0 && s !== "lan")
            }
            if (steps.length > 0) devTimer.start()
        }
        function target(n) { return +n < targetModel.count ? targetModel.get(+n) : null }
        function next() {
            if (sheet.testState === "running") return
            if (idle > 0) { --idle; return }
            if (steps.length === 0) { devTimer.stop(); return }
            const step = steps.shift()
            const colon = step.indexOf(":")
            const verb = colon < 0 ? step : step.substr(0, colon)
            const arg = colon < 0 ? "" : step.substr(colon + 1)
            if (verb === "server") lan.setServerEnabled(arg !== "off")
            else if (verb === "network") prefs.networkEnabled = arg !== "off"
            else if (verb === "sleep") idle = Math.round(+arg * 1000 / devTimer.interval)
            else if (verb === "add") sheet.openAdd(arg.length > 0 ? arg : "udp")
            else if (verb === "address") sheet.typeAddress(arg)
            else if (verb === "name") sheet.typeName(arg)
            else if (verb === "key") sheet.typeKey(arg)
            else if (verb === "test") sheet.runTest()
            else if (verb === "save") sheet.save()
            else if (verb === "edit" && target(arg)) sheet.openEdit(target(arg).targetId)
            else if (verb === "off" && target(arg)) lan.setTargetEnabled(target(arg).targetId, false)
            else if (verb === "remove" && target(arg)) page.askRemove(target(arg).targetId, target(arg).title)
            else if (verb === "menu" && target(arg)) page.openMenu(+arg, page, page.width - 120, page.height - 160 + 70 * +arg)
            else if (verb === "regenerate") regenerateDialog.ask()
            else if (verb === "copy") {
                const button = page.findCopy(page, "lanCopy" + (arg || "0"))
                if (button) button.popup()
            }
        }
    }
    function findCopy(item, name) {
        if (item.objectName === name) return item
        for (let i = 0; i < item.children.length; ++i) {
            const found = findCopy(item.children[i], name)
            if (found) return found
        }
        return null
    }
    Timer {
        id: devTimer
        interval: 700
        repeat: true
        onTriggered: devAid.next()
    }
}
