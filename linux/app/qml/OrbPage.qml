import QtQuick
import QtQuick.Controls
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
                anchors.verticalCenter: parent.verticalCenter
                value: Math.abs(orb.diameter - 112) < 0.5 ? 112 : Math.abs(orb.diameter - 148) < 0.5 ? 148
                     : Math.abs(orb.diameter - 196) < 0.5 ? 196 : -1
                options: [
                    { value: 112, label: S.tr2(S.rev, "menu.size.small") },
                    { value: 148, label: S.tr2(S.rev, "menu.size.medium") },
                    { value: 196, label: S.tr2(S.rev, "menu.size.large") }
                ]
                onActivated: (v) => orb.diameter = v
            }
            Slider {
                id: sizeSlider
                anchors.verticalCenter: parent.verticalCenter
                width: 140
                from: orb.minimumDiameter
                to: 240
                stepSize: 4
                snapMode: Slider.SnapAlways
                value: orb.diameter
                onMoved: orb.diameter = value
                Accessible.name: S.tr2(S.rev, "settings.orb.size")
            }
            Text {
                anchors.verticalCenter: parent.verticalCenter
                width: 46
                horizontalAlignment: Text.AlignRight
                text: Math.round(orb.diameter) + " pt"
                color: Theme.ink2
                font.pixelSize: 12
                font.features: Theme.tabular
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "orbs.traffic")
            caption: orb.traffic === "intelligence" ? S.tr2(S.rev, "orbs.traffic.hint.intel") : S.tr2(S.rev, "orbs.traffic.hint")
            Dropdown {
                value: orb.traffic
                popupWidth: 200
                options: [
                    { value: "sources", label: S.tr2(S.rev, "orbs.traffic.sources") },
                    { value: "machine", label: S.tr2(S.rev, "orbs.traffic.machine") },
                    { value: "intelligence", label: S.tr2(S.rev, "orbs.traffic.intelligence") },
                    { value: "off", label: S.tr2(S.rev, "orbs.traffic.off") }
                ]
                onActivated: (v) => orb.traffic = v
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
        title: S.tr2(S.rev, "orbs.style")
        Item {
            Layout.fillWidth: true
            implicitHeight: stylePicker.implicitHeight + 28
            OrbStylePicker {
                id: stylePicker
                x: 14
                y: 14
                width: parent.width - 28
                selected: orb.style
                onPicked: (key) => orb.style = key
            }
        }
    }

    BoardsGroup {}
}
