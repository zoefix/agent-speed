import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 11 Settings → API: live reporting to a service the user picks.
SettingsPage {
    id: pane
    title: S.tr2(S.rev, "settings.report")
    property bool confirmingForget: false
    property bool othersOpen: false
    property string typedUrl
    readonly property color statusColor: report.statusLevel === "green" ? Theme.good
                                         : report.statusLevel === "amber" ? Theme.amber
                                         : report.statusLevel === "red" ? Theme.danger : Theme.ink3

    // 1. Master
    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "report.enabled")
            caption: report.networkEnabled ? S.tr2(S.rev, "report.enabled.hint") : S.tr2(S.rev, "settings.network.offHint")
            divider: false
            ToggleSwitch {
                label: S.tr2(S.rev, "report.enabled")
                enabled: report.networkEnabled || report.enabled
                checked: report.enabled
                onToggled: (on) => report.setEnabledByUser(on)
            }
        }
    }

    // 2. Service and 2b. Transport
    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "report.service")
            Dropdown {
                popupWidth: 240
                value: report.serviceIndex
                options: {
                    var list = []
                    for (var i = 0; i < report.presetNames.length; ++i) list.push({ value: i, label: report.presetNames[i] })
                    list.push({ value: report.presetNames.length, label: S.tr2(S.rev, "report.service.custom") })
                    return list
                }
                onActivated: (v) => report.chooseService(v)
            }
        }
        Item {
            visible: report.serviceIndex === report.presetNames.length
            Layout.fillWidth: true
            implicitHeight: urlColumn.implicitHeight + 20
            ColumnLayout {
                id: urlColumn
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 6
                Text {
                    text: S.tr2(S.rev, "report.url")
                    color: Theme.ink
                    font.pixelSize: 13
                }
                FieldInput {
                    id: urlField
                    Layout.fillWidth: true
                    text: report.customUrl
                    placeholder: S.tr2(S.rev, "report.url.placeholder")
                    accessibleName: S.tr2(S.rev, "report.url")
                    error: report.urlNoteLevel === "error"
                    onEdited: (t) => { pane.typedUrl = t; debounce.restart() }
                    onCommitted: (t) => { report.editCustomUrl(t); report.commitCustomUrl() }
                    Timer {
                        id: debounce
                        interval: 500
                        onTriggered: report.editCustomUrl(pane.typedUrl)
                    }
                }
                Text {
                    visible: report.urlNote.length > 0
                    text: report.urlNote
                    color: report.urlNoteLevel === "error" ? Theme.danger : Theme.amber
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
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
        // The service card (REPORTING 3): what the app knows before the first fetch, the document's details after it.
        Item {
            Layout.fillWidth: true
            implicitHeight: card.implicitHeight + 24
            ColumnLayout {
                id: card
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 4
                Text {
                    text: report.serviceName
                    color: Theme.ink
                    font.pixelSize: 13
                    font.weight: Font.DemiBold
                    Layout.fillWidth: true
                    elide: Text.ElideRight
                }
                TextEdit {
                    text: report.serviceHost
                    readOnly: true
                    selectByMouse: true
                    color: Theme.ink2
                    font.pixelSize: 12
                    Layout.fillWidth: true
                    wrapMode: TextEdit.Wrap
                }
                Column {
                    Layout.fillWidth: true
                    spacing: 4
                    opacity: report.serviceKnown ? 1 : 0
                    visible: report.serviceKnown
                    Behavior on opacity { NumberAnimation { duration: 200; easing.type: Easing.OutCubic } }
                    Text {
                        width: parent.width
                        visible: text.length > 0
                        text: report.serviceDescription
                        color: Theme.ink3
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                        maximumLineCount: 3
                        elide: Text.ElideRight
                    }
                    Text {
                        width: parent.width
                        visible: text.length > 0
                        text: report.serviceRetention
                        color: Theme.ink3
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                    }
                    Flow {
                        width: parent.width
                        spacing: 14
                        LinkButton {
                            visible: report.termsUrl.length > 0
                            text: S.tr2(S.rev, "report.service.terms")
                            onClicked: report.openLink(report.termsUrl)
                        }
                        LinkButton {
                            visible: report.privacyUrl.length > 0
                            text: S.tr2(S.rev, "report.service.privacy")
                            onClicked: report.openLink(report.privacyUrl)
                        }
                        LinkButton {
                            visible: report.leaderboardUrl.length > 0
                            text: S.tr2(S.rev, "report.service.leaderboard")
                            onClicked: report.openLink(report.leaderboardUrl)
                        }
                    }
                }
                RowLayout {
                    visible: report.checking
                    spacing: 8
                    BusyIndicator {
                        running: report.checking
                        implicitWidth: 16
                        implicitHeight: 16
                    }
                    Text {
                        text: S.tr2(S.rev, "report.service.checking")
                        color: Theme.ink2
                        font.pixelSize: 12
                    }
                }
                RowLayout {
                    visible: report.fetchMessage.length > 0 && !report.checking
                    Layout.fillWidth: true
                    spacing: 8
                    Glyph {
                        name: "alertCircle"
                        width: 14
                        height: 14
                        color: report.fetchLevel === "red" ? Theme.danger : Theme.amber
                    }
                    Text {
                        text: report.fetchMessage
                        color: Theme.ink
                        font.pixelSize: 12
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                    PillButton {
                        text: S.tr2(S.rev, "report.service.retry")
                        onClicked: report.retry()
                    }
                }
                RowLayout {
                    visible: report.needsReview
                    Layout.fillWidth: true
                    spacing: 8
                    Text {
                        text: S.tr2(S.rev, "report.service.changed")
                        color: Theme.ink
                        font.pixelSize: 12
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                    PillButton {
                        text: S.tr2(S.rev, "report.service.review")
                        onClicked: report.review()
                    }
                }
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
        SettingsRow {
            label: S.tr2(S.rev, "report.transport")
            caption: report.transportUnsupported ? "" : report.transportNote
            divider: false
            Segmented {
                value: report.webSocket ? "ws" : "http"
                options: [
                    { value: "http", label: S.tr2(S.rev, "report.transport.http") },
                    { value: "ws", label: S.tr2(S.rev, "report.transport.websocket") }
                ]
                onActivated: (v) => report.setWebSocket(v === "ws")
            }
        }
        RowLayout {
            visible: report.transportUnsupported || report.throttledNote.length > 0
            Layout.fillWidth: true
            Layout.leftMargin: 14
            Layout.rightMargin: 14
            Layout.bottomMargin: 12
            spacing: 6
            Glyph {
                visible: report.transportUnsupported
                name: "info"
                width: 13
                height: 13
                color: Theme.ink3
            }
            Text {
                text: report.transportUnsupported ? report.transportNote : report.throttledNote
                color: report.transportUnsupported ? Theme.ink3 : Theme.amber
                font.pixelSize: 11
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
        }
    }

    // The profile card (REPORTING-EXTENSIONS) when the service offers it.
    ReportProfileCard {
        visible: report.profileAvailable
    }

    // 3. Username and 4. API key
    SettingsGroup {
        Item {
            Layout.fillWidth: true
            implicitHeight: nameColumn.implicitHeight + 22
            ColumnLayout {
                id: nameColumn
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 6
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 10
                    Text {
                        text: S.tr2(S.rev, "report.username")
                        color: Theme.ink
                        font.pixelSize: 13
                    }
                    FieldInput {
                        Layout.fillWidth: true
                        text: report.username
                        accessibleName: S.tr2(S.rev, "report.username")
                        error: report.usernameError.length > 0 || report.usernameNoteError
                        onCommitted: (t) => report.commitUsername(t)
                    }
                    LinkButton {
                        glyph: "dice"
                        text: S.tr2(S.rev, "report.username.random")
                        onClicked: report.randomizeUsername()
                    }
                }
                Text {
                    text: S.tr2(S.rev, "report.username.public")
                    color: Theme.ink3
                    font.pixelSize: 11
                    lineHeight: 1.2
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                Text {
                    readonly property string message: report.usernameError.length > 0 ? report.usernameError : report.usernameNote
                    visible: message.length > 0
                    text: message
                    color: report.usernameError.length > 0 || report.usernameNoteError ? Theme.danger : Theme.ink2
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                ColumnLayout {
                    visible: report.transferOffered
                    spacing: 4
                    PillButton {
                        text: S.tr2(S.rev, "report.username.transfer")
                        onClicked: report.transferUsername()
                    }
                    Text {
                        text: S.tr2(S.rev, "report.username.transfer.hint")
                        color: Theme.ink3
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                }
            }
            Rectangle {
                visible: report.apiKeyVisible
                anchors.left: parent.left
                anchors.leftMargin: 14
                anchors.right: parent.right
                anchors.bottom: parent.bottom
                height: 1
                color: Theme.hairline
            }
        }
        Item {
            visible: report.apiKeyVisible
            Layout.fillWidth: true
            implicitHeight: keyColumn.implicitHeight + 22
            ColumnLayout {
                id: keyColumn
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 6
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 10
                    Text {
                        text: S.tr2(S.rev, "report.apiKey")
                        color: Theme.ink
                        font.pixelSize: 13
                    }
                    FieldInput {
                        id: keyField
                        Layout.fillWidth: true
                        secret: true
                        text: ""
                        placeholder: report.apiKeyMasked.length > 0 ? report.apiKeyMasked
                                     : report.apiKeyRequired ? "" : S.tr2(S.rev, "report.apiKey.optional")
                        accessibleName: S.tr2(S.rev, "report.apiKey")
                        onCommitted: (t) => { if (t.length > 0) report.saveApiKey(t); keyField.restore() }
                    }
                    LinkButton {
                        visible: report.apiKeyMasked.length > 0
                        text: S.tr2(S.rev, "report.apiKey.clear")
                        onClicked: report.clearApiKey()
                    }
                }
                Text {
                    text: (report.apiKeyRequired ? S.tr2(S.rev, "report.apiKey.required") + " " : "") + S.tr2(S.rev, "report.apiKey.hint")
                    color: Theme.ink3
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                LinkButton {
                    visible: report.apiKeyUrl.length > 0
                    text: S.tr2(S.rev, "report.apiKey.get")
                    onClicked: report.openLink(report.apiKeyUrl)
                }
            }
        }
    }

    // 5. What's sent
    SettingsGroup {
        title: S.tr2(S.rev, "report.sections")
        footnote: S.tr2(S.rev, "report.sections.hint")
        Repeater {
            model: report.sections
            SettingsRow {
                required property var modelData
                required property int index
                label: modelData.label
                caption: modelData.note
                enabled: modelData.enabled
                ToggleSwitch {
                    label: modelData.label
                    checked: modelData.on
                    onToggled: (on) => report.setSection(modelData.key, on)
                }
            }
        }
        Item {
            Layout.fillWidth: true
            implicitHeight: 44
            PillButton {
                anchors.left: parent.left
                anchors.leftMargin: 14
                anchors.verticalCenter: parent.verticalCenter
                text: S.tr2(S.rev, "report.preview")
                onClicked: report.openPreview()
            }
        }
    }

    // 6. Status
    SettingsGroup {
        title: S.tr2(S.rev, "report.status")
        Item {
            Layout.fillWidth: true
            implicitHeight: statusColumn.implicitHeight + 22
            ColumnLayout {
                id: statusColumn
                anchors.left: parent.left
                anchors.right: actions.left
                anchors.leftMargin: 14
                anchors.rightMargin: 10
                anchors.verticalCenter: parent.verticalCenter
                spacing: 3
                RowLayout {
                    spacing: 8
                    Layout.fillWidth: true
                    Rectangle {
                        width: 8
                        height: 8
                        radius: 4
                        color: pane.statusColor
                        Accessible.ignored: true
                    }
                    Text {
                        text: report.statusText + (report.rankText.length > 0 ? "  ·  " + report.rankText : "")
                        color: Theme.ink
                        font.pixelSize: 13
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                        Accessible.role: Accessible.StaticText
                        Accessible.name: text
                    }
                }
                Text {
                    id: wsStatsLine
                    visible: report.wsStats.length > 0
                    text: report.wsStats
                    color: Theme.ink3
                    font.pixelSize: 11
                    Layout.leftMargin: 16
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                    ToolTip.visible: statsHover.hovered
                    ToolTip.text: report.wsCounters
                    HoverHandler { id: statsHover }
                }
                Text {
                    visible: report.statusFootnote.length > 0
                    text: report.statusFootnote
                    color: Theme.ink3
                    font.pixelSize: 11
                    Layout.leftMargin: 16
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
            }
            Row {
                id: actions
                anchors.right: parent.right
                anchors.rightMargin: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 8
                PillButton {
                    visible: report.showRetry
                    text: S.tr2(S.rev, "report.service.retry")
                    onClicked: report.retry()
                }
                PillButton {
                    visible: report.showReview
                    text: S.tr2(S.rev, "report.service.review")
                    onClicked: report.review()
                }
                PillButton {
                    visible: report.canPause && !report.paused
                    text: S.tr2(S.rev, "report.pause")
                    onClicked: pauseMenu.popup()
                    Menu {
                        id: pauseMenu
                        MenuItem {
                            text: S.tr2(S.rev, "report.pause.hour")
                            onTriggered: report.pause(1)
                        }
                        MenuItem {
                            text: S.tr2(S.rev, "report.pause.indefinite")
                            onTriggered: report.pause(0)
                        }
                    }
                }
                PillButton {
                    visible: report.canPause && report.paused
                    text: S.tr2(S.rev, "report.resume")
                    onClicked: report.resume()
                }
            }
        }
    }

    // 7. This device
    SettingsGroup {
        visible: report.deviceVisible || report.forgetMessage.length > 0
        title: S.tr2(S.rev, "report.device")
        Item {
            Layout.fillWidth: true
            implicitHeight: deviceColumn.implicitHeight + 22
            ColumnLayout {
                id: deviceColumn
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 6
                RowLayout {
                    visible: report.deviceVisible
                    Layout.fillWidth: true
                    Text {
                        text: report.deviceDetail
                        color: Theme.ink
                        font.pixelSize: 13
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                    LinkButton {
                        visible: report.profileUrl.length > 0
                        text: S.tr2(S.rev, "report.device.profile")
                        onClicked: report.openLink(report.profileUrl)
                    }
                }
                RowLayout {
                    visible: report.deviceVisible
                    spacing: 8
                    Text {
                        text: S.tr2(S.rev, "report.device.fingerprint")
                        color: Theme.ink2
                        font.pixelSize: 12
                    }
                    TextEdit {
                        text: report.fingerprint
                        readOnly: true
                        selectByMouse: true
                        color: Theme.ink
                        font.family: "monospace"
                        font.pixelSize: 12
                    }
                }
                Text {
                    visible: report.forgetMessage.length > 0
                    text: report.forgetMessage
                    color: report.forgetState === "failed" ? Theme.danger : Theme.ink2
                    font.pixelSize: 12
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                ColumnLayout {
                    visible: report.forgetState === "failed"
                    spacing: 4
                    PillButton {
                        text: S.tr2(S.rev, "report.forget.localOnly")
                        onClicked: report.forgetLocalOnly()
                    }
                    Text {
                        text: S.tr2(S.rev, "report.forget.localOnly.detail")
                        color: Theme.ink3
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                    LinkButton {
                        visible: report.privacyUrl.length > 0
                        text: S.tr2(S.rev, "report.service.privacy")
                        onClicked: report.openLink(report.privacyUrl)
                    }
                }
                ColumnLayout {
                    visible: report.resetVisible
                    spacing: 4
                    PillButton {
                        text: S.tr2(S.rev, "report.reset")
                        onClicked: report.resetDevice()
                    }
                    Text {
                        text: S.tr2(S.rev, "report.reset.detail")
                        color: Theme.ink3
                        font.pixelSize: 11
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                }
                LinkButton {
                    visible: report.deviceVisible && !pane.confirmingForget && report.forgetState !== "working"
                    destructive: true
                    text: S.tr2(S.rev, "report.forget")
                    onClicked: pane.confirmingForget = true
                }
                // The confirmation of report.forget (destructive default report.forget.action, history.cancel).
                Rectangle {
                    visible: pane.confirmingForget
                    Layout.fillWidth: true
                    implicitHeight: confirmColumn.implicitHeight + 20
                    radius: 8
                    color: Theme.field
                    ColumnLayout {
                        id: confirmColumn
                        anchors.fill: parent
                        anchors.margins: 10
                        spacing: 6
                        Text {
                            text: report.forgetConfirm
                            color: Theme.ink
                            font.pixelSize: 13
                            font.weight: Font.DemiBold
                            wrapMode: Text.Wrap
                            Layout.fillWidth: true
                        }
                        Text {
                            text: S.tr2(S.rev, "report.forget.detail")
                            color: Theme.ink2
                            font.pixelSize: 12
                            wrapMode: Text.Wrap
                            Layout.fillWidth: true
                        }
                        RowLayout {
                            Layout.alignment: Qt.AlignRight
                            spacing: 8
                            PillButton {
                                text: S.tr2(S.rev, "history.cancel")
                                onClicked: pane.confirmingForget = false
                            }
                            PillButton {
                                accent: true
                                text: S.tr2(S.rev, "report.forget.action")
                                onClicked: {
                                    pane.confirmingForget = false
                                    report.forget()
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    // 8. Other services
    SettingsGroup {
        visible: report.others.length > 0
        Item {
            Layout.fillWidth: true
            implicitHeight: 40
            activeFocusOnTab: true
            Accessible.role: Accessible.Button
            Accessible.name: S.tr2(S.rev, "report.others")
            Keys.onSpacePressed: pane.othersOpen = !pane.othersOpen
            Glyph {
                id: disclosure
                x: 14
                anchors.verticalCenter: parent.verticalCenter
                name: "chevron"
                width: 12
                height: 12
                rotation: pane.othersOpen ? 0 : -90
                Behavior on rotation { NumberAnimation { duration: 160 } }
            }
            Text {
                anchors.left: disclosure.right
                anchors.leftMargin: 8
                anchors.verticalCenter: parent.verticalCenter
                text: S.tr2(S.rev, "report.others")
                color: Theme.ink
                font.pixelSize: 13
            }
            MouseArea {
                anchors.fill: parent
                cursorShape: Qt.PointingHandCursor
                onClicked: pane.othersOpen = !pane.othersOpen
            }
        }
        Repeater {
            model: pane.othersOpen ? report.others : []
            SettingsRow {
                required property var modelData
                label: modelData.host
                caption: modelData.username
                LinkButton {
                    destructive: true
                    text: S.tr2(S.rev, "report.forget")
                    onClicked: report.forgetOther(modelData.origin)
                }
            }
        }
    }
}
