import QtQuick
import QtQuick.Layouts
import "Format.js" as Format

SettingsPage {
    title: S.tr2(S.rev, "settings.about")

    RowLayout {
        Layout.fillWidth: true
        spacing: 16
        Gauge {
            heat: appState.snapshot.heat || 0
            Layout.preferredWidth: 64
            Layout.preferredHeight: 64
        }
        ColumnLayout {
            spacing: 3
            Text {
                text: S.tr2(S.rev, "app.name")
                color: Theme.ink
                font.pixelSize: 20
                font.weight: Font.DemiBold
            }
            Text {
                text: Format.fill(S.tr2(S.rev, "settings.about.version"), [catalog.version]) + " · Linux"
                color: Theme.ink2
                font.pixelSize: 12
            }
            Text {
                text: S.tr2(S.rev, "settings.about.github")
                color: Theme.accent
                font.pixelSize: 12
                font.underline: linkArea.containsMouse
                MouseArea {
                    id: linkArea
                    anchors.fill: parent
                    hoverEnabled: true
                    cursorShape: Qt.PointingHandCursor
                    onClicked: catalog.openUrl("https://github.com/zoefix/agent-speed")
                }
            }
        }
    }

    SettingsGroup {
        Item {
            Layout.fillWidth: true
            implicitHeight: privacy.implicitHeight + 28
            Text {
                id: privacy
                anchors.left: parent.left
                anchors.right: parent.right
                anchors.margins: 14
                anchors.verticalCenter: parent.verticalCenter
                // DESIGN 11: while reporting is on, no sentence claims that nothing is sent.
                text: S.tr2(S.rev, report.reporting ? "privacy.statement.report" : "privacy.statement") + "\n\n"
                      + S.tr2(S.rev, report.reporting ? "privacy.traffic.report" : "privacy.traffic") + "\n\n"
                      + S.tr2(S.rev, "privacy.report") + "\n\n" + S.tr2(S.rev, "privacy.taskScore")
                color: Theme.ink2
                font.pixelSize: 12
                lineHeight: 1.3
                wrapMode: Text.Wrap
            }
        }
    }

    SettingsGroup {
        SettingsRow {
            label: S.tr2(S.rev, "settings.network")
            caption: S.tr2(S.rev, report.reporting ? "settings.network.hint.report" : "settings.network.hint")
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.network")
                checked: prefs.networkEnabled
                onToggled: (on) => prefs.networkEnabled = on
            }
        }
        SettingsRow {
            label: Format.fill(S.tr2(S.rev, "update.current"), [catalog.version])
            caption: network.updateLine
            PillButton {
                enabled: prefs.networkEnabled && network.updateState !== "checking"
                text: network.updateState === "available" ? Format.fill(S.tr2(S.rev, "update.menu"), [network.availableVersion])
                                                         : S.tr2(S.rev, "update.check")
                accent: network.updateState === "available"
                onClicked: network.updateState === "available" ? network.showUpdate() : network.checkUpdates()
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "update.auto")
            caption: S.tr2(S.rev, "update.auto.hint")
            divider: false
            ToggleSwitch {
                enabled: prefs.networkEnabled
                label: S.tr2(S.rev, "update.auto")
                checked: prefs.autoCheckUpdates
                onToggled: (on) => prefs.autoCheckUpdates = on
            }
        }
    }
}
