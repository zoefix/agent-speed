import QtQuick
import QtQuick.Layouts

// DESIGN 10 Settings → 網路流量: measuring, tools, the rate unit and the sampler's status.
SettingsPage {
    title: S.tr2(S.rev, "settings.traffic")

    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "settings.traffic.enabled")
            caption: S.tr2(S.rev, "settings.traffic.enabled.hint")
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.traffic.enabled")
                checked: prefs.trafficEnabled
                onToggled: (on) => prefs.trafficEnabled = on
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.traffic.includeTools")
            caption: S.tr2(S.rev, "settings.traffic.includeTools.hint")
            enabled: prefs.trafficEnabled
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.traffic.includeTools")
                checked: prefs.trafficIncludeTools
                onToggled: (on) => prefs.trafficIncludeTools = on
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.traffic.unit")
            enabled: prefs.trafficEnabled
            divider: false
            Segmented {
                value: prefs.trafficUnit
                options: [
                    { value: "bytes", label: S.tr2(S.rev, "settings.traffic.unit.bytes") },
                    { value: "bits", label: S.tr2(S.rev, "settings.traffic.unit.bits") }
                ]
                onActivated: (v) => prefs.trafficUnit = v
            }
        }
    }

    SettingsGroup {
        title: S.tr2(S.rev, "traffic.status")
        SettingsRow {
            id: statusRow
            readonly property var traffic: appState.snapshot.traffic || ({})
            label: !prefs.trafficEnabled ? S.tr2(S.rev, "settings.traffic.enabled")
                   : traffic.available === false ? S.tr2(S.rev, "traffic.unavailable.sampler") : S.tr2(S.rev, "traffic.status.ok")
            divider: false
            Rectangle {
                width: 8
                height: 8
                radius: 4
                color: !prefs.trafficEnabled ? Theme.ink3 : statusRow.traffic.available === false ? Theme.amber : "#34C759"
            }
        }
    }
}
