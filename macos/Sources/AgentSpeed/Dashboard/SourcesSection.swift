import AgentSpeedCore
import SwiftUI

/// One row per source that produced tokens today, with its live speed and today's output (DESIGN section 4.3), and
/// under it the source's quota lines (QUOTAS; a source with a recent quota is listed too). Each row opens the Usage
/// History window filtered to its source. The other sources are left to Settings → Data Sources.
struct SourcesSection: View {
    let model: AppModel

    var body: some View {
        let l10n = model.l10n
        let quotaListed = model.quota.listedSources
        let sources = SourceKind.allCases.compactMap { kind in
            model.snapshot.sources.first { $0.kind == kind && ($0.producedToday || quotaListed.contains(kind)) }
        }
        let separator = " · "
        let (quota, columns) = SourceQuotaColumns.fit(
            Dictionary(uniqueKeysWithValues: sources.map { ($0.kind, model.quotaLines($0.kind)) }), separator: separator)

        VStack(alignment: .leading, spacing: DashboardLayout.rowSpacing) {
            HStack(spacing: Spacing.s) {
                SectionTitle(text: l10n("dashboard.sources"), latin: model.dashboardLatinCaps)
                Spacer(minLength: 0)
                if !sources.isEmpty {
                    ColumnCaption(text: l10n("dashboard.today"), width: DashboardLayout.tokensColumn)
                    ColumnCaption(text: model.settings.liveUnit == .cost ? model.cost.unitPerSecond : l10n("unit.tokS"),
                                  width: DashboardLayout.speedColumn)
                }
            }
            .padding(.bottom, 2)
            if sources.isEmpty {
                Text(l10n("dashboard.sources.empty"))
                    .font(DashboardFont.row)
                    .foregroundStyle(.dashboardSecondary)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                    .frame(minHeight: DashboardLayout.rowHeight, alignment: .leading)
                    .transition(.opacity)
            }
            ForEach(sources) { source in
                SourceRow(model: model, source: source, quota: quota[source.kind] ?? [], columns: columns,
                          separator: separator,
                          invitesCli: source.kind == .claudeCode && source.producedToday && model.quota.invitesCli)
                    .transition(.opacity)
            }
        }
        .animation(model.dashboardMotion, value: sources.map(\.kind))
        .animation(model.accessibility.reduceMotion ? nil : .easeInOut(duration: 0.25),
                   value: quota.mapValues { $0.count })
    }
}

extension SourceStatus {
    /// The source's folder exists or it has data today.
    var isDetected: Bool { available || today.requests > 0 }
}

extension SourceRow {
    /// Today's cost of the source (DESIGN 9): `–` when nothing is priced, `≥` when part is; a subscription source
    /// shows `cost.subscription.tag` unless subscriptions are included.
    fileprivate var todayCost: String {
        if model.settings.isSubscription(source.kind), !model.settings.includeSubscriptions {
            return model.l10n("cost.subscription.tag")
        }
        return model.cost.tallyText(source.todayCost, requests: source.today.requests)
    }

    fileprivate var spokenCost: String {
        if model.settings.isSubscription(source.kind), !model.settings.includeSubscriptions {
            return model.l10n("cost.subscription.tag")
        }
        return model.cost.spokenTally(source.todayCost, requests: source.today.requests)
    }
}

/// The live money value of a source row: the rate number (`≥` when partly priced), `–` below the visible minimum or
/// when nothing is priced, in the same trailing slot as tok/s.
private struct LiveCostValue: View {
    let model: AppModel
    let source: SourceStatus

    var body: some View {
        var snapshot = SpeedSnapshot.empty
        snapshot.sources = [source]
        // A row always shows its own source's API-equivalent rate; marks only change what the totals count.
        let reading = OrbSubset.cost(snapshot, sources: SourceSet(source.kind), billable: SourceSet(source.kind))
        let money = model.cost.live(reading, agentCount: 0)
        let idle = source.live.output < LiveSpeedValue.visibleMinimum && reading.microUsdPerSecond <= 0
        let text = idle || money.unknown ? "–" : (money.atLeast ? "≥" : "") + money.number
        return Text(verbatim: text)
            .font(DashboardFont.rowNumber)
            .foregroundStyle(text == "–" ? AnyShapeStyle(.tertiary) : AnyShapeStyle(.primary))
            .lineLimit(1)
            .fixedSize()
            .frame(minWidth: DashboardLayout.speedColumn, alignment: .trailing)
            .accessibilityLabel(text == "–" ? model.l10n("cost.unknown") : model.cost.spoken(money.number))
    }
}

private struct SourceRow: View {
    /// The name's leading edge: the dot and the gap after it; quota lines start here.
    static let nameInset = SourceQuotaColumns.nameInset

    let model: AppModel
    let source: SourceStatus
    let quota: [SourceQuotaLine]
    let columns: SourceQuotaColumns
    let separator: String
    let invitesCli: Bool

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.previewHoveredSource) private var previewHovered

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            button
            if invitesCli {
                QuotaInvitationLine(model: model)
                    .padding(.leading, Self.nameInset)
                    .transition(.opacity)
            }
        }
        .animation(model.accessibility.reduceMotion ? nil : .easeInOut(duration: 0.25), value: invitesCli)
    }

    /// The quota windows read after the cost, joined like a list.
    private var spokenQuota: String {
        quota.map(\.spoken).joined(separator: model.l10n("list.separator"))
    }

    private var button: some View {
        let l10n = model.l10n
        let live = source.enabled
        let speed = model.value(of: source.live)
        let name = l10n(source.kind.titleKey)
        let spokenQuota = quota.isEmpty ? "" : ", " + self.spokenQuota

        return Button {
            model.commands?.showHistory(source: source.kind)
        } label: {
            VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: Spacing.s) {
                Circle()
                    .fill(source.kind.tint(dark: colorScheme == .dark))
                    .frame(width: 8, height: 8)
                    .opacity(live ? 1 : 0.35)
                    .accessibilityHidden(true)
                Text(name)
                    .font(DashboardFont.row)
                    .foregroundStyle(live ? AnyShapeStyle(.primary) : AnyShapeStyle(.dashboardSecondary))
                    .lineLimit(1)
                    .layoutPriority(1)
                Spacer(minLength: Spacing.xs)
                if live {
                    let today = SpeedFormat.tokens(source.today.output) + " · " + todayCost
                    Text(today)
                        .font(DashboardFont.detail)
                        .foregroundStyle(.dashboardSecondary)
                        .lineLimit(1)
                        .fixedSize()
                        .frame(minWidth: DashboardLayout.tokensColumn, alignment: .trailing)
                        .accessibilityLabel("\(l10n("dashboard.today")) \(l10n("today.output")) \(SpeedFormat.tokens(source.today.output)), \(l10n("today.cost")) \(spokenCost)\(spokenQuota)")
                    if model.settings.liveUnit == .cost {
                        LiveCostValue(model: model, source: source)
                    } else {
                        LiveSpeedValue(speed: speed, l10n: l10n, reduceMotion: model.accessibility.reduceMotion)
                    }
                } else {
                    Text(l10n("source.disabled"))
                        .font(DashboardFont.detail)
                        .foregroundStyle(.dashboardSecondary)
                        .lineLimit(1)
                        .accessibilityLabel(l10n("source.disabled") + spokenQuota)
                }
            }
            .frame(minHeight: DashboardLayout.rowHeight)
            if !quota.isEmpty {
                SourceQuotaLines(source: source.kind, lines: quota, columns: columns, separator: separator,
                                 reduceMotion: model.accessibility.reduceMotion)
                    .padding(.leading, Self.nameInset)
                    .padding(.bottom, 4)
                    .accessibilityHidden(true)
                    .transition(.opacity)
            }
            }
        }
        .buttonStyle(DashboardSourceRowButtonStyle(forcedHover: previewHovered == source.kind, stacked: !quota.isEmpty))
        .animation(Motion.fade, value: model.cost.currency.code)
        .focusEffectDisabled()
        .help(l10n("mac.dashboard.sourceHint"))
        .accessibilityElement(children: .combine)
        .accessibilityHint(l10n("mac.dashboard.sourceHint"))
    }
}
