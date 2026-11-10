import AgentSpeedCore
import AppKit
import SwiftUI

/// The 額度 switches of a source group in Settings → Data Sources (DESIGN 5, *額度*), after its source toggles:
/// Codex in the Codex group; Claude Desktop and the Claude CLI's status line bridge in the Claude group.
struct QuotaSwitches: View {
    let folder: TranscriptFolder
    let model: AppModel

    var body: some View {
        let settings = model.settings
        let l10n = model.l10n
        switch folder {
        case .codex:
            QuotaToggleRow(title: l10n("settings.quota.codex"), hint: l10n("settings.quota.codex.hint"),
                           isOn: Binding(get: { settings.quotaCodex }, set: { settings.quotaCodex = $0 }))
        case .claude:
            QuotaToggleRow(title: l10n("settings.quota.claudeDesktop"), hint: l10n("settings.quota.claudeDesktop.hint"),
                           isOn: Binding(get: { settings.quotaClaudeDesktop }, set: { settings.quotaClaudeDesktop = $0 }))
            ClaudeCliQuotaRow(model: model)
        default:
            EmptyView()
        }
    }
}

/// A quota switch: its title and hint aligned with the source names above it, and an optional status line.
struct QuotaToggleRow<Status: View>: View {
    let title: String
    let hint: String
    @Binding var isOn: Bool
    @ViewBuilder var status: Status

    var body: some View {
        Toggle(isOn: $isOn) {
            HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                // The source dot's room, so the switch reads as part of its source.
                Image(systemName: "circle.fill")
                    .font(.system(size: 8))
                    .hidden()
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    RowTitle(title)
                    Footnote(hint)
                    status
                }
            }
        }
    }
}

extension QuotaToggleRow where Status == EmptyView {
    init(title: String, hint: String, isOn: Binding<Bool>) {
        self.init(title: title, hint: hint, isOn: isOn) { EmptyView() }
    }
}

/// The Claude CLI's switch: turning it on asks first (a sheet on Settings), turning it off restores the status line at
/// once; under it the bridge's status (QUOTAS 5.4) or why turning it on changed nothing.
private struct ClaudeCliQuotaRow: View {
    let model: AppModel
    @State private var host = HostWindow()

    var body: some View {
        let quota = model.quota
        let l10n = model.l10n
        QuotaToggleRow(
            title: l10n("settings.quota.claudeCli"), hint: l10n("settings.quota.claudeCli.hint"),
            isOn: Binding(get: { model.settings.quotaClaudeCli }, set: { on in
                if on { confirm() } else { quota.turnOffCli() }
            })
        ) {
            QuotaBridgeStatusLine(model: model)
        }
        .background(WindowReader(host: host))
        .onAppear {
            quota.refreshBridgeStatus()
            if quota.confirmsCli { DispatchQueue.main.async { confirm() } }
        }
        .onChange(of: quota.confirmsCli) { _, asked in
            if asked { confirm() }
        }
    }

    /// `settings.quota.claudeCli.confirm.*` as a sheet on Settings; Connect is the default button.
    private func confirm() {
        let quota = model.quota
        quota.confirmsCli = false
        guard !model.settings.quotaClaudeCli else { return }
        let l10n = model.l10n
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = l10n("settings.quota.claudeCli.confirm.title")
        alert.informativeText = l10n("settings.quota.claudeCli.confirm.message", quota.settingsPath)
        alert.addButton(withTitle: l10n("settings.quota.claudeCli.confirm.ok"))
        alert.addButton(withTitle: l10n("history.cancel"))
        let handle: (NSApplication.ModalResponse) -> Void = { response in
            if response == .alertFirstButtonReturn { quota.turnOnCli() }
        }
        if let window = host.window ?? NSApp.keyWindow {
            alert.beginSheetModal(for: window, completionHandler: handle)
        } else {
            handle(alert.runModal())
        }
    }
}

/// What the bridge is doing (DESIGN 5): connected and waiting, connected with the quota's time, replaced by another
/// setting (with Reconnect), or a file it could not read or write; problems in the warning style of `Notice` (an
/// orange symbol before secondary text).
struct QuotaBridgeStatusLine: View {
    let model: AppModel

    var body: some View {
        if let state {
            HStack(alignment: .firstTextBaseline, spacing: 5) {
                indicator(state)
                    .frame(width: 12, height: 12)
                    .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
                    .accessibilityHidden(true)
                Text(text(state))
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                    .contentTransition(.opacity)
                if state == .replaced {
                    Button(model.l10n("settings.quota.cli.reconnect")) { model.quota.turnOnCli() }
                        .controlSize(.small)
                        .fixedSize()
                }
            }
            .font(.subheadline)
            .padding(.top, 2)
            .animation(Motion.fade, value: state)
            .transition(.opacity)
        }
    }

    enum State: Equatable {
        case checking, waiting, ok(Int64?), replaced, unreadable, failed
    }

    var state: State? {
        let quota = model.quota
        if !model.settings.quotaClaudeCli {
            switch quota.bridgeFailure {
            case .unreadable: return .unreadable
            case .failed: return .failed
            case nil: return nil
            }
        }
        switch quota.bridgeStatus {
        case nil: return .checking
        case .waiting: return .waiting
        case .ok: return .ok(quota.cliReceivedMs)
        case .replaced: return .replaced
        case .unreadable: return .unreadable
        }
    }

    @ViewBuilder
    private func indicator(_ state: State) -> some View {
        switch state {
        case .checking: ProgressView().controlSize(.mini)
        case .waiting: Image(systemName: "clock").foregroundStyle(.secondary)
        case .ok: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .replaced, .unreadable, .failed: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        }
    }

    private func text(_ state: State) -> String {
        let l10n = model.l10n
        let path = model.quota.settingsPath
        switch state {
        case .checking, .waiting: return l10n("settings.quota.cli.waiting")
        case .ok(let received):
            let time = received.map { AppModel.quotaTime($0, nowMs: model.quota.clockMs, locale: l10n.locale) } ?? ""
            return l10n("settings.quota.cli.ok", time)
        case .replaced: return l10n("settings.quota.cli.replaced")
        case .unreadable: return l10n("settings.quota.cli.unreadable", path)
        case .failed: return l10n("settings.quota.cli.failed", path)
        }
    }
}
