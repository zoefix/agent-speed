import QtQuick
import QtQuick.Controls
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
            caption: S.rev >= 0 && catalog.revision >= 0 ? network.rateLine + (network.rateAttribution ? "\n" + S.tr2(S.rev, "rates.attribution") : "") : ""
            CurrencyPicker {
                value: prefs.currency
                onActivated: (code) => prefs.currency = code
            }
        }
        SettingsRow {
            visible: prefs.currency !== "USD"
            label: S.tr2(S.rev, "rates.refresh")
            PillButton {
                enabled: prefs.networkEnabled && !network.rateRefreshing
                text: network.rateRefreshing ? S.tr2(S.rev, "rates.refreshing") : S.tr2(S.rev, "rates.refresh")
                onClicked: network.refreshRates()
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
            label: S.rev >= 0 && appState.snapshot.priceVersion >= 0
                   ? catalog.priceStatus() + (catalog.pricesBundled() ? " · " + S.tr2(S.rev, "prices.bundled") : "") : ""
            caption: network.priceResult.length > 0 ? network.priceResult : network.priceLine
            LinkButton {
                text: S.tr2(S.rev, "prices.viewSource") + " ↗"
                onClicked: catalog.openUrl("https://github.com/zoefix/agent-speed/blob/main/spec/assets/prices.json")
            }
            PillButton {
                enabled: prefs.networkEnabled && !network.priceChecking
                text: network.priceChecking ? S.tr2(S.rev, "prices.checking") : S.tr2(S.rev, "prices.check")
                onClicked: network.checkPrices()
            }
        }
        // METRICS 9.10 Live: today's models without a price, selectable so the user can report them.
        Item {
            visible: (appState.snapshot.unknownToday || "").length > 0
            Layout.fillWidth: true
            implicitHeight: unknownText.implicitHeight + 16
            TextEdit {
                id: unknownText
                x: 14
                y: 8
                width: parent.width - 28
                readOnly: true
                selectByMouse: true
                wrapMode: Text.Wrap
                text: appState.snapshot.unknownToday || ""
                color: Theme.ink2
                font.pixelSize: 12
                HoverHandler { id: unknownHover }
                ToolTip.visible: unknownHover.hovered
                ToolTip.delay: 600
                ToolTip.text: appState.snapshot.unknownTodayHint || ""
            }
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
