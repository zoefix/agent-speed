import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// The Hosts section under Sources in the orb and board editors (DESIGN 13), only while remote hosts exist: All Hosts,
/// then every host (This Mac first) with its status dot and, when it is not connected, its status word. With All Hosts
/// on the rows show checked and disabled; the last checked host cannot be unchecked. Turning All Hosts off keeps
/// today's hosts checked, so the reading does not jump.
struct HostPickerSection: View {
    let model: AppModel
    let selection: OrbHostSelection
    let set: (OrbHostSelection) -> Void
    let toggle: (String) -> Void

    var body: some View {
        let choices = model.hostChoices
        if OrbHostChoice.pickerShown(choices) {
            content(choices)
        }
    }

    private func content(_ choices: [OrbHostChoice]) -> some View {
        let l10n = model.l10n
        let all = selection.isAll
        return Section {
            Toggle(isOn: Binding(
                get: { all },
                set: { on in set(on ? .all : .explicit(choices.map(\.id))) }
            )) {
                VStack(alignment: .leading, spacing: 2) {
                    Text(l10n("remote.hosts.all"))
                    Footnote(l10n("remote.hosts.allHint"))
                }
            }
            .toggleStyle(.checkbox)

            ForEach(selection.entries(choices), id: \.id) { entry in
                Toggle(isOn: Binding(
                    get: { all || entry.checked },
                    set: { _ in toggle(entry.id) }
                )) {
                    HStack(spacing: Spacing.xs) {
                        RemoteStatusDot(kind: model.hostDotKind(entry.id), reduceMotion: model.accessibility.reduceMotion)
                        Text(entry.name)
                            .lineLimit(1)
                        Spacer(minLength: Spacing.s)
                        if let word = word(entry.id, l10n: l10n) {
                            Text(word)
                                .font(.system(size: 12))
                                .foregroundStyle(.secondary)
                                .lineLimit(1)
                        }
                    }
                }
                .toggleStyle(.checkbox)
                .disabled(all || !entry.enabled)
                .help(!all && !entry.enabled ? l10n("remote.hosts.lastOne") : "")
                .accessibilityValue(Text(word(entry.id, l10n: l10n) ?? ""))
            }
        } header: {
            Text(l10n("remote.hosts"))
        }
    }

    /// A remote host that is not connected says why in one word.
    private func word(_ id: String, l10n: Localizer) -> String? {
        guard let host = model.settings.remoteHosts.host(id) else { return nil }
        let status = model.remoteStatus(of: host).status
        return status == .connected ? nil : RemoteStatusText.word(status, l10n: l10n)
    }
}
