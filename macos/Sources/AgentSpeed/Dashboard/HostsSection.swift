import AgentSpeedCore
import SwiftUI

/// Dashboard → 主機 (DESIGN 13), between Sources and Agents while remote hosts exist: one row per host that is
/// connected or produced output today, This Mac first, with its live value and today's output and cost. A host that is
/// not connected shows its status word instead of the live value. A remote host adds, for each of its sources with a
/// quota, a sub-line with the source's dot and name and that source's quota lines under the name (*Host quotas*). Each
/// row opens the Usage History window filtered to its host.
struct HostsSection: View {
    let model: AppModel

    /// The section exists only with a remote host configured (and a snapshot that knows the hosts).
    static func isShown(_ model: AppModel) -> Bool {
        model.hasRemoteHosts && !model.snapshot.hosts.isEmpty
    }

    /// Every remote host's quota lines, keyed `<host>|<source>`, so the section shares its columns.
    static func quotaCells(_ model: AppModel, _ hosts: [HostStatus]) -> [String: [SourceQuotaLine]] {
        var cells: [String: [SourceQuotaLine]] = [:]
        for host in hosts where !host.isLocal {
            for entry in model.hostQuotaLines(host.id) { cells["\(host.id)|\(entry.source.rawValue)"] = entry.lines }
        }
        return cells
    }

    var body: some View {
        let l10n = model.l10n
        let hosts = model.snapshot.hosts.filter(\.isListed)
        let separator = " · "
        let (fitted, columns) = SourceQuotaColumns.fit(Self.quotaCells(model, hosts), separator: separator,
                                                       inset: SourceQuotaColumns.hostSourceInset)
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
                let quota = host.isLocal ? [] : model.hostQuotaLines(host.id).compactMap { entry -> HostQuotaGroup? in
                    guard let lines = fitted["\(host.id)|\(entry.source.rawValue)"], !lines.isEmpty else { return nil }
                    return HostQuotaGroup(source: entry.source, lines: lines)
                }
                HostRow(model: model, host: host, quota: quota, columns: columns, separator: separator)
                    .transition(.opacity)
            }
        }
        .animation(model.dashboardMotion, value: hosts.map(\.id))
        .animation(model.accessibility.reduceMotion ? nil : .easeInOut(duration: 0.25), value: fitted.mapValues(\.count))
    }
}

/// One source of a host with its quota lines.
struct HostQuotaGroup: Identifiable, Equatable {
    var source: SourceKind
    var lines: [SourceQuotaLine]
    var id: SourceKind { source }
}

private struct HostRow: View {
    let model: AppModel
    let host: HostStatus
    let quota: [HostQuotaGroup]
    let columns: SourceQuotaColumns
    let separator: String

    @Environment(\.previewHoveredHost) private var previewHovered
    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let l10n = model.l10n
        let name = host.isLocal ? l10n("remote.local") : (model.remoteHostName(host.id) ?? host.name)
        let status = remoteStatus
        let reading = OrbSubset.cost(model.snapshot, sources: nil, hosts: [host.id], billable: model.cost.billable)
        let todayCost = model.cost.tallyText(reading.today, requests: reading.todayRequests)
        let spokenCost = model.cost.spokenTally(reading.today, requests: reading.todayRequests)
        let spokenQuota = quota.isEmpty ? "" : ", " + quota.map { group in
            l10n(group.source.titleKey) + " " + group.lines.map(\.spoken).joined(separator: l10n("list.separator"))
        }.joined(separator: l10n("list.separator"))
        Button {
            model.commands?.showHistory(host: host.id)
        } label: {
            VStack(alignment: .leading, spacing: 0) {
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
                    .accessibilityLabel("\(l10n("dashboard.today")) \(l10n("today.output")) \(SpeedFormat.tokens(host.today.output)), \(l10n("today.cost")) \(spokenCost)\(spokenQuota)")
                liveSlot(status: status, reading: reading, l10n: l10n)
            }
            .frame(minHeight: DashboardLayout.rowHeight)
            if !quota.isEmpty {
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(quota) { group in
                        HostQuotaSourceLines(group: group, columns: columns, separator: separator, dark: colorScheme == .dark,
                                             name: l10n(group.source.titleKey), reduceMotion: model.accessibility.reduceMotion)
                            .transition(.opacity)
                    }
                }
                .padding(.leading, SourceQuotaColumns.nameInset)
                .padding(.top, 2)
                .padding(.bottom, 4)
                .accessibilityHidden(true)
                .transition(.opacity)
            }
            }
        }
        .buttonStyle(DashboardSourceRowButtonStyle(forcedHover: previewHovered == host.id, stacked: !quota.isEmpty))
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

/// A host's source sub-line: the source's dot and name (11 pt, secondary), then its quota lines under the name.
private struct HostQuotaSourceLines: View {
    let group: HostQuotaGroup
    let columns: SourceQuotaColumns
    let separator: String
    let dark: Bool
    let name: String
    let reduceMotion: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Circle()
                    .fill(group.source.tint(dark: dark))
                    .frame(width: 6, height: 6)
                Text(name)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundStyle(.dashboardSecondary)
                    .lineLimit(1)
            }
            SourceQuotaLines(source: group.source, lines: group.lines, columns: columns, separator: separator,
                             reduceMotion: reduceMotion, topPadding: 3)
                .padding(.leading, SourceQuotaColumns.hostSourceInset - SourceQuotaColumns.nameInset)
        }
    }
}

extension EnvironmentValues {
    /// The host row drawn as hovered in previews.
    @Entry var previewHoveredHost: String?
}
