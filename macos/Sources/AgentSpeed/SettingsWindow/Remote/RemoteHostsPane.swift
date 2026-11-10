import AgentSpeedCore
import AppKit
import SwiftUI

/// Settings → 遠端主機 (DESIGN 13): one row per host in the order added, Add Host… below them and what is sent.
struct RemoteHostsPane: View {
    let model: AppModel

    /// The sheet's draft; previews pass one to render the sheet over the pane's data.
    @State private var editing: RemoteHostDraft?
    @State private var host = HostWindow()

    var body: some View {
        let l10n = model.l10n
        let document = model.settings.remoteHosts
        let names = document.displayNames()
        Form {
            Section {
                if document.hosts.isEmpty {
                    RemoteHostsEmptyState(model: model) { editing = RemoteHostDraft() }
                } else {
                    ForEach(document.hosts) { remote in
                        RemoteHostRow(model: model, host: remote, name: names[remote.id] ?? remote.automaticName,
                                      onEdit: { editing = RemoteHostDraft(editing: remote) },
                                      onRemove: { confirmRemoval(of: remote, name: names[remote.id] ?? remote.automaticName) })
                    }
                    addRow(document: document, l10n: l10n)
                }
            } header: {
                HStack(alignment: .firstTextBaseline) {
                    Text(l10n("remote.hosts"))
                    Spacer()
                    if !document.hosts.isEmpty {
                        Text(l10n("orbs.count", document.hosts.count, RemoteHostsDocument.maxHosts))
                            .font(.subheadline)
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                            .accessibilityHidden(true)
                    }
                }
            } footer: {
                SectionFootnote(l10n("remote.footnote"))
            }
        }
        .settingsForm()
        .background(WindowReader(host: host))
        .animation(model.accessibility.reduceMotion ? nil : Motion.layout, value: document.hosts.map(\.id))
        .sheet(item: $editing) { draft in
            RemoteHostSheet(model: model, draft: draft) { editing = nil }
        }
    }

    /// Add Host…, disabled at the limit, which then also reads beside it.
    private func addRow(document: RemoteHostsDocument, l10n: Localizer) -> some View {
        let limit = l10n("remote.limit", RemoteHostsDocument.maxHosts)
        return HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            if !document.canAdd {
                Footnote(limit)
                    .transition(.opacity)
            }
            Spacer(minLength: Spacing.xs)
            Button(l10n("remote.add")) { editing = RemoteHostDraft() }
                .disabled(!document.canAdd)
                .help(document.canAdd ? "" : limit)
        }
    }

    /// `remote.remove.confirm` as a sheet on Settings, with *Also delete its usage history* (off) and where the relay
    /// files stay (REMOTE 2.4).
    private func confirmRemoval(of remote: RemoteHost, name: String) {
        let l10n = model.l10n
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = l10n("remote.remove.confirm", name)
        alert.informativeText = l10n("remote.remove.files", remote.effectiveRelayFolder)
            + (remote.quotaClaudeCli ? "\n\n" + l10n("remote.quota.removeNote", remote.effectiveRelayFolder) : "")
        let remove = alert.addButton(withTitle: l10n("remote.remove.button"))
        remove.hasDestructiveAction = true
        alert.addButton(withTitle: l10n("history.cancel"))
        alert.showsSuppressionButton = true
        alert.suppressionButton?.title = l10n("remote.remove.deleteHistory")
        alert.suppressionButton?.state = .off
        let id = remote.id
        let deleteHistory = alert.suppressionButton
        let finish: (NSApplication.ModalResponse) -> Void = { [weak model] response in
            guard response == .alertFirstButtonReturn else { return }
            model?.removeRemoteHost(id, deleteHistory: deleteHistory?.state == .on)
        }
        if let window = host.window ?? NSApp.keyWindow {
            alert.beginSheetModal(for: window, completionHandler: finish)
        } else {
            finish(alert.runModal())
        }
    }
}

extension RemoteHostDraft: Identifiable {
    var id: String { original?.id ?? "new" }
}

/// No host yet: what this pane is for, and the way to add the first one.
private struct RemoteHostsEmptyState: View {
    let model: AppModel
    let onAdd: () -> Void

    var body: some View {
        let l10n = model.l10n
        VStack(spacing: Spacing.xs) {
            Image(systemName: "server.rack")
                .font(.system(size: 26, weight: .regular))
                .foregroundStyle(.tertiary)
                .padding(.bottom, 2)
                .accessibilityHidden(true)
            Text(l10n("remote.empty"))
                .font(.system(size: 13, weight: .semibold))
            Text(l10n("remote.empty.hint"))
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 440)
            Button(l10n("remote.add"), action: onAdd)
                .padding(.top, Spacing.xxs)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, Spacing.m)
        .accessibilityElement(children: .contain)
    }
}
