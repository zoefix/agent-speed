import QtQuick
import QtQuick.Effects
import QtQuick.Layouts

// REPORTING-EXTENSIONS 5–6 in Settings → API: one personal-profile card — the avatar and the editable name together.
// Nothing is sent before an explicit action (upload, save, remove).
SettingsGroup {
    id: card
    title: S.tr2(S.rev, "profile.title")
    Item {
        Layout.fillWidth: true
        implicitHeight: profileRow.implicitHeight + 24
        RowLayout {
            id: profileRow
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.margins: 14
            anchors.verticalCenter: parent.verticalCenter
            spacing: 14
            // The circular avatar (the local preview until it is uploaded).
            Rectangle {
                Layout.alignment: Qt.AlignTop
                width: 64
                height: 64
                radius: 32
                color: Theme.field
                border.width: 1
                border.color: Theme.border
                clip: true
                visible: report.avatarSupported
                Image {
                    id: avatar
                    anchors.fill: parent
                    source: report.avatarSource
                    fillMode: Image.PreserveAspectCrop
                    smooth: true
                    visible: false
                    layer.enabled: true
                }
                Rectangle {
                    id: mask
                    anchors.fill: parent
                    radius: width / 2
                    visible: false
                    layer.enabled: true
                }
                MultiEffect {
                    anchors.fill: parent
                    source: avatar
                    maskEnabled: true
                    maskSource: mask
                    visible: report.avatarSource.length > 0
                }
                Glyph {
                    anchors.centerIn: parent
                    visible: report.avatarSource.length === 0
                    name: "orb"
                    width: 24
                    height: 24
                    color: Theme.ink3
                }
            }
            ColumnLayout {
                Layout.fillWidth: true
                spacing: 6
                RowLayout {
                    Layout.fillWidth: true
                    spacing: 10
                    // The typed name until it is saved: 儲存 commits it, as Return does.
                    property string draft: ""
                    readonly property bool changed: draft.length > 0 && draft !== report.profileName
                    Text {
                        text: report.profileBound ? S.tr2(S.rev, "profile.displayName") : S.tr2(S.rev, "report.username")
                        color: Theme.ink
                        font.pixelSize: 13
                    }
                    FieldInput {
                        id: nameField
                        Layout.fillWidth: true
                        text: report.profileName
                        accessibleName: report.profileBound ? S.tr2(S.rev, "profile.displayName") : S.tr2(S.rev, "report.username")
                        onEdited: (t) => parent.draft = t
                        onCommitted: (t) => { parent.draft = ""; report.saveProfileName(t) }
                    }
                    PillButton {
                        visible: parent.changed
                        text: S.tr2(S.rev, "profile.save")
                        onClicked: {
                            var name = parent.draft
                            parent.draft = ""
                            report.saveProfileName(name)
                        }
                    }
                }
                Text {
                    text: report.profileBound ? S.tr2(S.rev, "profile.identity.bound") : S.tr2(S.rev, "profile.identity.unbound")
                    color: Theme.ink3
                    font.pixelSize: 11
                }
                Flow {
                    Layout.fillWidth: true
                    spacing: 12
                    visible: report.avatarSupported
                    LinkButton {
                        text: report.avatarSource.length > 0 ? S.tr2(S.rev, "profile.avatar.change") : S.tr2(S.rev, "profile.avatar.choose")
                        onClicked: report.chooseAvatar()
                    }
                    LinkButton {
                        visible: report.avatarPending
                        text: S.tr2(S.rev, "profile.avatar.save")
                        onClicked: report.uploadAvatar()
                    }
                    LinkButton {
                        visible: report.avatarPending
                        text: S.tr2(S.rev, "history.cancel")
                        onClicked: report.cancelAvatar()
                    }
                    LinkButton {
                        visible: !report.avatarPending && report.avatarSource.length > 0
                        destructive: true
                        text: S.tr2(S.rev, "profile.avatar.remove")
                        onClicked: report.removeAvatar()
                    }
                }
                Text {
                    visible: report.avatarPending
                    text: S.tr2(S.rev, "profile.avatar.pending")
                    color: Theme.amber
                    font.pixelSize: 11
                }
                Text {
                    visible: report.avatarSupported
                    text: S.tr2(S.rev, "profile.avatar.note")
                    color: Theme.ink3
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                Text {
                    visible: report.profileError.length > 0
                    text: report.profileError
                    color: Theme.danger
                    font.pixelSize: 11
                    wrapMode: Text.Wrap
                    Layout.fillWidth: true
                }
                RowLayout {
                    spacing: 12
                    LinkButton {
                        text: S.tr2(S.rev, "profile.refresh")
                        enabled: !report.profileBusy && report.networkEnabled
                        onClicked: report.refreshProfile()
                    }
                    LinkButton {
                        visible: report.identitySupported
                        text: S.tr2(S.rev, "profile.manageDevices")
                        enabled: report.networkEnabled
                        onClicked: report.openDevices()
                    }
                }
            }
        }
    }
}
