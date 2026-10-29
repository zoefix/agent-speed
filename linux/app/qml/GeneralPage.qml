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
                    { value: "zh-Hant", label: "繁體中文" },
                    { value: "zh-Hans", label: "简体中文" },
                    { value: "en", label: "English" },
                    { value: "ja", label: "日本語" }
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
            }
        }
    }
}
