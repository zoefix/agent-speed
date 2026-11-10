import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 13 Add / Edit sheet (420): the destination checked as you type, the name (placeholder = the automatic name),
// Advanced with the relay folder, and Test Connection with its result inline. Save stays disabled while a field is
// invalid; saving an enabled host connects it at once. Editing a field clears the last test.
Popup {
    id: sheet
    parent: Overlay.overlay
    anchors.centerIn: parent
    width: 420
    modal: true
    focus: true
    padding: 0
    closePolicy: Popup.CloseOnEscape

    property string editingId: ""
    property string originalName: ""
    property string destination: ""
    property string name: ""
    property string folder: ""
    property bool advanced: false
    // REMOTE 11.3: the host's Claude Code status line bridge, saved with the host.
    property bool quotaBridge: false
    property bool destinationEdited: false
    // idle | running | done
    property string testState: "idle"
    property var testResult: ({})
    property int testToken: 0
    readonly property bool editing: editingId.length > 0
    // What the window leaves for the fields between the header and the buttons.
    readonly property real formRoom: Math.max(160, (parent ? parent.height : 600) - 48 - headerBlock.implicitHeight - 40 - buttonsBlock.implicitHeight - 33)
    // One label column for every field, as wide as the longest label (capped; longer labels wrap).
    readonly property real labelColumn: Math.min(130, Math.ceil(Math.max(destinationLabel.advanceWidth, nameLabel.advanceWidth,
                                                                           folderLabel.advanceWidth)) + 1)
    TextMetrics { id: destinationLabel; font.pixelSize: 13; text: S.tr2(S.rev, "remote.destination") }
    TextMetrics { id: nameLabel; font.pixelSize: 13; text: S.tr2(S.rev, "remote.name") }
    TextMetrics { id: folderLabel; font.pixelSize: 13; text: S.tr2(S.rev, "remote.relayFolder") }
    readonly property string cleanDestination: destination.trim()
    readonly property string destinationError: S.rev >= 0 ? hostsView.destinationError(destination) : ""
    readonly property string nameError: S.rev >= 0 ? hostsView.nameError(name) : ""
    readonly property string folderError: S.rev >= 0 ? hostsView.folderError(folder) : ""
    readonly property bool valid: destinationError.length === 0 && nameError.length === 0 && folderError.length === 0
    readonly property string automaticName: S.rev >= 0 && destinationError.length === 0 ? hostsView.automaticName(destination, editingId) : ""
    readonly property string effectiveFolder: folder.trim().length > 0 ? folder.trim() : "~/.cache/agent-speed"
    readonly property string testBlocker: !prefs.networkEnabled ? S.tr2(S.rev, "remote.status.networkOff")
                                          : destinationError.length > 0 ? destinationError : folderError
    readonly property string title: editing ? Format.fill(S.tr2(S.rev, "remote.sheet.edit"), [originalName]) : S.tr2(S.rev, "remote.sheet.add")

    function openAdd() {
        editingId = ""
        originalName = ""
        load("", "", "")
        quotaBridge = false
        destinationEdited = false
        open()
        destinationField.focusField()
    }
    function openEdit(host) {
        editingId = host.hostId
        originalName = host.name
        load(host.destination, host.customName || "", host.relayFolder || "")
        quotaBridge = remote.hostQuotaBridge(host.hostId)
        destinationEdited = true
        open()
    }
    function load(d, n, f) {
        destination = d
        name = n
        folder = f
        advanced = f.length > 0
        resetTest()
        for (const pair of [[destinationField, d], [nameField, n], [folderField, f]]) {
            pair[0].text = pair[1]
            pair[0].restore()
        }
    }
    // Development aid: the bridge's confirmation as the switch asks it.
    function askBridge() { bridgeDialog.ask() }
    // Development aid: the destination as if typed.
    function typeDestination(text) { destinationField.type(text) }
    function typeName(text) { nameField.type(text) }
    function typeFolder(text) {
        advanced = true
        folderField.type(text)
    }
    function resetTest() {
        if (testState === "running") remote.cancelTest(testToken)
        testState = "idle"
        testResult = ({})
    }
    function runTest() {
        if (testBlocker.length > 0 || testState === "running") return
        testState = "running"
        testToken = remote.testConnection(cleanDestination, folder.trim())
    }
    function save() {
        if (!valid) return
        if (editing) {
            if (!remote.updateHost(editingId, cleanDestination, name, folder.trim())) return
            remote.setHostQuotaBridge(editingId, quotaBridge)
        } else {
            const id = remote.canAdd ? remote.addHost(cleanDestination, name, folder.trim()) : ""
            if (id.length === 0) return
            remote.setHostQuotaBridge(id, quotaBridge)
        }
        close()
    }
    onClosed: resetTest()

    Connections {
        target: remote
        function onTestFinished(token, result) {
            if (token !== sheet.testToken || sheet.testState !== "running") return
            sheet.testResult = hostsView.testResult(result, sheet.cleanDestination)
            sheet.testState = "done"
        }
    }

    Overlay.modal: Rectangle {
        color: Qt.rgba(0, 0, 0, Theme.dark ? 0.46 : 0.22)
        Behavior on opacity { NumberAnimation { duration: 160 } }
    }
    enter: Transition {
        ParallelAnimation {
            NumberAnimation { property: "opacity"; from: 0; to: 1; duration: 170; easing.type: Easing.OutCubic }
            NumberAnimation { property: "scale"; from: 0.97; to: 1; duration: 200; easing.type: Easing.OutCubic }
        }
    }
    exit: Transition { NumberAnimation { property: "opacity"; from: 1; to: 0; duration: 120 } }
    background: Rectangle {
        radius: 14
        color: Theme.dark ? "#22252C" : "#FFFFFF"
        border.width: 1
        border.color: Theme.cardLine
    }

    contentItem: ColumnLayout {
        spacing: 0
        Accessible.role: Accessible.Dialog
        Accessible.name: sheet.title

        // Header: symbol, title and what to type.
        ColumnLayout {
            id: headerBlock
            Layout.fillWidth: true
            Layout.margins: 24
            Layout.bottomMargin: 16
            spacing: 10
            RowLayout {
                Layout.fillWidth: true
                spacing: 12
                Rectangle {
                    Layout.preferredWidth: 30
                    Layout.preferredHeight: 30
                    Layout.alignment: Qt.AlignTop
                    radius: 8
                    gradient: Gradient {
                        GradientStop { position: 0; color: Qt.lighter(Theme.accent, 1.18) }
                        GradientStop { position: 1; color: Theme.accent }
                    }
                    Glyph {
                        anchors.centerIn: parent
                        width: 16
                        height: 16
                        name: "server"
                        color: "white"
                    }
                }
                Text {
                    Layout.fillWidth: true
                    Layout.alignment: Qt.AlignVCenter
                    text: sheet.title
                    color: Theme.ink
                    font.pixelSize: 17
                    font.weight: Font.DemiBold
                    wrapMode: Text.Wrap
                    maximumLineCount: 2
                    elide: Text.ElideRight
                    Accessible.role: Accessible.Heading
                    Accessible.name: text
                }
            }
            Text {
                Layout.fillWidth: true
                text: S.tr2(S.rev, "remote.sheet.body")
                color: Theme.ink2
                font.pixelSize: 13
                lineHeight: 1.25
                wrapMode: Text.Wrap
            }
        }

        // The fields scroll when the window is too short for the whole sheet.
        Flickable {
            id: formScroll
            Layout.fillWidth: true
            Layout.preferredHeight: Math.min(form.implicitHeight, sheet.formRoom)
            contentWidth: width
            contentHeight: form.implicitHeight
            boundsBehavior: Flickable.StopAtBounds
            interactive: contentHeight > height
            clip: interactive
            ScrollBar.vertical: ScrollBar { policy: formScroll.interactive ? ScrollBar.AsNeeded : ScrollBar.AlwaysOff }
            ColumnLayout {
                id: form
                width: formScroll.width
                spacing: 0
                // Destination and name.
                Rectangle {
                    Layout.fillWidth: true
                    Layout.leftMargin: 24
                    Layout.rightMargin: 24
                    implicitHeight: fields.implicitHeight + 24
                    radius: 10
                    color: Theme.card
                    border.width: 1
                    border.color: Theme.border
                    GridLayout {
                        id: fields
                        x: 14
                        y: 12
                        width: parent.width - 28
                        columns: 2
                        columnSpacing: 14
                        rowSpacing: 6
                        Text {
                            Layout.alignment: Qt.AlignTop
                            Layout.topMargin: 7
                            Layout.preferredWidth: sheet.labelColumn
                            text: S.tr2(S.rev, "remote.destination")
                            color: Theme.ink
                            font.pixelSize: 13
                            wrapMode: Text.Wrap
                        }
                        ColumnLayout {
                            Layout.fillWidth: true
                            spacing: 5
                            FieldInput {
                                id: destinationField
                                Layout.fillWidth: true
                                passEscape: true
                                monospace: true
                                placeholder: S.tr2(S.rev, "remote.destination.placeholder")
                                accessibleName: S.tr2(S.rev, "remote.destination")
                                accessibleDescription: sheet.destinationEdited ? sheet.destinationError : ""
                                error: sheet.destinationEdited && sheet.destinationError.length > 0
                                onEdited: (t) => {
                                    sheet.destination = t
                                    sheet.destinationEdited = true
                                    sheet.resetTest()
                                }
                                onReturnPressed: (t) => {
                                    sheet.destination = t
                                    sheet.save()
                                }
                            }
                            FieldError {
                                Layout.fillWidth: true
                                text: sheet.destinationEdited ? sheet.destinationError : ""
                            }
                        }
                        Text {
                            Layout.alignment: Qt.AlignTop
                            Layout.topMargin: 7
                            Layout.preferredWidth: sheet.labelColumn
                            text: S.tr2(S.rev, "remote.name")
                            color: Theme.ink
                            font.pixelSize: 13
                            wrapMode: Text.Wrap
                        }
                        ColumnLayout {
                            Layout.fillWidth: true
                            spacing: 5
                            FieldInput {
                                id: nameField
                                Layout.fillWidth: true
                                passEscape: true
                                placeholder: sheet.automaticName
                                accessibleName: S.tr2(S.rev, "remote.name")
                                accessibleDescription: sheet.nameError
                                error: sheet.nameError.length > 0
                                onEdited: (t) => sheet.name = t
                                onReturnPressed: (t) => {
                                    sheet.name = t
                                    sheet.save()
                                }
                            }
                            FieldError {
                                Layout.fillWidth: true
                                text: sheet.nameError
                            }
                        }
                    }
                }

                // DESIGN 13 Host quotas: the host's Claude Code status line bridge, and its state under the switch.
                Rectangle {
                    id: quotaCard
                    readonly property var line: quotas.revision >= 0 && sheet.editing ? quotas.hostBridgeLine(sheet.editingId) : ({})
                    Layout.fillWidth: true
                    Layout.leftMargin: 24
                    Layout.rightMargin: 24
                    Layout.topMargin: 12
                    implicitHeight: quotaRow.implicitHeight + 24
                    radius: 10
                    color: Theme.card
                    border.width: 1
                    border.color: Theme.border
                    RowLayout {
                        id: quotaRow
                        anchors.left: parent.left
                        anchors.leftMargin: 14
                        anchors.right: parent.right
                        anchors.rightMargin: 14
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 12
                        ColumnLayout {
                            Layout.fillWidth: true
                            spacing: 3
                            Text {
                                Layout.fillWidth: true
                                text: S.tr2(S.rev, "remote.quota.claudeCli")
                                color: Theme.ink
                                font.pixelSize: 13
                                wrapMode: Text.Wrap
                            }
                            Text {
                                Layout.fillWidth: true
                                text: S.tr2(S.rev, "remote.quota.claudeCli.hint")
                                color: Theme.ink3
                                font.pixelSize: 11
                                lineHeight: 1.25
                                wrapMode: Text.Wrap
                            }
                            RowLayout {
                                visible: sheet.quotaBridge && (quotaCard.line.text || "").length > 0
                                Layout.topMargin: 2
                                Layout.fillWidth: true
                                spacing: 5
                                Glyph {
                                    Layout.preferredWidth: 11
                                    Layout.preferredHeight: 11
                                    Layout.alignment: Qt.AlignTop
                                    Layout.topMargin: 1
                                    name: quotaCard.line.warning ? "warning" : quotaCard.line.ok ? "checkCircle" : "dotRing"
                                    color: quotaCard.line.warning ? (Theme.dark ? "#FB923C" : "#E86A0C") : quotaCard.line.ok ? Theme.good : Theme.ink3
                                    Accessible.ignored: true
                                }
                                Text {
                                    Layout.fillWidth: true
                                    // The host reports the settings path: plain text only (REMOTE 5.6).
                                    textFormat: Text.PlainText
                                    text: quotaCard.line.text || ""
                                    color: quotaCard.line.warning ? (Theme.dark ? "#FDBA74" : "#C2410C") : Theme.ink2
                                    font.pixelSize: 11
                                    font.features: Theme.tabular
                                    wrapMode: Text.Wrap
                                }
                            }
                        }
                        ToggleSwitch {
                            Layout.alignment: Qt.AlignTop
                            label: S.tr2(S.rev, "remote.quota.claudeCli")
                            checked: sheet.quotaBridge
                            onToggled: (on) => on ? bridgeDialog.ask() : (sheet.quotaBridge = false)
                        }
                    }
                }

                // Advanced: the relay folder, its value in effect while closed.
                Rectangle {
                    Layout.fillWidth: true
                    Layout.leftMargin: 24
                    Layout.rightMargin: 24
                    Layout.topMargin: 12
                    implicitHeight: advancedBody.implicitHeight
                    radius: 10
                    color: Theme.card
                    border.width: 1
                    border.color: Theme.border
                    clip: true
                    ColumnLayout {
                        id: advancedBody
                        width: parent.width
                        spacing: 0
                        Item {
                            id: disclosure
                            Layout.fillWidth: true
                            implicitHeight: 40
                            activeFocusOnTab: true
                            Accessible.role: Accessible.Button
                            Accessible.name: S.tr2(S.rev, "remote.advanced")
                            Accessible.description: sheet.advanced ? "" : sheet.effectiveFolder
                            Accessible.onPressAction: sheet.advanced = !sheet.advanced
                            Keys.onSpacePressed: sheet.advanced = !sheet.advanced
                            Keys.onReturnPressed: sheet.advanced = !sheet.advanced
                            Rectangle {
                                anchors.fill: parent
                                anchors.margins: 3
                                radius: 8
                                color: disclosureArea.containsMouse ? Theme.hover : "transparent"
                                border.width: disclosure.activeFocus ? 1.5 : 0
                                border.color: Theme.accent
                            }
                            RowLayout {
                                anchors.fill: parent
                                anchors.leftMargin: 14
                                anchors.rightMargin: 12
                                spacing: 10
                                Text {
                                    text: S.tr2(S.rev, "remote.advanced")
                                    color: Theme.ink
                                    font.pixelSize: 13
                                }
                                Text {
                                    Layout.fillWidth: true
                                    horizontalAlignment: Text.AlignRight
                                    opacity: sheet.advanced ? 0 : 1
                                    Behavior on opacity { NumberAnimation { duration: 160 } }
                                    text: sheet.effectiveFolder
                                    color: Theme.ink3
                                    font.pixelSize: 12
                                    font.family: "monospace"
                                    elide: Text.ElideMiddle
                                }
                                Glyph {
                                    Layout.preferredWidth: 12
                                    Layout.preferredHeight: 12
                                    name: "chevron"
                                    color: Theme.ink2
                                    rotation: sheet.advanced ? 0 : -90
                                    Behavior on rotation { NumberAnimation { duration: 180; easing.type: Easing.OutCubic } }
                                }
                            }
                            MouseArea {
                                id: disclosureArea
                                anchors.fill: parent
                                hoverEnabled: true
                                cursorShape: Qt.PointingHandCursor
                                onClicked: sheet.advanced = !sheet.advanced
                            }
                        }
                        ColumnLayout {
                            visible: sheet.advanced
                            Layout.fillWidth: true
                            Layout.leftMargin: 14
                            Layout.rightMargin: 14
                            Layout.bottomMargin: 12
                            spacing: 6
                            opacity: sheet.advanced ? 1 : 0
                            Behavior on opacity { NumberAnimation { duration: 180 } }
                            Rectangle { Layout.fillWidth: true; Layout.bottomMargin: 6; height: 1; color: Theme.hairline }
                            RowLayout {
                                Layout.fillWidth: true
                                spacing: 14
                                Text {
                                    Layout.alignment: Qt.AlignTop
                                    Layout.topMargin: 7
                                    Layout.preferredWidth: sheet.labelColumn
                                    text: S.tr2(S.rev, "remote.relayFolder")
                                    color: Theme.ink
                                    font.pixelSize: 13
                                    wrapMode: Text.Wrap
                                }
                                ColumnLayout {
                                    Layout.fillWidth: true
                                    spacing: 5
                                    FieldInput {
                                        id: folderField
                                        Layout.fillWidth: true
                                        passEscape: true
                                        monospace: true
                                        placeholder: "~/.cache/agent-speed"
                                        accessibleName: S.tr2(S.rev, "remote.relayFolder")
                                        accessibleDescription: sheet.folderError.length > 0 ? sheet.folderError : S.tr2(S.rev, "remote.relayFolder.hint")
                                        error: sheet.folderError.length > 0
                                        onEdited: (t) => {
                                            sheet.folder = t
                                            sheet.resetTest()
                                        }
                                        onReturnPressed: (t) => {
                                            sheet.folder = t
                                            sheet.save()
                                        }
                                    }
                                    FieldError {
                                        Layout.fillWidth: true
                                        text: sheet.folderError
                                    }
                                }
                            }
                            Text {
                                Layout.fillWidth: true
                                text: S.tr2(S.rev, "remote.relayFolder.hint")
                                color: Theme.ink3
                                font.pixelSize: 11
                                lineHeight: 1.25
                                wrapMode: Text.Wrap
                            }
                        }
                    }
                }

                // Test Connection and its result.
                RowLayout {
                    Layout.fillWidth: true
                    Layout.leftMargin: 24
                    Layout.rightMargin: 24
                    Layout.topMargin: 14
                    Layout.bottomMargin: 20
                    spacing: 12
                    PillButton {
                        id: testButton
                        Layout.alignment: Qt.AlignTop
                        text: S.tr2(S.rev, "remote.test")
                        enabled: sheet.testBlocker.length === 0 && sheet.testState !== "running"
                        onClicked: sheet.runTest()
                        HoverHandler { id: testHover }
                        ToolTip.visible: testHover.hovered && sheet.testBlocker.length > 0
                        ToolTip.text: sheet.testBlocker
                    }
                    RowLayout {
                        Layout.fillWidth: true
                        Layout.alignment: Qt.AlignVCenter
                        spacing: 6
                        visible: opacity > 0
                        opacity: sheet.testState === "idle" ? 0 : 1
                        Behavior on opacity { NumberAnimation { duration: 180 } }
                        Glyph {
                            id: testMark
                            Layout.alignment: Qt.AlignTop
                            Layout.topMargin: 1
                            Layout.preferredWidth: 14
                            Layout.preferredHeight: 14
                            name: sheet.testState === "running" ? "dotRing" : sheet.testResult.ok ? "checkCircle" : "alertCircle"
                            color: sheet.testState === "running" ? Theme.ink3 : sheet.testResult.ok ? Theme.good
                                   : sheet.testResult.attention ? Theme.danger : (Theme.dark ? "#FB923C" : "#E86A0C")
                            RotationAnimator on rotation {
                                running: sheet.testState === "running" && sheet.visible
                                from: 0
                                to: 360
                                duration: 1100
                                loops: Animation.Infinite
                                onRunningChanged: if (!running) testMark.rotation = 0
                            }
                        }
                        TextEdit {
                            Layout.fillWidth: true
                            text: sheet.testState === "running" ? S.tr2(S.rev, "remote.test.running") : (sheet.testResult.text || "")
                            color: Theme.ink2
                            font.pixelSize: 12
                            wrapMode: TextEdit.Wrap
                            readOnly: true
                            selectByMouse: true
                            activeFocusOnPress: false
                            selectionColor: Theme.accent
                            selectedTextColor: "white"
                            Accessible.role: Accessible.StaticText
                            Accessible.name: text
                        }
                    }
                }
            }
        }

        Rectangle { Layout.fillWidth: true; height: 1; color: Theme.hairline }

        RowLayout {
            id: buttonsBlock
            Layout.fillWidth: true
            Layout.margins: 16
            Layout.leftMargin: 24
            Layout.rightMargin: 24
            spacing: 8
            Item { Layout.fillWidth: true }
            PillButton {
                text: S.tr2(S.rev, "history.cancel")
                onClicked: sheet.close()
            }
            PillButton {
                id: saveButton
                accent: true
                text: S.tr2(S.rev, sheet.editing ? "remote.save" : "remote.addButton")
                enabled: sheet.valid && (sheet.editing || remote.canAdd)
                onClicked: sheet.save()
            }
        }
    }

    QuotaConfirmDialog {
        id: bridgeDialog
        glyph: "gauge"
        question: Format.fill(S.tr2(S.rev, "remote.quota.claudeCli.confirm.title"),
                              [sheet.editing ? sheet.originalName : (sheet.name.trim().length > 0 ? sheet.name.trim() : sheet.automaticName)])
        detail: Format.fill(S.tr2(S.rev, "remote.quota.claudeCli.confirm.message"),
                            [sheet.editing ? quotas.hostBridgePath(sheet.editingId) : "~/.claude/settings.json"])
        action: S.tr2(S.rev, "settings.quota.claudeCli.confirm.ok")
        onConfirmed: sheet.quotaBridge = true
    }
}
