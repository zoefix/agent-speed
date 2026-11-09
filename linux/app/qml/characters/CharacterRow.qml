import QtQuick
import QtQuick.Controls
import QtQuick.Layouts

// One character: portrait (the installed preview, else a quiet silhouette; nothing is fetched for a character that is
// not downloaded), accent dot and name, the artist credit and the download size, and its action on the right.
Item {
    id: item
    property var row: ({})
    property bool divider: true
    Layout.fillWidth: true
    implicitHeight: Math.max(64, texts.implicitHeight + 20)
    Accessible.role: Accessible.ListItem
    Accessible.name: [row.name, row.artist, row.size, row.error].filter((s) => s && s.length > 0).join(", ")

    Item {
        anchors.fill: parent
        anchors.leftMargin: 14
        anchors.rightMargin: 14

        Item {
            id: portraitBox
            anchors.left: parent.left
            anchors.verticalCenter: parent.verticalCenter
            width: 40
            height: 52
            Image {
                id: portrait
                anchors.fill: parent
                visible: item.row.preview && item.row.preview.length > 0 && status === Image.Ready
                source: item.row.preview || ""
                sourceSize.height: 104
                fillMode: Image.PreserveAspectFit
                smooth: true
                mipmap: true
                asynchronous: true
            }
            CharacterSilhouette {
                anchors.fill: parent
                visible: !portrait.visible
                color: Theme.ink3
            }
        }

        ColumnLayout {
            id: texts
            anchors.left: portraitBox.right
            anchors.leftMargin: 12
            anchors.right: action.left
            anchors.rightMargin: 12
            anchors.verticalCenter: parent.verticalCenter
            spacing: 3
            RowLayout {
                spacing: 6
                Layout.fillWidth: false
                Layout.alignment: Qt.AlignLeft
                Rectangle {
                    width: 7
                    height: 7
                    radius: 3.5
                    color: item.row.accent || Theme.ink3
                }
                Text {
                    text: item.row.name || item.row.id || ""
                    color: Theme.ink
                    font.pixelSize: 13
                    font.weight: Font.Medium
                }
                Text {
                    visible: item.row.development === true
                    text: S.tr2(S.rev, "characters.development")
                    color: Theme.ink3
                    font.pixelSize: 10
                }
            }
            RowLayout {
                spacing: 8
                Layout.fillWidth: false
                Layout.alignment: Qt.AlignLeft
                LinkButton {
                    visible: item.row.artist && item.row.artist.length > 0 && item.row.artistLink && item.row.artistLink.length > 0
                    text: item.row.artist || ""
                    onClicked: Qt.openUrlExternally(item.row.artistLink)
                }
                Text {
                    visible: item.row.artist && item.row.artist.length > 0 && !(item.row.artistLink && item.row.artistLink.length > 0)
                    text: item.row.artist || ""
                    color: Theme.ink2
                    font.pixelSize: 11
                }
                Text {
                    visible: item.row.size && item.row.size.length > 0
                    text: item.row.size || ""
                    color: Theme.ink3
                    font.pixelSize: 11
                    font.features: Theme.tabular
                }
            }
            Text {
                visible: item.row.error && item.row.error.length > 0
                Layout.fillWidth: true
                text: item.row.error || ""
                color: Theme.danger
                font.pixelSize: 11
                wrapMode: Text.Wrap
            }
        }

        // The action: progress with cancel while downloading, installing, update / remove, or download.
        RowLayout {
            id: action
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            spacing: 6
            RowLayout {
                visible: item.row.downloading === true && item.row.installing !== true
                Layout.fillWidth: false
                spacing: 6
                ProgressBar {
                    Layout.preferredWidth: 76
                    from: 0
                    to: 1
                    value: item.row.progress || 0
                }
                IconButton {
                    glyph: "close"
                    label: S.tr2(S.rev, "characters.cancel")
                    onClicked: characters.cancel(item.row.id)
                }
            }
            RowLayout {
                visible: item.row.downloading === true && item.row.installing === true
                Layout.fillWidth: false
                spacing: 6
                BusyIndicator {
                    running: parent.visible
                    Layout.preferredWidth: 18
                    Layout.preferredHeight: 18
                }
                Text {
                    text: S.tr2(S.rev, "characters.installing")
                    color: Theme.ink2
                    font.pixelSize: 11
                }
            }
            PillButton {
                visible: item.row.downloading !== true && item.row.installed === true && item.row.hasUpdate === true
                text: S.tr2(S.rev, "characters.update")
                onClicked: characters.download(item.row.id)
            }
            PillButton {
                visible: item.row.downloading !== true && item.row.installed === true && item.row.development !== true
                text: S.tr2(S.rev, "characters.remove")
                onClicked: characters.remove(item.row.id)
            }
            PillButton {
                visible: item.row.downloading !== true && item.row.installed !== true && item.row.canDownload === true
                accent: true
                enabled: characters.networkEnabled
                text: S.tr2(S.rev, "characters.download")
                onClicked: characters.download(item.row.id)
            }
        }
    }
    Rectangle {
        visible: item.divider
        anchors.left: parent.left
        anchors.leftMargin: 66
        anchors.right: parent.right
        anchors.bottom: parent.bottom
        height: 1
        color: Theme.hairline
    }
}
