import AgentSpeedCore
import SwiftUI

/// Dashboard → 主機 (DESIGN 13), between Sources and Agents while remote hosts exist: one row per host that is
/// connected or produced output today, This Mac first, with its live value and today's output and cost. A host that is
/// not connected shows its status word instead of the live value. Each row opens the Usage History window filtered to
/// its host.
struct HostsSection: View {
    let model: AppModel

    /// The section exists only with a remote host configured (and a snapshot that knows the hosts).
    static func isShown(_ model: AppModel) -> Bool {
        model.hasRemoteHosts && !model.snapshot.hosts.isEmpty
    }

    var body: some View {
        let l10n = model.l10n
        let hosts = model.snapshot.hosts.filter(\.isListed)
        VStack(alignment: .leading, spacing: DashboardLayout.rowSpacing) {
            HStack(spacing: Spacing.s) {
                SectionTitle(text: l10n("remote.hosts"), latin: model.dashboardLatinCaps)
                Spacer(minLength: 0)
                ColumnCaption(text: l10n("dashboard.today"), width: DashboardLayout.tokensColumn)
                ColumnCaption(text: model.settings.liveUnit == .cost ? model.cost.unitPerSecond : l10n("unit.tokS"),
                              width: DashboardLayout.speedColumn)
            }
            .padding(.bottom, 2)
            ForEach(hosts) { host in
                HostRow(model: model, host: host)
                    .transition(.opacity)
            }
        }
        .animation(model.dashboardMotion, value: hosts.map(\.id))
    }
}

private struct HostRow: View {
    let model: AppModel
    let host: HostStatus

    @Environment(\.previewHoveredHost) private var previewHovered

    var body: some View {
        let l10n = model.l10n
        let name = host.isLocal ? l10n("remote.local") : (model.remoteHostName(host.id) ?? host.name)
        let status = remoteStatus
        let reading = OrbSubset.cost(model.snapshot, sources: nil, hosts: [host.id], billable: model.cost.billable)
        let todayCost = model.cost.tallyText(reading.today, requests: reading.todayRequests)
        let spokenCost = model.cost.spokenTally(reading.today, requests: reading.todayRequests)
        Button {
            model.commands?.showHistory(host: host.id)
        } label: {
            HStack(spacing: Spacing.s) {
                RemoteStatusDot(kind: status.map { RemoteDotKind($0.status) } ?? .connected,
                                reduceMotion: model.accessibility.reduceMotion)
                Text(name)
                    .font(DashboardFont.row)
                    .foregroundStyle(host.isConnected ? AnyShapeStyle(.primary) : AnyShapeStyle(.dashboardSecondary))
                    .lineLimit(1)
                    .truncationMode(.middle)
                Spacer(minLength: Spacing.xs)
                Text(SpeedFormat.tokens(host.today.output) + " · " + todayCost)
                    .font(DashboardFont.detail)
                    .foregroundStyle(.dashboardSecondary)
                    .lineLimit(1)
                    .fixedSize()
                    .frame(minWidth: DashboardLayout.tokensColumn, alignment: .trailing)
                    .accessibilityLabel("\(l10n("dashboard.today")) \(l10n("today.output")) \(SpeedFormat.tokens(host.today.output)), \(l10n("today.cost")) \(spokenCost)")
                liveSlot(status: status, reading: reading, l10n: l10n)
            }
            .frame(minHeight: DashboardLayout.rowHeight)
        }
        .buttonStyle(DashboardSourceRowButtonStyle(forcedHover: previewHovered == host.id))
        .animation(Motion.fade, value: host.isConnected)
        .focusEffectDisabled()
        .help(l10n("remote.dashboard.hint"))
        .accessibilityElement(children: .combine)
        .accessibilityHint(l10n("remote.dashboard.hint"))
    }

    /// The settings' view of a remote host's status; nil for this Mac.
    private var remoteStatus: RemoteHostStatus? {
        guard !host.isLocal else { return nil }
        if let configured = model.settings.remoteHosts.host(host.id) { return model.remoteStatus(of: configured) }
        return host.remote
    }

    @ViewBuilder
    private func liveSlot(status: RemoteHostStatus?, reading: CostReading, l10n: Localizer) -> some View {
        if let status, status.status != .connected {
            Text(RemoteStatusText.word(status.status, l10n: l10n))
                .font(DashboardFont.detail)
                .foregroundStyle(.dashboardSecondary)
                .lineLimit(1)
                .fixedSize()
                .frame(minWidth: DashboardLayout.speedColumn, alignment: .trailing)
                .transition(.opacity)
        } else if model.settings.liveUnit == .cost {
            let money = model.cost.live(reading, agentCount: 0)
            let idle = host.live.output < LiveSpeedValue.visibleMinimum && reading.microUsdPerSecond <= 0
            let text = idle || money.unknown ? "–" : (money.atLeast ? "≥" : "") + money.number
            Text(verbatim: text)
                .font(DashboardFont.rowNumber)
                .foregroundStyle(text == "–" ? AnyShapeStyle(.tertiary) : AnyShapeStyle(.primary))
                .lineLimit(1)
                .fixedSize()
                .frame(minWidth: DashboardLayout.speedColumn, alignment: .trailing)
                .accessibilityLabel(text == "–" ? l10n("cost.unknown") : model.cost.spoken(money.number))
        } else {
            LiveSpeedValue(speed: model.value(of: host.live), l10n: l10n, reduceMotion: model.accessibility.reduceMotion)
        }
    }
}

extension EnvironmentValues {
    /// The host row drawn as hovered in previews.
    @Entry var previewHoveredHost: String?
}
