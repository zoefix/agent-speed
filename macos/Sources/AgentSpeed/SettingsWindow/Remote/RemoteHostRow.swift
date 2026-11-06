import AgentSpeedCore
import AppKit
import SwiftUI

/// One remote host (DESIGN 13): status dot, name and destination, the status line (with the clock note), the actions
/// menu and the `enabled` switch. The context menu offers the same actions.
struct RemoteHostRow: View {
    let model: AppModel
    let host: RemoteHost
    let name: String
    let onEdit: () -> Void
    let onRemove: () -> Void

    var body: some View {
        let l10n = model.l10n
        let status = model.remoteStatus(of: host)
        let kind = RemoteDotKind(status.status)
        let canRetry = (kind == .retrying || kind == .attention) && status.status != .invalidDestination
        HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
            RemoteStatusDot(kind: kind, reduceMotion: model.accessibility.reduceMotion)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                    Text(name)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(host.enabled ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                        .lineLimit(1)
                        .layoutPriority(1)
                    if name != host.destination {
                        Text(verbatim: host.destination)
                            .font(.system(size: 12).monospacedDigit())
                            .foregroundStyle(.secondary)
                            .lineLimit(1)
                            .truncationMode(.middle)
                    }
                }
                RemoteHostStatusLine(model: model, host: host, status: status)
            }
            .accessibilityElement(children: .combine)
            Spacer(minLength: Spacing.s)
            RemoteHostActionsMenu(model: model, host: host, canRetry: canRetry, onEdit: onEdit, onRemove: onRemove)
            Toggle(isOn: Binding(
                get: { host.enabled },
                set: { model.setRemoteHostEnabled($0, id: host.id) }
            )) {
                Text(l10n("remote.enabled", name))
            }
            .labelsHidden()
            .toggleStyle(.switch)
            .controlSize(.mini)
            .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
        .contextMenu {
            RemoteHostActions(model: model, host: host, canRetry: canRetry, onEdit: onEdit, onRemove: onRemove)
        }
    }
}

/// The status line and, while connected with a skewed clock, the clock note; while the host's history is too large
/// to sync, that note. A retry countdown ticks every second; hovering shows the last error, the exit status or the
/// helper's version.
private struct RemoteHostStatusLine: View {
    let model: AppModel
    let host: RemoteHost
    let status: RemoteHostStatus

    var body: some View {
        let l10n = model.l10n
        VStack(alignment: .leading, spacing: 2) {
            Group {
                if status.status.isRetrying, status.retryAt != nil {
                    TimelineView(.periodic(from: .now, by: 1)) { context in
                        line(now: context.date, l10n: l10n)
                    }
                } else {
                    line(now: Date(), l10n: l10n)
                }
            }
            .help(RemoteStatusText.tooltip(status, now: Date(), l10n: l10n) ?? "")
            if let note = RemoteStatusText.clockNote(status, l10n: l10n) {
                Label {
                    Text(note)
                } icon: {
                    Image(systemName: "clock.arrow.circlepath")
                }
                .labelStyle(RemoteNoteLabelStyle())
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            }
            if let note = RemoteStatusText.historyNote(status, l10n: l10n) {
                Label {
                    Text(note)
                } icon: {
                    Image(systemName: "exclamationmark.triangle.fill")
                        .foregroundStyle(Color(nsColor: .systemOrange))
                }
                .labelStyle(RemoteNoteLabelStyle())
                .font(.subheadline)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
                .transition(.opacity)
            }
        }
        .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: status.historyTooLarge)
    }

    private func line(now: Date, l10n: Localizer) -> some View {
        Text(RemoteStatusText.sentence(status, destination: host.destination, sessions: model.hostSessions(host.id),
                                       now: now, l10n: l10n))
            .font(.subheadline)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .textSelection(.enabled)
            .contentTransition(.opacity)
            .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: status.status)
    }
}

/// A small leading symbol at the text's first line.
private struct RemoteNoteLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 4) {
            configuration.icon.imageScale(.small).accessibilityHidden(true)
            configuration.title
        }
    }
}

/// The ellipsis button: the row's actions as a menu.
private struct RemoteHostActionsMenu: View {
    let model: AppModel
    let host: RemoteHost
    let canRetry: Bool
    let onEdit: () -> Void
    let onRemove: () -> Void

    var body: some View {
        let label = model.l10n("remote.actions")
        Menu {
            RemoteHostActions(model: model, host: host, canRetry: canRetry, onEdit: onEdit, onRemove: onRemove)
        } label: {
            Image(systemName: "ellipsis.circle")
                .font(.system(size: 14))
                .foregroundStyle(.secondary)
        }
        .menuStyle(.borderlessButton)
        .menuIndicator(.hidden)
        .fixedSize()
        .help(label)
        .accessibilityLabel(label)
        .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
    }
}

/// *Edit…*, *Retry now* (only while it is failing), *Copy SSH command* and *Remove…*.
private struct RemoteHostActions: View {
    let model: AppModel
    let host: RemoteHost
    let canRetry: Bool
    let onEdit: () -> Void
    let onRemove: () -> Void

    var body: some View {
        let l10n = model.l10n
        Button(l10n("remote.edit"), action: onEdit)
        if canRetry {
            Button(l10n("remote.retry")) { model.retryRemoteHost(host.id) }
        }
        Button(l10n("remote.copyCommand")) {
            let pasteboard = NSPasteboard.general
            pasteboard.clearContents()
            pasteboard.setString("ssh " + host.destination, forType: .string)
        }
        Divider()
        Button(l10n("remote.remove"), role: .destructive, action: onRemove)
    }
}
