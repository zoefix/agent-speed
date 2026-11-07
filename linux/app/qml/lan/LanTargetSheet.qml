import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 14 Add / Edit sheet (420), laid out as the host sheet: the method (UDP / HTTP) with its caption, the address
// checked as typed (0.3 s), the name, the key, and Send Test with its result inline. Save stays disabled while the
// address is invalid; editing a field clears the last test.
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
    property string originalTitle: ""
    property string kind: "udp"
    property string address: ""
    property string name: ""
    property string key: ""
    // The address error shows 0.3 s after typing stops (and at once when the field is left or saved).
    property string shownError: ""
    property bool addressEdited: false
    // idle | running | done
    property string testState: "idle"
    property var testResult: ({})
    property int testToken: 0
    readonly property bool editing: editingId.length > 0
    readonly property string addressError: S.rev >= 0 ? lan.addressError(kind, address) : ""
    readonly property bool valid: addressError.length === 0
    readonly property bool broadcastNote: S.rev >= 0 && valid && lan.broadcast(kind, address)
    readonly property string testBlocker: !lan.networkEnabled ? S.tr2(S.rev, "lan.target.status.networkOff") : ""
    readonly property string title: editing ? Format.fill(S.tr2(S.rev, "lan.sheet.edit"), [originalTitle]) : S.tr2(S.rev, "lan.sheet.add")
    readonly property real formRoom: Math.max(160, (parent ? parent.height : 600) - 48 - headerBlock.implicitHeight - 40 - buttonsBlock.implicitHeight - 33)
    readonly property real labelColumn: Math.min(120, Math.ceil(Math.max(kindLabel.advanceWidth, addressLabel.advanceWidth, nameLabel.advanceWidth,
                                                                       keyLabel.advanceWidth)) + 1)
    TextMetrics { id: kindLabel; font.pixelSize: 13; text: S.tr2(S.rev, "lan.target.kind") }
    TextMetrics { id: addressLabel; font.pixelSize: 13; text: S.tr2(S.rev, "lan.target.address") }
    TextMetrics { id: nameLabel; font.pixelSize: 13; text: S.tr2(S.rev, "lan.target.name") }
    TextMetrics { id: keyLabel; font.pixelSize: 13; text: S.tr2(S.rev, "lan.target.key") }

    function openAdd(kindValue) {
        editingId = ""
        originalTitle = ""
        load(kindValue || "udp", "", "", "")
        addressEdited = false
        open()
        addressField.focusField()
    }
    function openEdit(id) {
        const t = lan.target(id)
        if (!t.targetId) return
        editingId = id
        originalTitle = t.title
        load(t.kind, t.address, t.name, t.key)
        addressEdited = true
        open()
    }
    function load(k, a, n, keyText) {
        kind = k
        address = a
        name = n
        key = keyText
        resetTest()
        shownError = ""
        for (const pair of [[addressField, a], [nameField, n], [keyField, keyText]]) {
            pair[0].text = pair[1]
            pair[0].restore()
        }
    }
    // Development aid: fields as if typed.
    function typeAddress(text) { addressField.type(text) }
    function typeName(text) { nameField.type(text) }
    function typeKey(text) { keyField.type(text) }
    function showError() { shownError = addressEdited ? addressError : "" }
    function resetTest() {
        testState = "idle"
        testResult = ({})
    }
    function runTest() {
        if (testBlocker.length > 0 || !valid || testState === "running") return
        testState = "running"
        testToken = lan.test(kind, address, key)
    }
    function save() {
        showError()
        if (!valid) return
        if (editing) {
            if (!lan.updateTarget(editingId, kind, address, name, key)) return
        } else if (!lan.canAdd || lan.addTarget(kind, address, name, key).length === 0) {
            return
        }
        close()
    }
    onClosed: resetTest()
    onKindChanged: if (addressEdited) debounce.restart()

    Timer {
        id: debounce
        interval: 300
        onTriggered: sheet.showError()
    }
    Connections {
        target: lan
        function onTestFinished(token, result) {
            if (token !== sheet.testToken || sheet.testState !== "running") return
            sheet.testResult = result
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

    component FieldLabel: Text {
        Layout.alignment: Qt.AlignTop
        Layout.topMargin: 7
        Layout.preferredWidth: sheet.labelColumn
        color: Theme.ink
        font.pixelSize: 13
        wrapMode: Text.Wrap
    }
    component Caption: Text {
        Layout.fillWidth: true
        color: Theme.ink3
        font.pixelSize: 11
        lineHeight: 1.25
        wrapMode: Text.Wrap
    }

    contentItem: ColumnLayout {
        spacing: 0
        Accessible.role: Accessible.Dialog
        Accessible.name: sheet.title

        // Header: the accent tile, the title and what the sheet is for.
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
                        name: "broadcast"
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
                text: S.tr2(S.rev, "lan.sheet.body")
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
                Rectangle {
                    Layout.fillWidth: true
                    Layout.leftMargin: 24
                    Layout.rightMargin: 24
                    implicitHeight: fields.implicitHeight + 28
                    radius: 10
                    color: Theme.card
                    border.width: 1
                    border.color: Theme.border
                    GridLayout {
                        id: fields
                        x: 14
                        y: 14
                        width: parent.width - 28
                        columns: 2
                        columnSpacing: 14
                        rowSpacing: 12

                        FieldLabel { text: S.tr2(S.rev, "lan.target.kind"); Layout.topMargin: 5 }
                        ColumnLayout {
                            Layout.fillWidth: true
                            spacing: 6
                            Segmented {
                                fixedWidth: 140
                                value: sheet.kind
                                Accessible.name: S.tr2(S.rev, "lan.target.kind")
                                options: [{ value: "udp", label: "UDP" }, { value: "http", label: "HTTP" }]
                                onActivated: (v) => {
                                    sheet.kind = v
                                    sheet.resetTest()
                                }
                            }
                            Caption { text: S.tr2(S.rev, sheet.kind === "udp" ? "lan.target.kind.udp.hint" : "lan.target.kind.http.hint") }
                        }

                        FieldLabel { text: S.tr2(S.rev, "lan.target.address") }
                        ColumnLayout {
                            Layout.fillWidth: true
                            spacing: 5
                            FieldInput {
                                id: addressField
                                Layout.fillWidth: true
                                passEscape: true
                                monospace: true
                                placeholder: S.tr2(S.rev, sheet.kind === "udp" ? "lan.target.address.placeholder.udp" : "lan.target.address.placeholder.http")
                                accessibleName: S.tr2(S.rev, "lan.target.address")
                                accessibleDescription: sheet.shownError
                                error: sheet.shownError.length > 0
                                onEdited: (t) => {
                                    sheet.address = t
                                    sheet.addressEdited = true
                                    sheet.resetTest()
                                    if (sheet.valid) sheet.shownError = ""
                                    debounce.restart()
                                }
                                onCommitted: sheet.showError()
                                onReturnPressed: (t) => {
                                    sheet.address = t
                                    sheet.save()
                                }
                            }
                            FieldError {
                                Layout.fillWidth: true
                                text: sheet.shownError
                            }
                            RowLayout {
                                visible: sheet.broadcastNote
                                Layout.fillWidth: true
                                spacing: 5
                                Glyph {
                                    Layout.alignment: Qt.AlignTop
                                    Layout.topMargin: 1
                                    Layout.preferredWidth: 11
                                    Layout.preferredHeight: 11
                                    name: "info"
                                    color: Theme.accent
                                }
                                Text {
                                    Layout.fillWidth: true
                                    text: S.tr2(S.rev, "lan.target.broadcast")
                                    color: Theme.ink2
                                    font.pixelSize: 11
                                    lineHeight: 1.2
                                    wrapMode: Text.Wrap
                                }
                            }
                        }

                        FieldLabel { text: S.tr2(S.rev, "lan.target.name") }
                        FieldInput {
                            id: nameField
                            Layout.fillWidth: true
                            passEscape: true
                            maximumLength: 64
                            placeholder: S.tr2(S.rev, "lan.target.name.placeholder")
                            accessibleName: S.tr2(S.rev, "lan.target.name")
                            onEdited: (t) => sheet.name = t
                            onReturnPressed: (t) => {
                                sheet.name = t
                                sheet.save()
                            }
                        }

                        FieldLabel { text: S.tr2(S.rev, "lan.target.key") }
                        ColumnLayout {
                            Layout.fillWidth: true
                            spacing: 6
                            FieldInput {
                                id: keyField
                                Layout.fillWidth: true
                                passEscape: true
                                maximumLength: 128
                                placeholder: S.tr2(S.rev, "lan.target.name.placeholder")
                                accessibleName: S.tr2(S.rev, "lan.target.key")
                                accessibleDescription: S.tr2(S.rev, "lan.target.key.hint")
                                onEdited: (t) => {
                                    sheet.key = t
                                    sheet.resetTest()
                                }
                                onReturnPressed: (t) => {
                                    sheet.key = t
                                    sheet.save()
                                }
                            }
                            Caption { text: S.tr2(S.rev, "lan.target.key.hint") }
                        }
                    }
                }

                // Send Test and its result.
                RowLayout {
                    Layout.fillWidth: true
                    Layout.leftMargin: 24
                    Layout.rightMargin: 24
                    Layout.topMargin: 14
                    Layout.bottomMargin: 20
                    spacing: 12
                    PillButton {
                        Layout.alignment: Qt.AlignTop
                        text: S.tr2(S.rev, "lan.target.test")
                        enabled: sheet.testBlocker.length === 0 && sheet.valid && sheet.testState !== "running"
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
                                   : sheet.testResult.dot === "attention" ? Theme.danger : (Theme.dark ? "#FB923C" : "#E86A0C")
                            RotationAnimator on rotation {
                                running: sheet.testState === "running" && sheet.visible
                                from: 0
                                to: 360
                                duration: 1100
                                loops: Animation.Infinite
                                onRunningChanged: if (!running) testMark.rotation = 0
                            }
                        }
                        Text {
                            Layout.fillWidth: true
                            text: sheet.testState === "running" ? S.tr2(S.rev, "remote.test.running") : (sheet.testResult.text || "")
                            color: Theme.ink2
                            font.pixelSize: 12
                            wrapMode: Text.Wrap
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
                accent: true
                text: S.tr2(S.rev, sheet.editing ? "remote.save" : "remote.addButton")
                enabled: sheet.valid && (sheet.editing || lan.canAdd)
                onClicked: sheet.save()
            }
        }
    }
}
