import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// DESIGN 5 `characters.title` (only for a `character` orb, under the style cards; CHARACTERS 2, 7): the shown
// character (Auto or an installed one), the automatic download's progress with Stop while it runs, then every
// character of the catalog and every installed one with its portrait, name, artist and size, and Download / Update /
// Remove. Showing the section refreshes the catalog (at most every 10 minutes).
SettingsGroup {
    id: section
    property var orb: null
    title: S.tr2(S.rev, "characters.title")
    Component.onCompleted: characters.refreshForList()
    onVisibleChanged: if (visible) characters.refreshForList()

    SettingsRow {
        label: S.tr2(S.rev, "characters.choice")
        caption: S.tr2(S.rev, "characters.choice.hint")
        Dropdown {
            popupWidth: 240
            value: section.orb ? section.orb.character : ""
            options: characters.choices
            onActivated: (v) => { if (section.orb) orbs.setCharacter(section.orb.id, v) }
        }
    }

    // The automatic download of every character.
    Item {
        visible: characters.autoTotal > 0
        Layout.fillWidth: true
        implicitHeight: visible ? 44 : 0
        Accessible.role: Accessible.ProgressBar
        Accessible.name: characters.autoStatus
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 14
            anchors.rightMargin: 14
            spacing: 10
            ProgressBar {
                Layout.preferredWidth: 96
                from: 0
                to: Math.max(1, characters.autoTotal)
                value: Math.max(0, characters.autoPosition - 1)
                Behavior on value { NumberAnimation { duration: 240; easing.type: Easing.OutCubic } }
            }
            Text {
                Layout.fillWidth: true
                text: characters.autoStatus
                color: Theme.ink2
                font.pixelSize: 12
                font.features: Theme.tabular
                elide: Text.ElideRight
            }
            PillButton {
                text: S.tr2(S.rev, "characters.auto.stop")
                onClicked: characters.stopAutomatic()
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

    Repeater {
        model: characters.rows
        delegate: CharacterRow {
            required property var modelData
            required property int index
            row: modelData
            divider: index < characters.rows.length - 1
        }
    }

    // No rows: loading, unavailable with Retry, or offline.
    Item {
        visible: characters.rows.length === 0
        Layout.fillWidth: true
        implicitHeight: visible ? 52 : 0
        RowLayout {
            anchors.fill: parent
            anchors.leftMargin: 14
            anchors.rightMargin: 14
            spacing: 8
            BusyIndicator {
                visible: characters.catalogState === "loading"
                running: visible
                Layout.preferredWidth: 20
                Layout.preferredHeight: 20
            }
            Text {
                Layout.fillWidth: true
                text: characters.catalogState === "loading" ? S.tr2(S.rev, "characters.loading")
                    : characters.networkEnabled ? S.tr2(S.rev, "characters.unavailable") : S.tr2(S.rev, "characters.offline")
                color: Theme.ink2
                font.pixelSize: 12
                wrapMode: Text.Wrap
            }
            PillButton {
                visible: characters.catalogState !== "loading" && characters.networkEnabled
                text: S.tr2(S.rev, "characters.retry")
                onClicked: characters.retry()
            }
        }
    }

    // Footer: where packs come from, and the folder they live in.
    Item {
        Layout.fillWidth: true
        implicitHeight: footer.implicitHeight + 18
        RowLayout {
            id: footer
            anchors.left: parent.left
            anchors.right: parent.right
            anchors.leftMargin: 14
            anchors.rightMargin: 14
            anchors.verticalCenter: parent.verticalCenter
            spacing: 12
            Text {
                Layout.fillWidth: true
                text: S.tr2(S.rev, "characters.footer")
                color: Theme.ink3
                font.pixelSize: 11
                lineHeight: 1.2
                wrapMode: Text.Wrap
            }
            LinkButton {
                text: S.tr2(S.rev, "characters.openFolder")
                onClicked: characters.openFolder()
            }
        }
    }
}
