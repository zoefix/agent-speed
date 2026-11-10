import AgentSpeedCore
import AppKit
import SwiftUI

/// The phase 2 quota switches of Settings → Data Sources (DESIGN 5, *額度, phase 2*), after each group's source
/// toggles: the hint, `settings.quota.unverified`, and while on the source's status line. Grok Build adds its live
/// request indented under its log; ZCode its key field and region; Qoder and WorkBuddy ask before reading the
/// browser's session. While the source itself is off its quota rows are dimmed: nothing is read for it (QUOTAS 9.1).
struct QuotaVendorSwitches: View {
    let folder: TranscriptFolder
    let model: AppModel

    var body: some View {
        let settings = model.settings
        Group {
            rows(settings)
        }
        .disabled(!sourceIsOn(settings))
    }

    /// Qoder's shared account counts while either Qoder source is on.
    private func sourceIsOn(_ settings: AppSettings) -> Bool {
        folder == .qoder ? settings.isEnabled(.qoder) || settings.isEnabled(.qoderCli)
            : folder.sources.contains(where: settings.isEnabled)
    }

    @ViewBuilder
    private func rows(_ settings: AppSettings) -> some View {
        switch folder {
        case .grokBuild:
            QuotaRouteRow(route: .grokBuild, model: model, title: "settings.quota.grokBuild", hint: "settings.quota.grokBuild.hint",
                          isOn: Binding(get: { settings.quotaGrokBuild }, set: { settings.quotaGrokBuild = $0 }))
            QuotaRouteRow(route: .grokBuildLive, model: model, title: "settings.quota.grokBuildLive", hint: nil, indented: true,
                          isOn: Binding(get: { settings.quotaGrokBuildLive }, set: { settings.quotaGrokBuildLive = $0 }))
                .disabled(!settings.quotaGrokBuild)
        case .devin:
            QuotaRouteRow(route: .devin, model: model, title: "settings.quota.devin", hint: "settings.quota.devin.hint",
                          isOn: Binding(get: { settings.quotaDevin }, set: { settings.quotaDevin = $0 }))
        case .antigravity:
            QuotaRouteRow(route: .antigravity, model: model, title: "settings.quota.antigravity",
                          hint: "settings.quota.antigravity.hint",
                          isOn: Binding(get: { settings.quotaAntigravity }, set: { settings.quotaAntigravity = $0 }))
        case .kimi:
            QuotaRouteRow(route: .kimi, model: model, title: "settings.quota.kimi", hint: "settings.quota.kimi.hint",
                          isOn: Binding(get: { settings.quotaKimi }, set: { settings.quotaKimi = $0 }))
        case .gemini:
            QuotaRouteRow(route: .gemini, model: model, title: "settings.quota.gemini", hint: "settings.quota.gemini.hint",
                          isOn: Binding(get: { settings.quotaGemini }, set: { settings.quotaGemini = $0 }))
        case .droid:
            QuotaRouteRow(route: .droid, model: model, title: "settings.quota.droid", hint: "settings.quota.droid.hint",
                          isOn: Binding(get: { settings.quotaDroid }, set: { settings.quotaDroid = $0 }))
        case .zcode:
            ZcodeQuotaRows(model: model)
        case .qoder:
            BrowserQuotaRow(route: .qoder, model: model)
        case .workbuddy:
            BrowserQuotaRow(route: .workbuddy, model: model)
        default:
            EmptyView()
        }
    }
}

/// One route's switch with its captions and, while on, its status line.
private struct QuotaRouteRow: View {
    let route: QuotaRoute
    let model: AppModel
    let title: String
    let hint: String?
    var indented = false
    @Binding var isOn: Bool

    init(route: QuotaRoute, model: AppModel, title: String, hint: String?, indented: Bool = false, isOn: Binding<Bool>) {
        self.route = route
        self.model = model
        self.title = title
        self.hint = hint
        self.indented = indented
        _isOn = isOn
    }

    var body: some View {
        let l10n = model.l10n
        QuotaToggleRow(title: l10n(title), hint: hint.map { l10n($0) }, note: indented ? nil : l10n("settings.quota.unverified"),
                       indented: indented, isOn: $isOn) {
            QuotaRouteStatusLine(route: route, model: model)
        }
    }
}

/// ZCode: its switch, then while on the key field (saved to the keychain on Return or when it loses focus, never shown
/// again) and the region picker.
private struct ZcodeQuotaRows: View {
    let model: AppModel
    @State private var draft = ""
    @FocusState private var focused: Bool

    var body: some View {
        let settings = model.settings
        let l10n = model.l10n
        QuotaRouteRow(route: .zcode, model: model, title: "settings.quota.zcode", hint: "settings.quota.zcode.hint",
                      isOn: Binding(get: { settings.quotaZcode }, set: { settings.quotaZcode = $0 }))
        if settings.quotaZcode {
            LabeledContent {
                keyField(l10n)
                    .frame(width: QuotaFieldLayout.width, alignment: .trailing)
            } label: {
                HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                    QuotaIndent()
                    RowTitle(l10n("settings.quota.zcode.key"))
                }
            }
            .transition(.opacity)
            Picker(selection: Binding(get: { settings.quotaZcodeRegion }, set: { region in
                guard region != settings.quotaZcodeRegion else { return }
                settings.quotaZcodeRegion = region
                model.quota.forgetZcode()
            })) {
                Text(l10n("settings.quota.zcode.regionGlobal")).tag(QuotaRequests.ZaiRegion.global)
                Text(l10n("settings.quota.zcode.regionCn")).tag(QuotaRequests.ZaiRegion.bigmodelCn)
            } label: {
                HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                    QuotaIndent()
                    RowTitle(l10n("settings.quota.zcode.region"))
                }
            }
            .pickerStyle(.menu)
            .transition(.opacity)
        }
    }

    @ViewBuilder
    private func keyField(_ l10n: Localizer) -> some View {
        if model.quota.zcodeKeySaved == true {
            HStack(spacing: Spacing.xs) {
                HStack(spacing: 5) {
                    Image(systemName: "key.fill")
                        .font(.system(size: 11, weight: .medium))
                        .symbolRenderingMode(.hierarchical)
                        .foregroundStyle(.secondary)
                        .accessibilityHidden(true)
                    Text(l10n("settings.quota.zcode.saved"))
                        .foregroundStyle(.secondary)
                }
                .font(.subheadline)
                .lineLimit(1)
                Button(l10n("settings.quota.zcode.remove")) {
                    withAnimation(model.accessibility.reduceMotion ? nil : Motion.fade) { model.quota.removeZcodeKey() }
                }
                .controlSize(.small)
                .fixedSize()
            }
            .accessibilityElement(children: .contain)
        } else {
            SecureField(text: $draft, prompt: Text(l10n("settings.quota.zcode.keyPlaceholder"))) {
                Text(l10n("settings.quota.zcode.key"))
            }
            .labelsHidden()
            .textFieldStyle(.roundedBorder)
            .multilineTextAlignment(.leading)
            .font(.system(size: 13))
            .focused($focused)
            .onSubmit(save)
            .onChange(of: focused) { _, isFocused in
                if !isFocused { save() }
            }
        }
    }

    private func save() {
        guard !draft.isEmpty else { return }
        let text = draft
        draft = ""
        if !model.quota.saveZcodeKey(text) { NSSound.beep() }
    }
}

enum QuotaFieldLayout {
    static let width: CGFloat = 220
}

/// Qoder or WorkBuddy: off until the user agrees to read the site's session from the browsers (DESIGN 5, QUOTAS 9.10).
private struct BrowserQuotaRow: View {
    let route: QuotaRoute
    let model: AppModel
    @State private var host = HostWindow()

    var body: some View {
        let l10n = model.l10n
        let site = route.site ?? ("", "")
        QuotaToggleRow(title: l10n(route == .qoder ? "settings.quota.qoder" : "settings.quota.workbuddy"),
                       hint: l10n("settings.quota.cookies.hint", site.name, site.host), note: l10n("settings.quota.unverified"),
                       isOn: Binding(get: { isOn }, set: { on in on ? confirm() : set(false) })) {
            QuotaRouteStatusLine(route: route, model: model)
        }
        .background(WindowReader(host: host))
    }

    private var isOn: Bool { route == .qoder ? model.settings.quotaQoder : model.settings.quotaWorkBuddy }

    private func set(_ on: Bool) {
        if route == .qoder { model.settings.quotaQoder = on } else { model.settings.quotaWorkBuddy = on }
    }

    /// `settings.quota.cookies.confirm.*` as a sheet on Settings; Allow is the default button.
    private func confirm() {
        let alert = QuotaCookieConsent.alert(route, l10n: model.l10n)
        let handle: (NSApplication.ModalResponse) -> Void = { response in
            if response == .alertFirstButtonReturn { set(true) }
        }
        if let window = host.window ?? NSApp.keyWindow {
            alert.beginSheetModal(for: window, completionHandler: handle)
        } else {
            handle(alert.runModal())
        }
    }
}

/// The browser-session consent (DESIGN 5): the site's name and host, what is read and where it stays, and that macOS
/// asks once per browser.
enum QuotaCookieConsent {
    @MainActor
    static func alert(_ route: QuotaRoute, l10n: Localizer) -> NSAlert {
        let site = route.site ?? ("", "")
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = l10n("settings.quota.cookies.confirm.title", site.name)
        alert.informativeText = l10n("settings.quota.cookies.confirm.message", site.name, site.host)
        alert.addButton(withTitle: l10n("settings.quota.cookies.confirm.ok"))
        alert.addButton(withTitle: l10n("history.cancel"))
        return alert
    }
}

/// What a route is doing (DESIGN 5): updated with the time, or why nothing was asked; problems in the warning style of
/// `Notice` (an orange symbol before secondary text).
struct QuotaRouteStatusLine: View {
    let route: QuotaRoute
    let model: AppModel

    var body: some View {
        if let status = model.quota.status(route) {
            QuotaStatusLabel(status: status, text: QuotaStatusText.text(status, route: route, model: model))
                .transition(.opacity)
        }
    }
}

/// One status line: its symbol and sentence.
struct QuotaStatusLabel: View {
    let status: QuotaRouteStatus
    let text: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            indicator(status)
                .frame(width: 12, height: 12)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
                .accessibilityHidden(true)
            Text(text)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .contentTransition(.opacity)
        }
        .font(.subheadline)
        .padding(.top, 2)
        .animation(Motion.fade, value: status)
    }

    @ViewBuilder
    private func indicator(_ status: QuotaRouteStatus) -> some View {
        switch status {
        case .ok: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .notRunning: Image(systemName: "pause.circle").foregroundStyle(.secondary)
        case .networkOff: Image(systemName: "wifi.slash").foregroundStyle(.secondary)
        default: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        }
    }
}

/// The status sentences of `settings.quota.status.*`.
enum QuotaStatusText {
    @MainActor
    static func text(_ status: QuotaRouteStatus, route: QuotaRoute?, agent: String? = nil, model: AppModel) -> String {
        let l10n = model.l10n
        let name = agent ?? route?.agentName(l10n) ?? ""
        switch status {
        case .ok(let at): return l10n("settings.quota.status.ok", AppModel.quotaTime(at, nowMs: model.quota.clockMs, locale: l10n.locale))
        case .noCredential: return l10n("settings.quota.status.noCredential")
        case .noBrowser: return l10n("settings.quota.status.noBrowser", route?.site?.name ?? name)
        case .expired: return l10n("settings.quota.status.expired", name)
        case .denied(let host): return l10n("settings.quota.status.denied", host)
        case .failed: return l10n("settings.quota.status.failed")
        case .networkOff: return l10n("settings.quota.status.networkOff")
        case .notRunning: return l10n("settings.quota.status.notRunning", name)
        case .noCertificates: return l10n("settings.quota.status.noCertificates")
        }
    }
}
