import AgentSpeedCore
import AppKit
import SwiftUI

/// Settings → API → 區域網路 (DESIGN 14): devices read this Mac, this Mac sends to devices, and what is sent.
struct LanPane: View {
    let model: AppModel

    /// The sheet's draft; previews pass one to render the sheet over the pane's data.
    @State private var editing: LanTargetDraft?
    @State private var host = HostWindow()

    static let docsURL = URL(string: "https://github.com/zoefix/agent-speed/blob/main/spec/LAN.md")!

    var body: some View {
        let l10n = model.l10n
        let document = model.settings.lan
        Form {
            LanServerSection(model: model)
            Section {
                if document.targets.isEmpty {
                    LanTargetsEmptyState(model: model) { editing = LanTargetDraft() }
                } else {
                    ForEach(document.targets) { target in
                        LanTargetRow(model: model, target: target,
                                     onEdit: { editing = LanTargetDraft(editing: target) },
                                     onRemove: { confirmRemoval(of: target) })
                    }
                    addRow(document: document, l10n: l10n)
                }
            } header: {
                HStack(alignment: .firstTextBaseline) {
                    Text(l10n("lan.targets"))
                    Spacer()
                    if !document.targets.isEmpty {
                        Text(l10n("orbs.count", document.targets.count, LanDocument.maxTargets))
                            .font(.subheadline)
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                            .accessibilityHidden(true)
                    }
                }
            } footer: {
                SectionFootnote(l10n("mac.lan.localNetwork"))
            }
            Section {
                VStack(alignment: .leading, spacing: Spacing.xs) {
                    Footnote(l10n("lan.footnote"))
                        .multilineTextAlignment(.leading)
                    Link(destination: Self.docsURL) {
                        HStack(spacing: 3) {
                            Text(l10n("lan.docs"))
                            Image(systemName: "arrow.up.right")
                                .font(.system(size: 9, weight: .semibold))
                        }
                        .font(.subheadline)
                    }
                    .buttonStyle(.link)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
            }
        }
        .settingsForm()
        .background(WindowReader(host: host))
        .animation(model.accessibility.reduceMotion ? nil : Motion.layout, value: document.targets.map(\.id))
        .sheet(item: $editing) { draft in
            LanTargetSheet(model: model, draft: draft) { editing = nil }
        }
    }

    /// Add Device…, disabled at the limit, which then also reads beside it.
    private func addRow(document: LanDocument, l10n: Localizer) -> some View {
        let limit = l10n("lan.targets.limit", LanDocument.maxTargets)
        return HStack(alignment: .firstTextBaseline, spacing: Spacing.s) {
            if !document.canAdd {
                Footnote(limit)
                    .transition(.opacity)
            }
            Spacer(minLength: Spacing.xs)
            Button(l10n("lan.targets.add")) { editing = LanTargetDraft() }
                .disabled(!document.canAdd)
                .help(document.canAdd ? "" : limit)
        }
    }

    private func confirmRemoval(of target: LanDocument.Target) {
        let l10n = model.l10n
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = l10n("lan.target.remove.confirm", target.displayName)
        let remove = alert.addButton(withTitle: l10n("remote.remove.button"))
        remove.hasDestructiveAction = true
        alert.addButton(withTitle: l10n("history.cancel"))
        let lan = model.lan
        let id = target.id
        if let window = host.window {
            alert.beginSheetModal(for: window) { response in
                if response == .alertFirstButtonReturn { MainActor.assumeIsolated { lan.remove(id) } }
            }
        } else if alert.runModal() == .alertFirstButtonReturn {
            lan.remove(id)
        }
    }
}

/// 讓裝置讀取: the switch, then while on the status, the access key, the addresses and the port (DESIGN 14).
private struct LanServerSection: View {
    let model: AppModel

    var body: some View {
        let l10n = model.l10n
        let document = model.settings.lan
        let network = model.settings.networkEnabled
        Section {
            Toggle(isOn: Binding(get: { document.server.enabled }, set: { model.lan.setServerEnabled($0) })) {
                VStack(alignment: .leading, spacing: 2) {
                    RowTitle(l10n("lan.server.enabled"))
                    Footnote(network ? l10n("lan.server.hint") : l10n("settings.network.offHint"))
                }
            }
            .disabled(!network)
            if document.server.enabled {
                LanServerStatusRow(model: model)
                if let key = document.server.key {
                    LanKeyRow(model: model, key: key)
                }
                LanAddressRows(model: model, port: document.server.port, key: document.server.key ?? "")
                LanPortRow(model: model)
            }
        } header: {
            Text(l10n("lan.server"))
        }
        .animation(model.accessibility.reduceMotion ? nil : Motion.layout, value: document.server.enabled)
    }
}

private struct LanServerStatusRow: View {
    let model: AppModel

    var body: some View {
        let (text, kind) = LanStatusText.server(model.lan.serverStatus, network: model.settings.networkEnabled,
                                                port: model.settings.lan.server.port, l10n: model.l10n)
        HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
            RemoteStatusDot(kind: kind, reduceMotion: model.accessibility.reduceMotion)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
            Text(text)
                .foregroundStyle(.secondary)
                .contentTransition(.opacity)
                .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: text)
            Spacer(minLength: 0)
        }
        .accessibilityElement(children: .combine)
    }
}

/// The access key in groups of four, a copy button and Regenerate….
private struct LanKeyRow: View {
    let model: AppModel
    let key: String

    @State private var copied = false
    @State private var reset: Task<Void, Never>?

    var body: some View {
        let l10n = model.l10n
        LabeledContent {
            HStack(spacing: Spacing.xs) {
                Text(verbatim: LanDocument.displayKey(key))
                    .font(.system(size: 13, weight: .medium, design: .monospaced))
                    .tracking(0.6)
                    .textSelection(.enabled)
                    .accessibilityLabel(l10n("lan.key"))
                    .accessibilityValue(LanDocument.displayKey(key))
                Button(action: copy) {
                    Image(systemName: copied ? "checkmark" : "doc.on.doc")
                        .font(.system(size: 12, weight: .medium))
                        .frame(width: 16, height: 16)
                        .contentTransition(.symbolEffect(.replace))
                }
                .buttonStyle(.borderless)
                .help(copied ? l10n("lan.key.copied") : l10n("lan.key.copy"))
                .accessibilityLabel(l10n("lan.key.copy"))
                Button(l10n("lan.key.regenerate"), action: confirmRegenerate)
                    .buttonStyle(.borderless)
            }
        } label: {
            RowTitle(l10n("lan.key"))
        }
    }

    private func copy() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(LanDocument.displayKey(key), forType: .string)
        copied = true
        reset?.cancel()
        reset = Task { @MainActor in
            try? await Task.sleep(for: .seconds(1.5))
            guard !Task.isCancelled else { return }
            copied = false
        }
    }

    private func confirmRegenerate() {
        let l10n = model.l10n
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = l10n("lan.key.regenerate.confirm")
        alert.informativeText = l10n("lan.key.regenerate.detail")
        alert.addButton(withTitle: l10n("history.cancel"))
        let regenerate = alert.addButton(withTitle: l10n("lan.key.regenerate").replacingOccurrences(of: "…", with: ""))
        regenerate.hasDestructiveAction = true
        let lan = model.lan
        if let window = NSApp.keyWindow {
            alert.beginSheetModal(for: window) { response in
                if response == .alertSecondButtonReturn { MainActor.assumeIsolated { lan.regenerateKey() } }
            }
        } else if alert.runModal() == .alertSecondButtonReturn {
            lan.regenerateKey()
        }
    }
}

/// One line per local IPv4 address with a Copy menu (HTTP and WebSocket URLs with the key, the TCP address).
private struct LanAddressRows: View {
    let model: AppModel
    let port: Int
    let key: String

    var body: some View {
        let l10n = model.l10n
        LabeledContent {
            VStack(alignment: .trailing, spacing: 4) {
                if model.lan.addresses.isEmpty {
                    Text(l10n("lan.addresses.none"))
                        .foregroundStyle(.secondary)
                }
                ForEach(model.lan.addresses, id: \.self) { address in
                    HStack(spacing: Spacing.xs) {
                        Text(verbatim: "\(address):\(port)")
                            .font(.system(size: 13).monospacedDigit())
                            .foregroundStyle(.secondary)
                            .textSelection(.enabled)
                        Menu {
                            Button(l10n("lan.copy.http")) { copy("http://\(address):\(port)/v1/live?key=\(display)") }
                            Button(l10n("lan.copy.ws")) { copy("ws://\(address):\(port)/v1/stream?key=\(display)") }
                            Button(l10n("lan.copy.tcp")) { copy("\(address):\(port)") }
                        } label: {
                            Text(l10n("lan.copy"))
                        }
                        .menuStyle(.borderlessButton)
                        .fixedSize()
                        .accessibilityLabel(l10n("lan.copy"))
                    }
                }
            }
        } label: {
            RowTitle(l10n("lan.addresses"))
        }
    }

    private var display: String { LanDocument.displayKey(key) }

    private func copy(_ text: String) {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(text, forType: .string)
    }
}

/// The port, applied on Return or when focus leaves; an invalid value is shown and not applied.
private struct LanPortRow: View {
    let model: AppModel

    @State private var text = ""
    @State private var invalid = false
    @FocusState private var focused: Bool

    var body: some View {
        let l10n = model.l10n
        LabeledContent {
            VStack(alignment: .trailing, spacing: 4) {
                TextField(l10n("lan.port"), text: $text)
                    .labelsHidden()
                    .textFieldStyle(.roundedBorder)
                    .multilineTextAlignment(.trailing)
                    .font(.system(size: 13).monospacedDigit())
                    .frame(width: 72)
                    .focused($focused)
                    .onSubmit(commit)
                    .accessibilityLabel(l10n("lan.port"))
                if invalid {
                    Label(l10n("lan.port.invalid"), systemImage: "exclamationmark.circle.fill")
                        .symbolRenderingMode(.multicolor)
                        .font(.system(size: 11))
                        .transition(.opacity)
                }
            }
        } label: {
            RowTitle(l10n("lan.port"))
        }
        .onAppear { text = String(model.settings.lan.server.port) }
        .onChange(of: focused) { _, isFocused in if !isFocused { commit() } }
        .onChange(of: text) { _, _ in if invalid { invalid = false } }
        .animation(Motion.fade, value: invalid)
    }

    private func commit() {
        let trimmed = text.trimmingCharacters(in: .whitespaces)
        guard let port = Int(trimmed), model.lan.setPort(port) else {
            invalid = true
            return
        }
        invalid = false
        text = String(port)
    }
}

private struct LanTargetsEmptyState: View {
    let model: AppModel
    let onAdd: () -> Void

    var body: some View {
        let l10n = model.l10n
        VStack(spacing: Spacing.xs) {
            Image(systemName: "dot.radiowaves.left.and.right")
                .font(.system(size: 22, weight: .regular))
                .foregroundStyle(.tertiary)
                .accessibilityHidden(true)
            Text(l10n("lan.targets.empty"))
                .font(.system(size: 13, weight: .semibold))
            Text(l10n("lan.targets.empty.hint"))
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: 380)
            Button(l10n("lan.targets.add"), action: onAdd)
                .padding(.top, Spacing.xxs)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, Spacing.m)
    }
}
