import AgentSpeedCore
import AgentSpeedOrbModel
import AppKit
import SwiftUI

/// One agent: phase badge, looping mark, project, source and model, effort capsule, 性能, live speed. Its tooltip
/// is the score card (DESIGN 12), which also reaches VoiceOver as the row's extra content and custom action.
struct AgentRow: View {
    let model: AppModel
    let agent: AgentStatus
    /// Visual nesting level in the parent tree; roots are 0.
    let depth: Int
    /// Snapshot time, the reference for "x minutes ago".
    let now: Date
    /// The shared effort capsule column of the visible rows.
    var capsuleColumn = AgentCapsuleColumn.none
    /// The intelligence cell's width, the same for every row.
    let intelligenceWidth: CGFloat

    /// Below this a truncated detail would show only a clipped sliver of its first glyph, so it steps aside.
    static let minimumDetail: CGFloat = 20

    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.dashboardScoreCards) private var cards
    @State private var detailFits = true
    @State private var anchor = ScreenAnchorBox()
    @FocusState private var focused: Bool

    var body: some View {
        let l10n = model.l10n
        let sourceName = l10n(agent.source.titleKey)
        let project = agent.project.isEmpty ? sourceName : agent.project
        // Model first: when space runs out the source name truncates, and the badge color still carries it.
        let detail = [agent.model, sourceName].compactMap { $0?.isEmpty == false ? $0 : nil }.joined(separator: " · ")
        // Attributed traffic today (DESIGN 10, Agents): the line ends with `· ↓ 2.10 MB`, kept whole while the model and
        // source names give way; both directions in the tooltip.
        let traffic = agentTraffic
        let received = traffic.flatMap { $0.rx > 0 ? "↓ " + TrafficFormat.total($0.rx) : nil }
        let trafficSuffix = received.map { " · " + $0 }
        let speed = model.value(of: agent.live)
        let status = status(l10n)
        let capsule = EffortCapsule(agent.modes)
        let modeLines = capsule.effort != nil || capsule.speed != nil ? capsule.tooltipLines(l10n) : []
        let live = LiveSpeedValue(
            speed: speed,
            phase: agent.phase,
            l10n: l10n,
            reduceMotion: model.accessibility.reduceMotion
        )
        let projectLabel = agent.isSubagent ? "\(project), \(l10n("agents.subagent"))" : project
        let looping = AgentSignalText.looping(agent.signals, l10n)
        let reduceMotion = model.accessibility.reduceMotion
        let score = PerfSetReading(agent: agent)

        HStack(spacing: Spacing.s) {
            PhaseBadge(
                phase: agent.phase,
                tint: agent.source.tint(dark: colorScheme == .dark),
                animated: !model.accessibility.reduceMotion
            )
            .anchorPreference(key: BadgeAnchors.self, value: .bounds) { [agent.id: $0] }

            HStack(alignment: .firstTextBaseline, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: LoopingMark.gap) {
                    if looping != nil { LoopingMark().transition(.opacity) }
                    Text(project)
                        .font(DashboardFont.rowTitle)
                        .lineLimit(1)
                }
                .layoutPriority(1)
                .animation(LoopingMark.animation(appearing: looping != nil, reduceMotion: reduceMotion), value: looping != nil)
                HStack(spacing: 0) {
                    let readable = min(Self.minimumDetail, Self.detailWidth(detail) - 1)
                    Text(detail)
                        .lineLimit(1)
                        .truncationMode(.tail)
                        .opacity(detailFits ? 1 : 0)
                        .onGeometryChange(for: Bool.self) { $0.size.width >= readable } action: { detailFits = $0 }
                    if let trafficSuffix {
                        Text(trafficSuffix)
                            .lineLimit(1)
                            .fixedSize()
                    }
                }
                .font(DashboardFont.detail)
                .foregroundStyle(.dashboardSecondary)
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: AgentCapsuleColumn.gap) {
                if capsuleColumn.width > 0 {
                    EffortCapsuleView(
                        capsule: capsule,
                        compact: capsuleColumn.compact,
                        width: capsuleColumn.width,
                        reduceMotion: model.accessibility.reduceMotion,
                        shimmers: model.effectsLevel != .off
                    )
                    .animation(.easeInOut(duration: 0.2), value: capsuleColumn)
                }
                HStack(alignment: .firstTextBaseline, spacing: AgentIntelligenceColumn.gap) {
                    AgentIntelligenceCell(reading: score, width: intelligenceWidth)
                        .environment(\.perfReduceMotion, reduceMotion)
                    live
                }
            }
        }
        .frame(height: DashboardLayout.rowHeight)
        .padding(.leading, CGFloat(depth) * DashboardLayout.subagentIndent)
        .contentShape(Rectangle())
        .background(ScreenAnchor(box: anchor))
        .accessibilityElement(children: .ignore)
        // 性能 follows the live value; the label ends with the capsule's effort and speed lines (DESIGN 4).
        .accessibilityLabel(
            [projectLabel, looping, detail, received, live.accessibilityText,
             AgentSignalText.intelligenceAccessibility(score, l10n),
             modeLines.isEmpty ? nil : capsule.accessibilityText(l10n)]
                .compactMap { $0?.isEmpty == false ? $0 : nil }
                .joined(separator: ", ")
        )
        .accessibilityValue(status)
        // With keyboard navigation on, a row takes focus with the system ring; Space shows its card (DESIGN 12, *Keyboard*).
        .focusable(cards != nil && NSApp.isFullKeyboardAccessEnabled)
        .focused($focused)
        .contentShape(.focusEffect, RoundedRectangle(cornerRadius: 6, style: .continuous))
        // After the row became one element, so its extra content and custom action belong to it.
        .modifier(ScoreCardTooltip(cards: cards, agent: agent, model: model, anchor: anchor, focused: focused,
                                   fallback: [looping, "\(project) · \(status)"].compactMap { $0 }
                                       + Self.facts(of: agent, model: model) + AgentSignalText.closing(agent.signals, l10n)))
    }

    /// The detail's untruncated width in the row's detail font.
    private static func detailWidth(_ detail: String) -> CGFloat {
        (detail as NSString).size(withAttributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: 11, weight: .regular)]).width
    }

    /// Today's traffic attributed to this agent's tree (roots only; sub-agents run inside their parent's process).
    private var agentTraffic: TrafficBytes? { Self.traffic(of: agent, model: model) }

    static func traffic(of agent: AgentStatus, model: AppModel) -> TrafficBytes? {
        guard model.settings.trafficEnabled, agent.parentId == nil,
              let traffic = model.traffic.snapshot.agents[agent.id] else { return nil }
        let bytes = traffic.today.total(includeTools: model.settings.trafficIncludeTools)
        return bytes.isZero ? nil : bytes
    }

    /// The score card's facts (DESIGN 12): the row's former tooltip lines — effort and speed, a top rung's detail,
    /// today's traffic.
    static func facts(of agent: AgentStatus, model: AppModel) -> [String] {
        let l10n = model.l10n
        let capsule = EffortCapsule(agent.modes)
        var lines = capsule.effort != nil || capsule.speed != nil ? capsule.tooltipLines(l10n) : []
        if let traffic = traffic(of: agent, model: model) {
            lines.append(l10n("traffic.agent.tooltip", TrafficText(model: model).pair(traffic)))
        }
        return lines
    }

    private func status(_ l10n: Localizer) -> String {
        switch agent.phase {
        case .generating: return l10n("phase.generating")
        case .tools: return l10n("phase.tools")
        case .idle:
            let seconds = Int(now.timeIntervalSince(agent.lastActivity).rounded(.down))
            let ago: String
            if seconds < 10 {
                ago = l10n("time.justNow")
            } else if seconds < 60 {
                ago = l10n("time.secondsAgo", seconds)
            } else {
                ago = l10n("time.minutesAgo", seconds / 60)
            }
            return "\(l10n("phase.idle")) · \(ago)"
        }
    }
}

/// The row's tooltip: its score card while the dashboard hosts one, else (previews) the same facts as text.
private struct ScoreCardTooltip: ViewModifier {
    let cards: DashboardScoreCards?
    let agent: AgentStatus
    let model: AppModel
    let anchor: ScreenAnchorBox
    let focused: Bool
    let fallback: [String]

    func body(content: Content) -> some View {
        if let cards {
            let id = agent.id
            let card = PerfCardModel(agent: agent, facts: AgentRow.facts(of: agent, model: model))
            content.perfCard(cards.presenter, key: id, lines: PerfCardText.accessibilityLines(card, model.l10n),
                             focused: focused, l10n: model.l10n) { [weak cards, anchor] in
                cards?.request(agentId: id, row: anchor.view)
            }
        } else {
            content.help(fallback.joined(separator: "\n"))
        }
    }
}
