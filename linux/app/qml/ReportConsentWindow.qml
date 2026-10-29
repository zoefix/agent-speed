import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 11 Consent sheet: nothing is sent before Start Reporting; only the discovery document has been fetched.
Window {
    id: win
    width: 520
    height: Math.min(Screen.desktopAvailableHeight - 80, body.implicitHeight + 48)
    minimumWidth: 460
    title: report.consentTitle
    color: Theme.panel
    modality: Qt.ApplicationModal
    flags: Qt.Dialog
    property bool previewOpen: true

    onVisibleChanged: {
        report.watchPreview(visible)
        if (!visible && report.consentOpen) report.cancelConsent()
    }
    Connections {
        target: report
        function onChanged() { if (!report.consentOpen && win.visible) win.close() }
    }
    Component.onCompleted: if (visible) report.watchPreview(true)

    Shortcut {
        sequence: "Esc"
        onActivated: win.close()
    }
    Shortcut {
        sequences: ["Return", "Enter"]
        enabled: report.consentReady
        onActivated: report.startReporting()
    }

    Flickable {
        anchors.fill: parent
        contentHeight: body.implicitHeight + 48
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        ColumnLayout {
            id: body
            x: 24
            y: 24
            width: win.width - 48
            spacing: 14

            Text {
                text: report.consentTitle
                color: Theme.ink
                font.pixelSize: 17
                font.weight: Font.DemiBold
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
            Text {
                text: report.consentBody
                color: Theme.ink2
                font.pixelSize: 13
                lineHeight: 1.2
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
            // The service card.
            Rectangle {
                Layout.fillWidth: true
                implicitHeight: serviceColumn.implicitHeight + 24
                radius: 10
                color: Theme.card
                border.width: 1
                border.color: Theme.border
                ColumnLayout {
                    id: serviceColumn
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.margins: 12
                    anchors.verticalCenter: parent.verticalCenter
                    spacing: 4
                    Text {
                        text: report.serviceName
                        color: Theme.ink
                        font.pixelSize: 13
                        font.weight: Font.DemiBold
                    }
                    Text {
                        text: report.serviceHost
                        color: Theme.ink2
                        font.pixelSize: 12
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                    Text {
                        visible: text.length > 0
                        text: report.serviceDescription
                        color: Theme.ink3
                        font.pixelSize: 11
                        maximumLineCount: 3
                        elide: Text.ElideRight
                        wrapMode: Text.Wrap
                        Layout.fillWidth: true
                    }
                    Text {
                        visible: text.length > 0
                        text: report.serviceRetention
                        color: Theme.ink3
                        font.pixelSize: 11
                    }
                    Flow {
                        Layout.fillWidth: true
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
                }
            }
            // Username.
            ColumnLayout {
                Layout.fillWidth: true
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
                        text: report.consentUsername
                        accessibleName: S.tr2(S.rev, "report.username")
                        error: report.consentUsernameError.length > 0
                        onEdited: (t) => report.setConsentUsername(t)
                        onCommitted: (t) => report.setConsentUsername(t)
                    }
                    LinkButton {
                        glyph: "dice"
                        text: S.tr2(S.rev, "report.username.random")
                        onClicked: report.randomizeConsentUsername()
                    }
                }
                Text {
                    text: S.tr2(S.rev, "report.username.public")
                    color: Theme.ink3
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                Text {
                    visible: report.consentUsernameError.length > 0
                    text: report.consentUsernameError
                    color: Theme.danger
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                ColumnLayout {
                    visible: report.consentTransfer
                    spacing: 4
                    PillButton {
                        text: S.tr2(S.rev, "report.username.transfer")
                        onClicked: report.transferConsent()
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
            // API key, when the service uses keys.
            ColumnLayout {
                visible: report.consentApiKeyVisible
                Layout.fillWidth: true
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
                        Layout.fillWidth: true
                        secret: true
                        placeholder: report.consentApiKeyRequired ? "" : S.tr2(S.rev, "report.apiKey.optional")
                        accessibleName: S.tr2(S.rev, "report.apiKey")
                        error: report.consentApiKeyError.length > 0
                        onEdited: (t) => report.setConsentApiKey(t)
                        onCommitted: (t) => report.setConsentApiKey(t)
                    }
                }
                Text {
                    text: report.consentApiKeyError.length > 0 ? report.consentApiKeyError
                          : (report.consentApiKeyRequired ? S.tr2(S.rev, "report.apiKey.required") + " " : "") + S.tr2(S.rev, "report.apiKey.hint")
                    color: report.consentApiKeyError.length > 0 ? Theme.danger : Theme.ink3
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
            }
            // Sections.
            Rectangle {
                Layout.fillWidth: true
                implicitHeight: sectionColumn.implicitHeight
                radius: 10
                color: Theme.card
                border.width: 1
                border.color: Theme.border
                ColumnLayout {
                    id: sectionColumn
                    width: parent.width
                    spacing: 0
                    Repeater {
                        model: report.consentSections
                        SettingsRow {
                            required property var modelData
                            required property int index
                            label: modelData.label
                            caption: modelData.note
                            enabled: modelData.enabled
                            divider: index < report.consentSections.length - 1
                            ToggleSwitch {
                                label: modelData.label
                                checked: modelData.on
                                onToggled: (on) => report.setConsentSection(modelData.key, on)
                            }
                        }
                    }
                }
            }
            // The live preview, expanded.
            Item {
                Layout.fillWidth: true
                implicitHeight: 22
                activeFocusOnTab: true
                Accessible.role: Accessible.Button
                Accessible.name: S.tr2(S.rev, "report.preview.title")
                Keys.onSpacePressed: win.previewOpen = !win.previewOpen
                Glyph {
                    id: chevron
                    anchors.verticalCenter: parent.verticalCenter
                    name: "chevron"
                    width: 12
                    height: 12
                    rotation: win.previewOpen ? 0 : -90
                    Behavior on rotation { NumberAnimation { duration: 160 } }
                }
                Text {
                    anchors.left: chevron.right
                    anchors.leftMargin: 6
                    anchors.verticalCenter: parent.verticalCenter
                    text: S.tr2(S.rev, "report.preview.title")
                    color: Theme.ink
                    font.pixelSize: 13
                    font.weight: Font.Medium
                }
                LinkButton {
                    anchors.right: parent.right
                    anchors.verticalCenter: parent.verticalCenter
                    text: S.tr2(S.rev, "report.preview.copy")
                    onClicked: report.copyPreview()
                }
                MouseArea {
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.rightMargin: 120
                    height: parent.height
                    cursorShape: Qt.PointingHandCursor
                    onClicked: win.previewOpen = !win.previewOpen
                }
            }
            ReportPreviewText {
                visible: win.previewOpen
                Layout.fillWidth: true
                Layout.preferredHeight: 220
            }
            Text {
                visible: win.previewOpen
                text: report.previewHint
                color: Theme.ink3
                font.pixelSize: 11
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
            Text {
                text: S.tr2(S.rev, "report.consent.never")
                color: Theme.ink3
                font.pixelSize: 11
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
            Text {
                text: S.tr2(S.rev, "report.consent.honesty")
                color: Theme.ink3
                font.pixelSize: 11
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
            RowLayout {
                Layout.fillWidth: true
                spacing: 8
                RowLayout {
                    visible: report.consentStarting
                    spacing: 6
                    BusyIndicator {
                        running: report.consentStarting
                        implicitWidth: 16
                        implicitHeight: 16
                    }
                    Text {
                        text: S.tr2(S.rev, "report.status.registering")
                        color: Theme.ink2
                        font.pixelSize: 12
                    }
                }
                Text {
                    visible: report.consentFailure.length > 0 && !report.consentStarting
                    text: report.consentFailure
                    color: Theme.danger
                    font.pixelSize: 12
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                Item { Layout.fillWidth: true }
                PillButton {
                    text: S.tr2(S.rev, "history.cancel")
                    onClicked: win.close()
                }
                PillButton {
                    accent: true
                    enabled: report.consentReady
                    text: S.tr2(S.rev, "report.start")
                    onClicked: report.startReporting()
                }
            }
        }
    }
}
