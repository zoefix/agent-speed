import AgentSpeedCore
import SwiftUI

/// One target (DESIGN 14): status dot, name and address, the status line, the actions menu and the `enabled` switch.
/// The context menu offers the same actions.
struct LanTargetRow: View {
    let model: AppModel
    let target: LanDocument.Target
    let onEdit: () -> Void
    let onRemove: () -> Void

    var body: some View {
        let l10n = model.l10n
        let status = model.lan.status(of: target)
        HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
            RemoteStatusDot(kind: LanStatusText.dot(status.state), reduceMotion: model.accessibility.reduceMotion)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
            VStack(alignment: .leading, spacing: 3) {
                HStack(alignment: .firstTextBaseline, spacing: Spacing.xs) {
                    Text(target.displayName)
                        .font(.system(size: 13, weight: .semibold))
                        .foregroundStyle(target.enabled ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                        .lineLimit(1)
                        .layoutPriority(1)
                    Text(verbatim: target.name.isEmpty ? target.kind.rawValue.uppercased()
                                                       : "\(target.kind.rawValue.uppercased()) · \(target.address)")
                        .font(.system(size: 12).monospacedDigit())
                        .foregroundStyle(.secondary)
                        .lineLimit(1)
                        .truncationMode(.middle)
                }
                LanTargetStatusLine(model: model, status: status)
            }
            .accessibilityElement(children: .combine)
            Spacer(minLength: Spacing.s)
            Menu {
                actions(l10n)
            } label: {
                Image(systemName: "ellipsis.circle")
                    .font(.system(size: 13))
                    .foregroundStyle(.secondary)
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .fixedSize()
            .help(l10n("remote.actions"))
            .accessibilityLabel(l10n("remote.actions"))
            .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
            Toggle(isOn: Binding(get: { target.enabled }, set: { model.lan.setEnabled($0, target: target.id) })) {
                Text(l10n("lan.target.enabled", target.displayName))
            }
            .labelsHidden()
            .toggleStyle(.switch)
            .controlSize(.mini)
            .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4.5 }
        }
        .padding(.vertical, 2)
        .contentShape(Rectangle())
        .contextMenu { actions(l10n) }
    }

    @ViewBuilder
    private func actions(_ l10n: Localizer) -> some View {
        Button(l10n("lan.target.edit"), action: onEdit)
        Divider()
        Button(l10n("lan.target.remove"), role: .destructive, action: onRemove)
    }
}

/// The status line; the time since the last send and a retry countdown move every second.
struct LanTargetStatusLine: View {
    let model: AppModel
    let status: LanTargetStatus

    var body: some View {
        Group {
            if status.state == .ok || status.state == .unreachable {
                TimelineView(.periodic(from: .now, by: 1)) { context in
                    line(now: context.date)
                }
            } else {
                line(now: Date())
            }
        }
        .help(status.lastSuccess.map { $0.formatted(date: .omitted, time: .standard) } ?? "")
    }

    private func line(now: Date) -> some View {
        Text(LanStatusText.target(status, now: now, l10n: model.l10n))
            .font(.subheadline)
            .foregroundStyle(.secondary)
            .fixedSize(horizontal: false, vertical: true)
            .contentTransition(.opacity)
            .animation(model.accessibility.reduceMotion ? nil : Motion.fade, value: status.state)
    }
}
