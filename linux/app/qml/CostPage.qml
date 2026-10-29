import QtQuick
import QtQuick.Layouts
import "Format.js" as Format
import "Sync.js" as Sync

SettingsPage {
    id: page
    title: S.tr2(S.rev, "settings.cost")

    ListModel { id: subscriptionModel }
    function refresh() {
        var rows = []
        var raw = appState.snapshot.sources || []
        for (var i = 0; i < raw.length; ++i) {
            var s = raw[i]
            if (s.source === "claudeDesktop" || !(s.available || s.today > 0)) continue
            rows.push({ key: s.source, source: s.source, name: s.name })
        }
        Sync.sync(subscriptionModel, rows, "key")
    }
    Connections {
        target: appState
        function onSnapshotChanged() { page.refresh() }
    }
    Component.onCompleted: refresh()

    SettingsGroup {
        footnote: S.tr2(S.rev, "cost.apiEquivalent.hint")
        SettingsRow {
            label: S.tr2(S.rev, "settings.currency")
            caption: S.rev >= 0 ? catalog.rateStatus(prefs.currency) : ""
            CurrencyPicker {
                value: prefs.currency
                onActivated: (code) => prefs.currency = code
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.liveUnit")
            caption: S.tr2(S.rev, "settings.liveUnit.hint")
            divider: false
            Segmented {
                value: prefs.liveUnit
                options: [
                    { value: "tokens", label: S.tr2(S.rev, "settings.liveUnit.tokens") },
                    { value: "cost", label: Format.fill(S.tr2(S.rev, "settings.liveUnit.cost"), [appState.snapshot.costSymbol || "$"]) },
                    { value: "both", label: Format.fill(S.tr2(S.rev, "settings.liveUnit.both"), [appState.snapshot.costSymbol || "$"]) }
                ]
                onActivated: (v) => prefs.liveUnit = v
            }
        }
    }

    SettingsGroup {
        title: S.tr2(S.rev, "settings.cost.subscriptions")
        footnote: S.tr2(S.rev, "settings.cost.subscriptionsHint")
        Repeater {
            model: subscriptionModel
            SettingsRow {
                required property string source
                required property string name
                label: name
                ToggleSwitch {
                    label: S.tr2(S.rev, "settings.cost.subscription") + " · " + name
                    checked: prefs.subscriptions.indexOf(source) >= 0
                    onToggled: (on) => prefs.setSubscribed(source, on)
                }
            }
        }
        SettingsRow {
            label: S.tr2(S.rev, "settings.cost.includeSubscriptions")
            caption: S.tr2(S.rev, "settings.cost.includeSubscriptionsHint")
            divider: false
            ToggleSwitch {
                label: S.tr2(S.rev, "settings.cost.includeSubscriptions")
                checked: prefs.includeSubscriptions
                onToggled: (on) => prefs.includeSubscriptions = on
            }
        }
    }

    SettingsGroup {
        title: S.tr2(S.rev, "prices.title")
        SettingsRow {
            label: S.rev >= 0 && appState.snapshot.priceVersion >= 0 ? catalog.priceStatus() : ""
            caption: S.tr2(S.rev, "prices.never")
        }
        SettingsRow {
            label: S.tr2(S.rev, "prices.autoSync")
            caption: S.tr2(S.rev, "prices.autoSync.hint")
            divider: false
            ToggleSwitch {
                enabled: prefs.networkEnabled
                label: S.tr2(S.rev, "prices.autoSync")
                checked: prefs.syncPrices
                onToggled: (on) => prefs.syncPrices = on
            }
        }
    }
}
