import QtQuick
import QtQuick.Layouts
import "Format.js" as Format
import "Heat.js" as Heat

SettingsPage {
    title: S.tr2(S.rev, "settings.general")

    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "settings.appearance")
            Segmented {
                value: prefs.appearance
                options: [
                    { value: "auto", label: S.tr2(S.rev, "appearance.auto") },
                    { value: "light", label: S.tr2(S.rev, "appearance.light") },
                    { value: "dark", label: S.tr2(S.rev, "appearance.dark") }
                ]
                onActivated: (v) => prefs.appearance = v
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.language")
            Dropdown {
                value: prefs.language
                options: [
                    { value: "system", label: S.tr2(S.rev, "settings.language.system") },
                    { value: "zh-Hant", label: S.tr2(S.rev, "language.zhHant") },
                    { value: "zh-Hans", label: S.tr2(S.rev, "language.zhHans") },
                    { value: "en", label: S.tr2(S.rev, "language.en") },
                    { value: "ja", label: S.tr2(S.rev, "language.ja") }
                ]
                onActivated: (v) => prefs.language = v
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.launchAtLogin")
            divider: false
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.launchAtLogin")
                checked: prefs.launchAtLogin
                onToggled: (on) => prefs.launchAtLogin = on
            }
        }
    }

    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "settings.metric")
            Segmented {
                value: prefs.metric
                options: [
                    { value: "output", label: S.tr2(S.rev, "metric.output") },
                    { value: "total", label: S.tr2(S.rev, "metric.total") }
                ]
                onActivated: (v) => prefs.metric = v
            }
        }
        Item {
            Layout.fillWidth: true
            implicitHeight: redline.implicitHeight + 28
            ColumnLayout {
                id: redline
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                spacing: 8
                RowLayout {
                    Layout.fillWidth: true
                    Text {
                        text: S.tr2(S.rev, "settings.redline")
                        color: Theme.ink
                        font.pixelSize: 13
                    }
                    Item { Layout.fillWidth: true }
                    Text {
                        text: prefs.redline + " " + S.tr2(S.rev, "unit.tokS")
                        color: Theme.heatText(Heat.color(1))
                        font.pixelSize: 13
                        font.weight: Font.DemiBold
                        font.features: Theme.tabular
                    }
                }
                RedlineSlider {
                    Layout.fillWidth: true
                    value: prefs.redline
                    onMoved: (v) => prefs.redline = v
                }
                Text {
                    Layout.fillWidth: true
                    text: S.tr2(S.rev, "settings.redline.hint")
                    color: Theme.ink3
                    font.pixelSize: 11
                    lineHeight: 1.2
                    wrapMode: Text.Wrap
                }
                // `title.ladder`: every title of the selected theme with its threshold at the current redline.
                Text {
                    Layout.fillWidth: true
                    text: S.tr2(S.rev, "title.ladder")
                    color: Theme.ink2
                    font.pixelSize: 11
                    font.weight: Font.DemiBold
                }
                Flow {
                    Layout.fillWidth: true
                    spacing: 10
                    Repeater {
                        model: 13
                        Row {
                            required property int index
                            readonly property int level: index + 1
                            spacing: 4
                            Text {
                                text: { themes.rev; return themes.title(parent.level) }
                                color: Theme.ink3
                                font.pixelSize: 11
                            }
                            Text {
                                text: Format.speed(themes.threshold(parent.level, prefs.redline))
                                color: Theme.heatText(Heat.color(Heat.heatOf(themes.threshold(parent.level, prefs.redline), prefs.redline)))
                                font.pixelSize: 11
                                font.weight: Font.DemiBold
                                font.features: Theme.tabular
                            }
                        }
                    }
                }
            }
        }
    }

    TitleThemesGroup {}
}
