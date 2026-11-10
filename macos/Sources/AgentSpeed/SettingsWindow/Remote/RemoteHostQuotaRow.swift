import AgentSpeedCore
import AppKit
import SwiftUI

/// The host editor's Claude Code bridge (DESIGN 13 *Host quotas*): `remote.quota.claudeCli` with its hint; turning it
/// on asks first (the host's name and the settings path on the host); under it, while the saved host has it on, the
/// bridge's state in the `settings.quota.cli.*` lines.
struct RemoteHostQuotaRow: View {
    let model: AppModel
    @Binding var draft: RemoteHostDraft
    let host: HostWindow

    var body: some View {
        let l10n = model.l10n
        Toggle(isOn: Binding(get: { draft.quotaClaudeCli }, set: { on in on ? confirm() : (draft.quotaClaudeCli = false) })) {
            VStack(alignment: .leading, spacing: 3) {
                RowTitle(l10n("remote.quota.claudeCli"))
                Footnote(l10n("remote.quota.claudeCli.hint"))
                if let state = bridgeState {
                    RemoteBridgeStatusLine(state: state, model: model, hostId: draft.original?.id ?? "")
                }
            }
        }
    }

    /// The saved host's bridge, while both it and the draft have it on.
    private var bridgeState: RemoteQuotaBridge? {
        guard draft.quotaClaudeCli, let saved = draft.original, saved.quotaClaudeCli,
              let bridge = model.quota.hostQuotas[saved.id]?.bridge, bridge.state != .off else { return nil }
        return bridge
    }

    private var hostName: String {
        if let original = draft.original { return model.settings.remoteHosts.displayName(of: original.id) ?? original.automaticName }
        let name = draft.name.trimmingCharacters(in: .whitespacesAndNewlines)
        return name.isEmpty ? draft.automaticName(in: model.settings.remoteHosts) : name
    }

    /// The path the host's relay reported, else Claude Code's default.
    static func settingsPath(_ model: AppModel, hostId: String?) -> String {
        hostId.flatMap { model.quota.hostQuotas[$0]?.bridge?.settingsPath } ?? "~/.claude/settings.json"
    }

    private func confirm() {
        let l10n = model.l10n
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = l10n("remote.quota.claudeCli.confirm.title", hostName)
        alert.informativeText = l10n("remote.quota.claudeCli.confirm.message", Self.settingsPath(model, hostId: draft.original?.id))
        alert.addButton(withTitle: l10n("settings.quota.claudeCli.confirm.ok"))
        alert.addButton(withTitle: l10n("history.cancel"))
        let handle: (NSApplication.ModalResponse) -> Void = { response in
            if response == .alertFirstButtonReturn { draft.quotaClaudeCli = true }
        }
        if let window = host.window ?? NSApp.keyWindow {
            alert.beginSheetModal(for: window, completionHandler: handle)
        } else {
            handle(alert.runModal())
        }
    }
}

/// The host bridge's state as the local bridge's lines say it (QUOTAS 5.4).
struct RemoteBridgeStatusLine: View {
    let state: RemoteQuotaBridge
    let model: AppModel
    let hostId: String

    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            indicator
                .frame(width: 12, height: 12)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
                .accessibilityHidden(true)
            Text(text)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            if state.state == .replaced {
                Button(model.l10n("settings.quota.cli.reconnect")) { model.reconnectHostBridge(hostId) }
                    .controlSize(.small)
                    .fixedSize()
            }
        }
        .font(.subheadline)
        .padding(.top, 2)
    }

    @ViewBuilder
    private var indicator: some View {
        switch state.state {
        case .off, .waiting: Image(systemName: "clock").foregroundStyle(.secondary)
        case .ok: Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .replaced, .unreadable, .failed: Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        }
    }

    private var text: String {
        let l10n = model.l10n
        switch state.state {
        case .off, .waiting: return l10n("settings.quota.cli.waiting")
        case .ok:
            let observed = model.quota.hostQuotas[hostId]?.reports[.claudeCode]?.snapshot.observedMs
            let time = observed.map { AppModel.quotaTime($0, nowMs: model.quota.clockMs, locale: l10n.locale) } ?? ""
            return l10n("settings.quota.cli.ok", time)
        case .replaced: return l10n("settings.quota.cli.replaced")
        case .unreadable: return l10n("settings.quota.cli.unreadable", state.settingsPath)
        case .failed: return l10n("settings.quota.cli.failed", state.settingsPath)
        }
    }
}
