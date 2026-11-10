import QtQuick
import QtQuick.Layouts
import "Format.js" as Format

// DESIGN 5 額度 and 額度, phase 2: the quota switches of one Data Sources group, after its source toggles — each with
// its hint, the unverified caption for the phase 2 routes and its status line. Turning the Claude Code bridge or a
// browser session on asks first; turning it off acts at once.
ColumnLayout {
    id: rows
    // The group's sources (`claudeCode` stands for Claude Code and Claude Desktop).
    property var sources: []
    Layout.fillWidth: true
    spacing: 0

    function has(source) { return sources.indexOf(source) >= 0 }
    function enabled(source) { return S.rev >= 0 && prefs.disabledSources.indexOf(source) < 0 }
    function status(key) { return (quotas.statuses || {})[key] || ({ text: "", warning: false, ok: false }) }
    function set(key, on) { prefs.setQuotaSwitch(key, on) }

    // The site and host a browser session reads (QUOTAS 9.9).
    readonly property var cookieSites: ({
        quotaQoder: { name: "Qoder", host: "qoder.com" },
        quotaWorkBuddy: { name: "WorkBuddy", host: "www.workbuddy.cn" }
    })
    function cookieHint(key) {
        const site = cookieSites[key]
        return Format.fill(S.tr2(S.rev, "settings.quota.cookies.hint"), [site.name, site.host])
    }
    property string pendingCookie: ""
    function askCookies(key) {
        const site = cookieSites[key]
        pendingCookie = key
        cookieDialog.question = Format.fill(S.tr2(S.rev, "settings.quota.cookies.confirm.title"), [site.name])
        cookieDialog.detail = Format.fill(S.tr2(S.rev, "linux.settings.quota.cookies.confirm.message"), [site.name, site.host])
        cookieDialog.ask()
    }
    function askCli() {
        cliDialog.ask()
        quotas.cliConfirmRequested = false
    }
    Connections {
        target: quotas
        function onSettingsChanged() { if (quotas.cliConfirmRequested && rows.has("claudeCode") && rows.visible) rows.askCli() }
    }
    Component.onCompleted: {
        if (quotas.cliConfirmRequested && has("claudeCode")) Qt.callLater(askCli)
        // Development aid: --quota-ask=<quotaClaudeCli|quotaQoder|quotaWorkBuddy> asks as turning that switch on does.
        for (let i = 0; i < Qt.application.arguments.length; ++i) {
            const arg = Qt.application.arguments[i]
            if (arg.indexOf("--quota-ask=") !== 0) continue
            const key = arg.substr(12)
            if (key === "quotaClaudeCli" && has("claudeCode")) askTimer.ask = askCli
            else if (key === "quotaQoder" && has("qoder")) askTimer.ask = () => askCookies(key)
            else if (key === "quotaWorkBuddy" && has("workbuddy")) askTimer.ask = () => askCookies(key)
            if (askTimer.ask) askTimer.start()
        }
    }
    Timer {
        id: askTimer
        property var ask: null
        interval: 900
        onTriggered: ask()
    }

    component QuotaRow: QuotaToggle {
        readonly property var state: rows.status(switchKey)
        statusText: state.text
        statusWarning: state.warning === true
        statusOk: state.ok === true
    }

    QuotaRow {
        visible: rows.has("codex")
        switchKey: "quotaCodex"
        title: S.tr2(S.rev, "settings.quota.codex")
        hint: S.tr2(S.rev, "settings.quota.codex.hint")
        sourceActive: rows.enabled("codex")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        visible: rows.has("claudeCode")
        switchKey: "quotaClaudeDesktop"
        title: S.tr2(S.rev, "settings.quota.claudeDesktop")
        hint: S.tr2(S.rev, "settings.quota.claudeDesktop.hint")
        sourceActive: rows.enabled("claudeCode")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        id: cli
        visible: rows.has("claudeCode")
        switchKey: "quotaClaudeCli"
        title: S.tr2(S.rev, "settings.quota.claudeCli")
        hint: S.tr2(S.rev, "settings.quota.claudeCli.hint")
        // The bridge stays switchable while Claude Code is off, so it can always be restored.
        sourceActive: true
        showStatus: on || quotas.cliState === "unreadable" || quotas.cliState === "failed"
        onToggledTo: (on) => on ? rows.askCli() : quotas.disableCli()
        LinkButton {
            visible: quotas.cliState === "replaced"
            text: S.tr2(S.rev, "settings.quota.cli.reconnect")
            onClicked: quotas.enableCli()
        }
    }
    QuotaRow {
        visible: rows.has("gemini")
        switchKey: "quotaGemini"
        title: S.tr2(S.rev, "settings.quota.gemini")
        hint: S.tr2(S.rev, "settings.quota.gemini.hint")
        unverified: true
        sourceActive: rows.enabled("gemini")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        visible: rows.has("antigravity")
        switchKey: "quotaAntigravity"
        title: S.tr2(S.rev, "settings.quota.antigravity")
        hint: S.tr2(S.rev, "settings.quota.antigravity.hint")
        unverified: true
        sourceActive: rows.enabled("antigravity")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        visible: rows.has("workbuddy")
        switchKey: "quotaWorkBuddy"
        title: S.tr2(S.rev, "settings.quota.workbuddy")
        hint: rows.cookieHint(switchKey)
        unverified: true
        sourceActive: rows.enabled("workbuddy")
        onToggledTo: (on) => on ? rows.askCookies(switchKey) : rows.set(switchKey, false)
    }
    QuotaRow {
        id: zcode
        visible: rows.has("zcode")
        switchKey: "quotaZcode"
        title: S.tr2(S.rev, "settings.quota.zcode")
        hint: S.tr2(S.rev, "settings.quota.zcode.hint")
        unverified: true
        sourceActive: rows.enabled("zcode")
        onToggledTo: (on) => rows.set(switchKey, on)
        QuotaKeyField {
            Layout.fillWidth: true
            labelWidth: zcodeLabels.width
        }
        RowLayout {
            Layout.fillWidth: true
            spacing: 10
            Text {
                Layout.preferredWidth: zcodeLabels.width
                text: S.tr2(S.rev, "settings.quota.zcode.region")
                color: Theme.ink2
                font.pixelSize: 12
            }
            Segmented {
                options: [{ value: "global", label: S.tr2(S.rev, "settings.quota.zcode.regionGlobal") },
                          { value: "bigmodel-cn", label: S.tr2(S.rev, "settings.quota.zcode.regionCn") }]
                value: prefs.quotaZcodeRegion
                segmentPadding: 16
                Accessible.name: S.tr2(S.rev, "settings.quota.zcode.region")
                onActivated: (value) => prefs.quotaZcodeRegion = value
            }
            Item { Layout.fillWidth: true }
        }
    }
    QuotaRow {
        visible: rows.has("kimi")
        switchKey: "quotaKimi"
        title: S.tr2(S.rev, "settings.quota.kimi")
        hint: S.tr2(S.rev, "settings.quota.kimi.hint")
        unverified: true
        sourceActive: rows.enabled("kimi")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        id: grok
        visible: rows.has("grokBuild")
        switchKey: "quotaGrokBuild"
        title: S.tr2(S.rev, "settings.quota.grokBuild")
        hint: S.tr2(S.rev, "settings.quota.grokBuild.hint")
        unverified: true
        sourceActive: rows.enabled("grokBuild")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        visible: rows.has("grokBuild")
        nested: true
        switchKey: "quotaGrokBuildLive"
        title: S.tr2(S.rev, "settings.quota.grokBuildLive")
        sourceActive: rows.enabled("grokBuild")
        parentOn: grok.on
        showStatus: on && grok.on
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        visible: rows.has("droid")
        switchKey: "quotaDroid"
        title: S.tr2(S.rev, "settings.quota.droid")
        hint: S.tr2(S.rev, "settings.quota.droid.hint")
        unverified: true
        sourceActive: rows.enabled("droid")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        visible: rows.has("devin")
        switchKey: "quotaDevin"
        title: S.tr2(S.rev, "settings.quota.devin")
        hint: S.tr2(S.rev, "settings.quota.devin.hint")
        unverified: true
        sourceActive: rows.enabled("devin")
        onToggledTo: (on) => rows.set(switchKey, on)
    }
    QuotaRow {
        visible: rows.has("qoder")
        switchKey: "quotaQoder"
        title: S.tr2(S.rev, "settings.quota.qoder")
        hint: rows.cookieHint(switchKey)
        unverified: true
        // One account for Qoder IDE and Qoder CLI: it counts while either is enabled.
        sourceActive: rows.enabled("qoder") || rows.enabled("qoderCli")
        onToggledTo: (on) => on ? rows.askCookies(switchKey) : rows.set(switchKey, false)
    }

    // ZCode's key and region labels share one column.
    Item {
        id: zcodeLabels
        visible: false
        width: Math.ceil(Math.max(keyLabel.advanceWidth, regionLabel.advanceWidth))
        TextMetrics { id: keyLabel; font.pixelSize: 12; text: S.tr2(S.rev, "settings.quota.zcode.key") }
        TextMetrics { id: regionLabel; font.pixelSize: 12; text: S.tr2(S.rev, "settings.quota.zcode.region") }
    }

    QuotaConfirmDialog {
        id: cliDialog
        glyph: "gauge"
        question: S.tr2(S.rev, "settings.quota.claudeCli.confirm.title")
        detail: Format.fill(S.tr2(S.rev, "settings.quota.claudeCli.confirm.message"), [quotas.cliPath])
        action: S.tr2(S.rev, "settings.quota.claudeCli.confirm.ok")
        onConfirmed: quotas.enableCli()
    }
    QuotaConfirmDialog {
        id: cookieDialog
        glyph: "eye"
        action: S.tr2(S.rev, "settings.quota.cookies.confirm.ok")
        onConfirmed: rows.set(rows.pendingCookie, true)
    }
}
