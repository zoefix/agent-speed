import QtQuick
import QtQuick.Controls
import QtQuick.Layouts
import "Format.js" as Format

// UPDATES 4: the compact update window — notes, then skip, later and install; a progress line while installing.
Window {
    id: win
    width: 460
    height: content.implicitHeight + 48
    minimumWidth: 420
    title: S.tr2(S.rev, "update.notes")
    color: Theme.panel
    readonly property bool busy: network.updateState === "downloading" || network.updateState === "verifying" || network.updateState === "installing"

    ColumnLayout {
        id: content
        x: 24
        y: 24
        width: win.width - 48
        spacing: 12
        RowLayout {
            spacing: 14
            Gauge {
                heat: 0.55
                Layout.preferredWidth: 48
                Layout.preferredHeight: 48
            }
            ColumnLayout {
                spacing: 3
                Text {
                    text: Format.fill(S.tr2(S.rev, "update.available.title"), [network.availableVersion])
                    color: Theme.ink
                    font.pixelSize: 16
                    font.weight: Font.DemiBold
                }
                Text {
                    text: Format.fill(S.tr2(S.rev, "update.current"), [catalog.version])
                    color: Theme.ink2
                    font.pixelSize: 12
                }
            }
        }
        Text {
            text: S.tr2(S.rev, "update.notes")
            color: Theme.ink2
            font.pixelSize: 12
            font.weight: Font.DemiBold
        }
        ScrollView {
            Layout.fillWidth: true
            Layout.preferredHeight: Math.min(240, notes.implicitHeight + 16)
            clip: true
            Text {
                id: notes
                width: win.width - 64
                text: network.releaseNotes
                textFormat: Text.MarkdownText
                color: Theme.ink
                font.pixelSize: 13
                wrapMode: Text.WordWrap
                onLinkActivated: (link) => catalog.openUrl(link)
            }
        }
        Text {
            visible: network.updateState === "failed" || win.busy
            Layout.fillWidth: true
            text: network.updateLine
            color: network.updateState === "failed" ? Theme.amber : Theme.ink2
            font.pixelSize: 12
            wrapMode: Text.WordWrap
        }
        ProgressBar {
            visible: network.updateState === "downloading"
            Layout.fillWidth: true
            value: network.updateProgress
        }
        RowLayout {
            visible: !win.busy
            Layout.fillWidth: true
            spacing: 8
            PillButton {
                text: S.tr2(S.rev, "update.skip")
                onClicked: { network.skipUpdate(); win.close() }
            }
            Item { Layout.fillWidth: true }
            PillButton {
                visible: network.updateState === "failed"
                text: S.tr2(S.rev, "update.openRelease")
                onClicked: network.openRelease()
            }
            PillButton {
                text: S.tr2(S.rev, "update.later")
                onClicked: win.close()
            }
            PillButton {
                accent: true
                text: network.updateState === "failed" ? S.tr2(S.rev, "update.retry")
                      : network.canInstall ? S.tr2(S.rev, "update.install") : S.tr2(S.rev, "update.openRelease")
                onClicked: network.installUpdate()
            }
        }
    }
}
