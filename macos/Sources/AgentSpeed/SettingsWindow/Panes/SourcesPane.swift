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
                let kinds = SourceKind.allCases.filter { $0.transcriptFolder == folder }
                Section {
                    ForEach(kinds) { kind in
                        SourceToggleRow(kind: kind, model: model)
                    }
                    SourceFolderRow(folder: folder, model: model)
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

extension SourceKind {
    /// The folder a source is read from.
    var transcriptFolder: TranscriptFolder {
        switch self {
        case .codex: .codex
        case .claudeCode, .claudeDesktop: .claude
        case .deepseek: .dsh
        case .opencode: .opencode
        case .gemini: .gemini
        case .omp: .omp
        case .antigravity: .antigravity
        case .workbuddy: .workbuddy
        case .workbuddyAi: .workbuddyAi
        case .zcode: .zcode
        case .kimi: .kimi
        case .grokBuild: .grokBuild
        case .pi: .pi
        case .openclaw: .openclaw
        case .hermes: .hermes
        case .droid: .droid
        case .trae: .trae
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
        self = status.available ? .sessions(status.sessionCount) : .missing
    }
}

private struct SourceToggleRow: View {
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

/// The folder a source reads from: abbreviated path (Gemini CLI lists both default folders, Antigravity the stores
/// found), default/custom badge, and Show in Finder / Reset / Change….
private struct SourceFolderRow: View {
    let folder: TranscriptFolder
    let model: AppModel
    @State private var host = HostWindow()

    private static let folderIcon = NSWorkspace.shared.icon(for: .folder)

    var body: some View {
        let settings = model.settings
        let l10n = model.l10n
        let urls = folder.urls(in: settings)
        let shown = folder.shownURLs(in: settings)
        let url = urls.first(where: folder.isReadable) ?? urls.first(where: folder.locationExists) ?? shown[0]
        let isCustom = folder.customURL(in: settings) != nil
        let readable = urls.contains(where: folder.isReadable)
        let exists = folder.locationExists(url)
        let paths = shown.map(TranscriptFolder.displayPath)
        HStack(spacing: Spacing.s) {
            Image(nsImage: Self.folderIcon)
                .resizable()
                .frame(width: 28, height: 28)
                .opacity(readable ? 1 : 0.45)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                VStack(alignment: .leading, spacing: 3) {
                    ForEach(Array(shown.enumerated()), id: \.offset) { _, candidate in
                        Text(verbatim: TranscriptFolder.displayPath(candidate))
                            .lineLimit(1)
                            .truncationMode(.middle)
                            .textSelection(.enabled)
                            .foregroundStyle(shown.count > 1 && !folder.isReadable(candidate) ? AnyShapeStyle(.secondary) : AnyShapeStyle(.primary))
                            .help(candidate.path)
                    }
                }
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(l10n("mac.settings.sources.folder"))
                .accessibilityValue(Text(verbatim: paths.joined(separator: ", ")))
                LocationBadge(isCustom: isCustom, l10n: l10n)
            }
            Spacer(minLength: Spacing.xs)
            folderButtons(url: url, exists: exists, isCustom: isCustom, l10n: l10n)
        }
        .background(WindowReader(host: host))
        .contextMenu {
            Button(l10n("mac.settings.sources.reveal")) {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
            .disabled(!exists)
        }
    }

    private func folderButtons(url: URL, exists: Bool, isCustom: Bool, l10n: Localizer) -> some View {
        let settings = model.settings
        let revealTitle = l10n("mac.settings.sources.reveal")
        return HStack(spacing: Spacing.xs) {
            Button {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            } label: {
                Image(systemName: "magnifyingglass")
            }
            .buttonStyle(.borderless)
            .help(revealTitle)
            .accessibilityLabel(revealTitle)
            .disabled(!exists)
            Button(l10n("settings.sources.reset")) {
                folder.setURL(nil, in: settings)
            }
            .disabled(!isCustom)
            Button(l10n("settings.sources.change")) {
                FolderChooser.choose(
                    message: l10n(folder.chooseMessageKey),
                    prompt: l10n("mac.settings.sources.choosePrompt"),
                    startingAt: url,
                    window: host.window ?? NSApp.keyWindow,
                    acceptsDatabaseFile: folder.acceptsDatabaseFile
                ) { chosen in
                    folder.setURL(chosen, in: settings)
                }
            }
        }
        .fixedSize()
    }
}

private struct LocationBadge: View {
    let isCustom: Bool
    let l10n: Localizer
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        Text(l10n(isCustom ? "mac.settings.sources.custom" : "mac.settings.sources.default"))
            .font(.caption.weight(.medium))
            .foregroundStyle(isCustom ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
            .padding(.horizontal, 6)
            .padding(.vertical, 1)
            .background {
                Capsule().fill(isCustom ? Color.accentColor.opacity(0.18) : Color.primary.opacity(0.07))
            }
            .overlay {
                if contrast == .increased {
                    Capsule().strokeBorder(Color.primary.opacity(0.35), lineWidth: 1)
                }
            }
    }
}
