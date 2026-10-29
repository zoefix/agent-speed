import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// REPORTING-EXTENSIONS 6: the separate device management surface — the shared identity, unlinking, one-time pairing
// and the linked devices. A code never links a device until the inviting device approves it.
Window {
    id: win
    width: 480
    height: 560
    minimumWidth: 420
    minimumHeight: 420
    title: S.tr2(S.rev, "profile.deviceManagement")
    color: Theme.panel
    onVisibleChanged: if (!visible) report.closeDevices()

    Shortcut {
        sequences: [StandardKey.Close, "Esc"]
        onActivated: win.close()
    }

    Flickable {
        anchors.fill: parent
        contentHeight: body.implicitHeight + 48
        clip: true
        boundsBehavior: Flickable.StopAtBounds
        ColumnLayout {
            id: body
            x: 22
            y: 22
            width: win.width - 44
            spacing: 18
            Text {
                text: S.tr2(S.rev, "profile.deviceManagement")
                color: Theme.ink
                font.pixelSize: 17
                font.weight: Font.DemiBold
            }
            // The shared identity.
            SettingsGroup {
                Item {
                    Layout.fillWidth: true
                    implicitHeight: identityColumn.implicitHeight + 22
                    ColumnLayout {
                        id: identityColumn
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.margins: 14
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 6
                        Text {
                            text: report.profileBound ? S.tr2(S.rev, "profile.identity.bound") : S.tr2(S.rev, "profile.identity.unbound")
                            color: Theme.ink
                            font.pixelSize: 13
                            font.weight: Font.Medium
                        }
                        PillButton {
                            visible: !report.profileBound
                            text: S.tr2(S.rev, "profile.identity.create")
                            enabled: !report.profileBusy
                            onClicked: report.createIdentity()
                        }
                        LinkButton {
                            visible: report.profileBound
                            destructive: true
                            text: S.tr2(S.rev, "profile.identity.unlink")
                            enabled: !report.profileBusy
                            onClicked: report.unlinkDevice()
                        }
                        Text {
                            visible: report.profileBound
                            text: S.tr2(S.rev, "profile.identity.unlinkNote")
                            color: Theme.ink3
                            font.pixelSize: 11
                            wrapMode: Text.Wrap
                            Layout.fillWidth: true
                        }
                    }
                }
            }
            // Pairing: invite from a linked device, join from a new one.
            SettingsGroup {
                Item {
                    Layout.fillWidth: true
                    implicitHeight: pairingColumn.implicitHeight + 22
                    ColumnLayout {
                        id: pairingColumn
                        anchors.left: parent.left
                        anchors.right: parent.right
                        anchors.margins: 14
                        anchors.verticalCenter: parent.verticalCenter
                        spacing: 8
                        RowLayout {
                            visible: report.pairingState === "" || report.pairingState === "approved" || report.pairingState === "cancelled" || report.pairingState === "expired"
                            spacing: 8
                            PillButton {
                                visible: report.profileBound
                                text: S.tr2(S.rev, "profile.pairing.invite")
                                enabled: !report.profileBusy
                                onClicked: report.invitePairing()
                            }
                        }
                        ColumnLayout {
                            visible: !report.profileBound && (report.pairingState === "" || report.pairingState === "cancelled" || report.pairingState === "expired")
                            spacing: 6
                            Layout.fillWidth: true
                            Text {
                                text: S.tr2(S.rev, "profile.pairing.join")
                                color: Theme.ink
                                font.pixelSize: 13
                            }
                            RowLayout {
                                Layout.fillWidth: true
                                spacing: 8
                                FieldInput {
                                    id: codeField
                                    Layout.fillWidth: true
                                    monospace: true
                                    placeholder: "XXXX-XXXX-XXXX"
                                    accessibleName: S.tr2(S.rev, "profile.pairing.code")
                                    property string typed
                                    onEdited: (t) => typed = t
                                    onCommitted: (t) => typed = t
                                }
                                PillButton {
                                    text: S.tr2(S.rev, "profile.pairing.join")
                                    enabled: !report.profileBusy && codeField.typed.length >= 12
                                    onClicked: report.joinPairing(codeField.typed)
                                }
                            }
                            Text {
                                text: S.tr2(S.rev, "profile.pairing.note")
                                color: Theme.ink3
                                font.pixelSize: 11
                                wrapMode: Text.Wrap
                                Layout.fillWidth: true
                            }
                        }
                        // An invitation: the code only in memory, the claimant to approve with its platform and fingerprint.
                        ColumnLayout {
                            visible: report.pairingInviter && report.pairingCode.length > 0 && (report.pairingState === "waiting" || report.pairingState === "pending")
                            spacing: 4
                            Text {
                                text: S.tr2(S.rev, "profile.pairing.code")
                                color: Theme.ink2
                                font.pixelSize: 12
                            }
                            TextEdit {
                                text: report.pairingCode
                                readOnly: true
                                selectByMouse: true
                                color: Theme.ink
                                font.family: "monospace"
                                font.pixelSize: 22
                                font.letterSpacing: 2
                            }
                        }
                        Text {
                            visible: report.pairingState.length > 0
                            text: report.pairingState === "waiting" ? S.tr2(S.rev, "profile.pairing.waiting")
                                  : report.pairingState === "pending" ? (report.pairingInviter ? report.pairingClaimant : S.tr2(S.rev, "profile.pairing.pending"))
                                  : report.pairingState === "approved" ? S.tr2(S.rev, "profile.pairing.approved")
                                  : S.tr2(S.rev, "profile.pairing.expired")
                            color: report.pairingState === "approved" ? Theme.good : Theme.ink2
                            font.pixelSize: 12
                            wrapMode: Text.Wrap
                            Layout.fillWidth: true
                        }
                        RowLayout {
                            visible: report.pairingState === "waiting" || report.pairingState === "pending"
                            spacing: 8
                            PillButton {
                                visible: report.pairingInviter && report.pairingState === "pending"
                                accent: true
                                text: S.tr2(S.rev, "profile.pairing.approve")
                                onClicked: report.approvePairing()
                            }
                            PillButton {
                                text: S.tr2(S.rev, "profile.pairing.check")
                                onClicked: report.checkPairing()
                            }
                            LinkButton {
                                destructive: true
                                text: S.tr2(S.rev, "profile.pairing.cancel")
                                onClicked: report.cancelPairing()
                            }
                        }
                    }
                }
            }
            // The linked devices.
            SettingsGroup {
                title: S.tr2(S.rev, "profile.devices")
                visible: report.profileDevices.length > 0
                Repeater {
                    model: report.profileDevices
                    SettingsRow {
                        required property var modelData
                        required property int index
                        label: modelData.platform + (modelData.current ? "  ·  " + S.tr2(S.rev, "report.device") : "")
                        caption: modelData.fingerprint
                        divider: index < report.profileDevices.length - 1
                        Rectangle {
                            width: 8
                            height: 8
                            radius: 4
                            color: modelData.online ? Theme.good : Theme.ink3
                        }
                    }
                }
            }
            Text {
                visible: report.profileError.length > 0
                text: report.profileError
                color: Theme.danger
                font.pixelSize: 12
                wrapMode: Text.Wrap
                Layout.fillWidth: true
            }
            Text {
                visible: !report.identitySupported
                text: S.tr2(S.rev, "profile.unavailable")
                color: Theme.ink3
                font.pixelSize: 12
            }
        }
    }
}
