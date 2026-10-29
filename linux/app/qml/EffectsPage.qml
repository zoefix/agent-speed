import QtQuick
import QtQuick.Layouts

SettingsPage {
    title: S.tr2(S.rev, "settings.effects")

    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "settings.effects.intensity")
            Segmented {
                value: orb.effectsLevel
                options: [
                    { value: 0, label: S.tr2(S.rev, "effects.off") },
                    { value: 1, label: S.tr2(S.rev, "effects.soft") },
                    { value: 2, label: S.tr2(S.rev, "effects.standard") },
                    { value: 3, label: S.tr2(S.rev, "effects.max") }
                ]
                onActivated: (v) => orb.effectsLevel = v
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.effects.peaks")
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.effects.peaks")
                checked: prefs.celebratePeaks
                onToggled: (on) => prefs.celebratePeaks = on
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.effects.pulses")
            divider: false
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.effects.pulses")
                checked: prefs.responsePulses
                onToggled: (on) => prefs.responsePulses = on
            }
        }
    }
    SettingsGroup {
        footnote: S.tr2(S.rev, "settings.effects.reduceMotion")
        SettingsRow {
            label: S.tr2(S.rev, "settings.effects.preview")
            divider: false
            PillButton {
                text: appState.demo ? S.tr2(S.rev, "settings.effects.previewing") : S.tr2(S.rev, "settings.effects.preview")
                onClicked: appState.demo = !appState.demo
            }
        }
    }
}
