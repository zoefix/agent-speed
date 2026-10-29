import QtQuick
import QtQuick.Layouts

SettingsPage {
    title: S.tr2(S.rev, "settings.orb")

    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "settings.orb.show")
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.orb.show")
                checked: orb.orbVisible
                onToggled: (on) => orb.orbVisible = on
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.orb.size")
            Segmented {
                value: orb.diameter < 130 ? 112 : orb.diameter < 172 ? 148 : 196
                options: [
                    { value: 112, label: S.tr2(S.rev, "menu.size.small") },
                    { value: 148, label: S.tr2(S.rev, "menu.size.medium") },
                    { value: 196, label: S.tr2(S.rev, "menu.size.large") }
                ]
                onActivated: (v) => orb.diameter = v
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "menu.alwaysOnTop")
            ToggleSwitch {
                label: S.tr2(S.rev, "menu.alwaysOnTop")
                checked: orb.alwaysOnTop
                onToggled: (on) => orb.alwaysOnTop = on
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "menu.clickThrough")
            divider: false
            ToggleSwitch {
                label: S.tr2(S.rev, "menu.clickThrough")
                checked: orb.clickThrough
                onToggled: (on) => orb.clickThrough = on
            }
        }
    }

    SettingsGroup {
        title: S.tr2(S.rev, "style.led")
        SettingsRow {
            label: S.tr2(S.rev, "style.led")
            caption: S.tr2(S.rev, "style.led.desc")
            divider: false
            Glyph { name: "check"; color: Theme.accent; width: 15; height: 15 }
        }
    }
}
