import QtQuick
import QtQuick.Layouts
import "Format.js" as Format
import "Sync.js" as Sync

// DESIGN 5 資料來源: per source a switch, the detected path, its status, Change… and Reset.
SettingsPage {
    id: page
    title: S.tr2(S.rev, "settings.sources")

    readonly property var hints: ({
        claudeCode: "settings.sources.sharedRoot", deepseek: "settings.sources.dshHint", opencode: "settings.sources.opencodeHint",
        gemini: "settings.sources.geminiHint", omp: "settings.sources.ompHint", antigravity: "settings.sources.antigravityHint",
        workbuddy: "settings.sources.workbuddyHint", workbuddyAi: "settings.sources.workbuddyAiHint", zcode: "settings.sources.zcodeHint",
        kimi: "settings.sources.kimiHint", grokBuild: "settings.sources.grokBuildHint", pi: "settings.sources.piHint",
        openclaw: "settings.sources.openclawHint", hermes: "settings.sources.hermesHint", droid: "settings.sources.droidHint"
    })

    ListModel { id: sourceModel }
    function refresh() {
        var rows = []
        var raw = appState.snapshot.sources || []
        for (var i = 0; i < raw.length; ++i) {
            var s = raw[i]
            if (s.source === "claudeDesktop") continue
            rows.push({ key: s.source, source: s.source, name: s.source === "claudeCode" ? "Claude Code · Claude Desktop" : s.name,
                        path: s.path, available: s.available, sessions: s.sessions })
        }
        Sync.sync(sourceModel, rows, "key")
    }
    Connections {
        target: appState
        function onSnapshotChanged() { page.refresh() }
    }
    Component.onCompleted: refresh()

    SettingsGroup {
        Repeater {
            model: sourceModel
            Item {
                id: row
                required property string source
                required property string name
                required property string path
                required property bool available
                required property int sessions
                required property int index
                readonly property bool enabled_: S.rev >= 0 && prefs.disabledSources.indexOf(source) < 0
                readonly property bool chosen: prefs.folders[source] !== undefined
                Layout.fillWidth: true
                implicitHeight: content.implicitHeight + 24

                ColumnLayout {
                    id: content
                    anchors.left: parent.left
                    anchors.right: parent.right
                    anchors.leftMargin: 14
                    anchors.rightMargin: 14
                    anchors.verticalCenter: parent.verticalCenter
                    spacing: 5
                    RowLayout {
                        Layout.fillWidth: true
                        spacing: 10
                        Rectangle {
                            Layout.preferredWidth: 9
                            Layout.preferredHeight: 9
                            radius: 4.5
                            color: Theme.sourceColor(row.source)
                            opacity: row.enabled_ ? 1 : 0.35
                        }
                        Text {
                            Layout.fillWidth: true
                            text: row.name
                            color: row.enabled_ ? Theme.ink : Theme.ink2
                            font.pixelSize: 13
                            font.weight: Font.Medium
                            elide: Text.ElideRight
                        }
                        Text {
                            text: !row.enabled_ ? S.tr2(S.rev, "source.disabled")
                                  : row.available ? Format.fill(S.tr2(S.rev, "source.sessions"), [row.sessions])
                                  : S.tr2(S.rev, row.chosen ? "source.folderMissing" : "source.notFound")
                            color: row.enabled_ && row.available ? Theme.ink2 : Theme.ink3
                            font.pixelSize: 11
                        }
                        ToggleSwitch {
                            label: row.name
                            checked: row.enabled_
                            onToggled: (on) => prefs.setSourceEnabled(row.source, on)
                        }
                    }
                    RowLayout {
                        Layout.fillWidth: true
                        Layout.leftMargin: 19
                        spacing: 8
                        Text {
                            Layout.fillWidth: true
                            text: row.path
                            color: Theme.ink3
                            font.pixelSize: 11
                            font.family: "monospace"
                            elide: Text.ElideMiddle
                        }
                        PillButton {
                            text: S.tr2(S.rev, "settings.sources.change")
                            implicitHeight: 24
                            onClicked: {
                                var title = S.tr2(S.rev, "settings.sources.change") + " " + row.name
                                var picked = row.source === "openclaw" ? catalog.chooseFileOrFolder(title, row.path)
                                                                       : catalog.chooseFolder(title, row.path)
                                if (picked.length > 0) prefs.setFolder(row.source, picked)
                            }
                        }
                        PillButton {
                            visible: row.chosen
                            text: S.tr2(S.rev, "settings.sources.reset")
                            implicitHeight: 24
                            onClicked: prefs.setFolder(row.source, "")
                        }
                    }
                    Text {
                        visible: page.hints[row.source] !== undefined
                        Layout.fillWidth: true
                        Layout.leftMargin: 19
                        text: page.hints[row.source] ? S.tr2(S.rev, page.hints[row.source]) : ""
                        color: Theme.ink3
                        font.pixelSize: 11
                        lineHeight: 1.2
                        wrapMode: Text.Wrap
                    }
                }
                Rectangle {
                    visible: row.index < sourceModel.count - 1
                    anchors.left: parent.left
                    anchors.leftMargin: 14
                    anchors.right: parent.right
                    anchors.bottom: parent.bottom
                    height: 1
                    color: Theme.hairline
                }
            }
        }
    }
}
