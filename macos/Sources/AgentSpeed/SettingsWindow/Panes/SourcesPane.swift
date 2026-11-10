import AgentSpeedCore
import AppKit
import SwiftUI
import UniformTypeIdentifiers

struct SourcesPane: View {
    let model: AppModel

    @State private var confirmsClear = false

    var body: some View {
        let l10n = model.l10n
        Form {
            ForEach(TranscriptFolder.allCases, id: \.self) { folder in
                Section {
                    ForEach(folder.sources) { kind in
                        SourceToggleRow(kind: kind, model: model)
                    }
                    QuotaSwitches(folder: folder, model: model)
                    SourceLocationList(folder: folder, model: model)
                } header: {
                    if folder == TranscriptFolder.allCases.first {
                        SectionFootnote(l10n("settings.sources.locationsHint"))
                    }
                } footer: {
                    if let key = folder.footnoteKey {
                        SectionFootnote(l10n(key))
                    }
                }
            }
            Section {
                HStack(spacing: Spacing.xs) {
                    Button(l10n("history.open")) {
                        model.commands?.showHistory(source: nil)
                    }
                    Spacer(minLength: Spacing.xs)
                    Button(l10n("history.clear")) {
                        confirmsClear = true
                    }
                }
            } footer: {
                SectionFootnote(l10n("history.privacy"))
            }
        }
        .settingsForm()
        .alert(l10n("mac.history.clear.title"), isPresented: $confirmsClear) {
            Button(l10n("history.clear.action"), role: .destructive) {
                model.clearHistory()
            }
            Button(l10n("history.cancel"), role: .cancel) {}
        } message: {
            Text(model.traffic.days.isEmpty ? l10n("history.clear.confirm")
                 : l10n("history.clear.confirm") + "\n\n" + l10n("history.clear.traffic"))
        }
    }
}

/// What the settings window says about one source.
enum SourceState: Equatable {
    case checking
    case sessions(Int)
    case missing
    case disabled

    @MainActor
    init(kind: SourceKind, model: AppModel) {
        guard model.settings.isEnabled(kind) else {
            self = .disabled
            return
        }
        let snapshot = model.store.snapshot
        if case .starting = snapshot.state {
            self = .checking
            return
        }
        guard let status = snapshot.sources.first(where: { $0.kind == kind }), status.enabled else {
            self = snapshot.state == .noSources ? .missing : .checking
            return
        }
        // DESIGN 13: `sources` adds every connected host; this pane is about this Mac's own folders.
        if snapshot.hosts.count > 1 {
            let local = snapshot.hostSources.first { $0.cell.host == RemoteHost.localId && $0.cell.source == kind }
            self = local.map { $0.available ? .sessions($0.sessionCount) : .missing } ?? .missing
            return
        }
        self = status.available ? .sessions(status.sessionCount) : .missing
    }
}

struct SourceToggleRow: View {
    let kind: SourceKind
    let model: AppModel
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let settings = model.settings
        let l10n = model.l10n
        Toggle(isOn: Binding(
            get: { settings.isEnabled(kind) },
            set: { settings.setEnabled(kind, $0) }
        )) {
            HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                Image(systemName: "circle.fill")
                    .font(.system(size: 8))
                    .foregroundStyle(kind.tint(dark: colorScheme == .dark))
                    .accessibilityHidden(true)
                VStack(alignment: .leading, spacing: 3) {
                    Text(l10n(kind.titleKey))
                    SourceStatusLabel(state: SourceState(kind: kind, model: model), l10n: l10n)
                }
            }
        }
    }
}

private struct SourceStatusLabel: View {
    let state: SourceState
    let l10n: Localizer

    var body: some View {
        HStack(spacing: 5) {
            indicator
                .frame(width: 12, height: 12)
                .accessibilityHidden(true)
            Text(text)
                .foregroundStyle(.secondary)
        }
        .font(.subheadline)
    }

    @ViewBuilder
    private var indicator: some View {
        switch state {
        case .checking:
            ProgressView().controlSize(.mini)
        case .sessions(let count) where count > 0:
            Image(systemName: "checkmark.circle.fill").foregroundStyle(.green)
        case .sessions:
            Image(systemName: "circle.dashed").foregroundStyle(.secondary)
        case .missing:
            Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(.orange)
        case .disabled:
            Image(systemName: "minus.circle.fill").foregroundStyle(.tertiary)
        }
    }

    private var text: String {
        switch state {
        case .checking: l10n("mac.settings.sources.checking")
        case .sessions(let count): l10n("source.sessions", count)
        case .missing: l10n("source.folderMissing")
        case .disabled: l10n("source.disabled")
        }
    }
}

/// A source's location list (DESIGN 5, METRICS 2.1): one row per entry, then Add Folder… and Reset. A pick that is in
/// the list already, that the automatic entry reads already, or beyond the limit changes nothing and says why.
private struct SourceLocationList: View {
    let folder: TranscriptFolder
    let model: AppModel
    @State private var host = HostWindow()

    var body: some View {
        let settings = model.settings
        let l10n = model.l10n
        let entries = folder.entries(in: settings)
        let locations = settings.sourceLocations
        ForEach(entries) { entry in
            SourceLocationRow(folder: folder, entry: entry, canRemove: locations.canRemove(from: folder), l10n: l10n) {
                withAnimation(model.accessibility.reduceMotion ? nil : Motion.layout) {
                    folder.remove(entry.location, in: settings)
                }
            }
        }
        SourceLocationActions(canAdd: locations.canAdd(to: folder), canReset: locations.canReset(folder), l10n: l10n,
                              onAdd: { choose(startingAt: entries.last?.revealURL ?? folder.defaultURL) },
                              onReset: {
                                  withAnimation(model.accessibility.reduceMotion ? nil : Motion.layout) {
                                      folder.reset(in: settings)
                                  }
                              })
            .background(WindowReader(host: host))
    }

    private func choose(startingAt start: URL) {
        let l10n = model.l10n
        let window = host.window ?? NSApp.keyWindow
        FolderChooser.choose(
            message: l10n(folder.chooseMessageKey),
            prompt: l10n("mac.settings.sources.choosePrompt"),
            startingAt: start,
            window: window,
            acceptsDatabaseFile: folder.acceptsDatabaseFile
        ) { chosen in
            var outcome = SourceLocations.AddOutcome.invalid
            withAnimation(model.accessibility.reduceMotion ? nil : Motion.layout) {
                outcome = folder.add(chosen, in: model.settings)
            }
            explain(outcome, chosen: chosen, window: window)
        }
    }

    /// Why a pick changed nothing, as a sheet on Settings.
    private func explain(_ outcome: SourceLocations.AddOutcome, chosen: URL, window: NSWindow?) {
        let l10n = model.l10n
        let message: String
        switch outcome {
        case .added, .restoredAutomatic: return
        case .invalid:
            NSSound.beep()
            return
        case .alreadyListed: message = l10n("settings.sources.alreadyListed")
        case .alreadyAutomatic: message = l10n("settings.sources.alreadyDefault")
        case .full: message = l10n("settings.sources.limit", SourceLocations.maxEntries)
        }
        let alert = NSAlert()
        alert.alertStyle = .informational
        alert.messageText = message
        alert.informativeText = TranscriptFolder.displayPath(chosen)
        alert.addButton(withTitle: l10n("mac.update.ok"))
        // After the open panel's own sheet has ended.
        DispatchQueue.main.async {
            if let window {
                alert.beginSheetModal(for: window)
            } else {
                alert.runModal()
            }
        }
    }
}
