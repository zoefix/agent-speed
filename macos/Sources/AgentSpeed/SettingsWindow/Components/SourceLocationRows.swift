import AgentSpeedCore
import AppKit
import SwiftUI

/// One entry of a source's location list (DESIGN 5): icon, path (the automatic entry may name several), the 預設 badge
/// for the automatic entry, a missing location dimmed with `source.folderMissing`, Show in Finder and remove.
struct SourceLocationRow: View {
    let folder: TranscriptFolder
    let entry: LocationEntry
    let canRemove: Bool
    let l10n: Localizer
    let onRemove: () -> Void

    private static let folderIcon = NSWorkspace.shared.icon(for: .folder)
    private static let fileIcon = NSWorkspace.shared.icon(for: .data)

    var body: some View {
        HStack(alignment: .center, spacing: Spacing.s) {
            Image(nsImage: isFile ? Self.fileIcon : Self.folderIcon)
                .resizable()
                .interpolation(.high)
                .frame(width: 22, height: 22)
                .opacity(entry.exists ? 1 : 0.45)
                .accessibilityHidden(true)
            VStack(alignment: .leading, spacing: 3) {
                paths
                if !entry.exists {
                    Label {
                        Text(l10n("source.folderMissing"))
                    } icon: {
                        Image(systemName: "exclamationmark.triangle.fill")
                            .foregroundStyle(.orange)
                            .imageScale(.small)
                    }
                    .labelStyle(MissingLabelStyle())
                    .font(.subheadline)
                    .foregroundStyle(.secondary)
                    .accessibilityHidden(true)
                }
            }
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(l10n("mac.settings.sources.folder"))
            .accessibilityValue(Text(verbatim: accessibilityValue))
            Spacer(minLength: Spacing.xs)
            buttons
        }
        .padding(.vertical, 1)
        .contextMenu {
            Button(l10n("mac.settings.sources.reveal"), action: reveal)
                .disabled(entry.revealURL == nil)
            Button(l10n("settings.sources.remove"), action: onRemove)
                .disabled(!canRemove)
        }
    }

    private var isFile: Bool {
        guard case .path(let path) = entry.location else { return false }
        return (path as NSString).lastPathComponent == FolderChooser.openclawDatabaseName
    }

    /// One line per folder named; the automatic entry's badge follows its first line.
    private var paths: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(Array(entry.shownURLs.enumerated()), id: \.offset) { index, url in
                let readable = folder.isReadable(url)
                HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                    Text(verbatim: TranscriptFolder.displayPath(url))
                        .lineLimit(1)
                        .truncationMode(.middle)
                        .textSelection(.enabled)
                        .foregroundStyle(readable || (entry.shownURLs.count == 1 && entry.exists)
                                         ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                        .help(url.path)
                    if index == 0, entry.isAutomatic {
                        DefaultLocationBadge(text: l10n("settings.sources.default"))
                            .layoutPriority(1)
                    }
                }
            }
        }
    }

    private var buttons: some View {
        let revealTitle = l10n("mac.settings.sources.reveal")
        let path = entry.shownURLs.first.map(TranscriptFolder.displayPath) ?? ""
        return HStack(spacing: Spacing.xxs) {
            Button(action: reveal) {
                Image(systemName: "magnifyingglass")
                    .frame(width: 22, height: 22)
                    .contentShape(Rectangle())
            }
            .help(revealTitle)
            .accessibilityLabel(revealTitle)
            .accessibilityValue(Text(verbatim: path))
            .disabled(entry.revealURL == nil)
            Button(action: onRemove) {
                Image(systemName: "minus.circle")
                    .frame(width: 22, height: 22)
                    .contentShape(Rectangle())
            }
            .help(canRemove ? l10n("settings.sources.remove") : l10n("settings.sources.keepOne"))
            .accessibilityLabel(l10n("settings.sources.removeLocation", path))
            .disabled(!canRemove)
        }
        .buttonStyle(.borderless)
        .imageScale(.medium)
        .fixedSize()
    }

    private var accessibilityValue: String {
        var parts = entry.shownURLs.map(TranscriptFolder.displayPath)
        if entry.isAutomatic { parts.append(l10n("settings.sources.default")) }
        if !entry.exists { parts.append(l10n("source.folderMissing")) }
        return parts.joined(separator: ", ")
    }

    private func reveal() {
        guard let url = entry.revealURL else { return }
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }
}

/// Add Folder… and Reset below the entries (DESIGN 5); at the limit Add is disabled and the limit reads beside it.
struct SourceLocationActions: View {
    let canAdd: Bool
    let canReset: Bool
    let l10n: Localizer
    let onAdd: () -> Void
    let onReset: () -> Void

    var body: some View {
        let limit = l10n("settings.sources.limit", SourceLocations.maxEntries)
        HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
            Button(l10n("settings.sources.add"), action: onAdd)
                .disabled(!canAdd)
                .help(canAdd ? "" : limit)
            Button(l10n("settings.sources.reset"), action: onReset)
                .disabled(!canReset)
            Spacer(minLength: Spacing.xs)
            if !canAdd {
                Footnote(limit)
                    .lineLimit(1)
                    .transition(.opacity)
            }
        }
    }
}

/// 預設: the automatic entry's mark.
private struct DefaultLocationBadge: View {
    let text: String
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        Text(text)
            .font(.caption.weight(.medium))
            .foregroundStyle(.secondary)
            .lineLimit(1)
            .fixedSize()
            .padding(.horizontal, 6)
            .padding(.vertical, 1)
            .background {
                Capsule().fill(Color.primary.opacity(0.07))
            }
            .overlay {
                if contrast == .increased {
                    Capsule().strokeBorder(Color.primary.opacity(0.35), lineWidth: 1)
                }
            }
    }
}

private struct MissingLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            configuration.icon
            configuration.title
        }
    }
}
