import AgentSpeedCore
import SwiftUI

/// The busiest agents in parent groups, already ordered by the engine (METRICS 6.7; DESIGN section 4.4). Collapsed it
/// shows the first rows and a disclosure for the rest; expanded it shows everyone, scrolling past a dozen rows.
struct AgentsSection: View {
    let model: AppModel
    /// Width of the agent rows: the panel minus its padding.
    var rowWidth = DashboardController.width - 2 * DashboardLayout.padding

    @State private var order = StableAgentOrder()
    /// True while a collapse animates: the extra rows stay until the closing region has slid over them.
    @State private var isCollapsing = false
    @Environment(\.colorScheme) private var colorScheme
    @Environment(\.colorSchemeContrast) private var contrast

    var body: some View {
        let l10n = model.l10n
        let snapshot = model.snapshot
        let expanded = model.settings.agentsExpanded
        let limit = DashboardLayout.maxAgents
        let rows = order.arrange(snapshot.agents, limit: expanded || isCollapsing ? nil : limit) { model.value(of: $0.live) }
        let total = snapshot.agents.count
        let visible = min(rows.count, expanded ? DashboardLayout.maxExpandedAgents : limit)
        let hidden = expanded ? 0 : total - visible
        let tree = AgentTree(rows)
        let depths = tree.depths.map { min($0, DashboardLayout.maxAgentDepth) }
        let intelligenceCaption = l10n("intel.glyph")
        let intelligenceWidth = AgentIntelligenceColumn.width(caption: intelligenceCaption)
        let column = AgentCapsuleColumn(
            rows: rows.indices.map { (EffortCapsule(rows[$0].current, l10n), depths[$0]) },
            rowWidth: rowWidth,
            intelligence: intelligenceWidth
        )

        VStack(alignment: .leading, spacing: DashboardLayout.rowSpacing) {
            HStack(spacing: Spacing.s) {
                SectionTitle(text: l10n("dashboard.agents"), latin: model.dashboardLatinCaps)
                Spacer(minLength: 0)
                if !rows.isEmpty {
                    HStack(spacing: AgentIntelligenceColumn.gap) {
                        ColumnCaption(text: intelligenceCaption, width: intelligenceWidth)
                            .help(l10n("agents.intel.hint"))
                        ColumnCaption(text: l10n("unit.tokS"), width: DashboardLayout.speedColumn)
                    }
                }
            }
            .padding(.bottom, 2)

            if rows.isEmpty {
                AgentsEmptyState(text: l10n("agents.empty"))
                    .transition(.opacity)
            } else {
                AgentsListRegion(
                    height: DashboardLayout.agentListHeight(rows: visible),
                    scrolls: expanded && rows.count > DashboardLayout.maxExpandedAgents,
                    motion: model.dashboardResizeMotion
                ) {
                    VStack(alignment: .leading, spacing: DashboardLayout.rowSpacing) {
                        ForEach(Array(rows.enumerated()), id: \.element.id) { index, agent in
                            // Leaving rows go at once so they never ghost under the row taking their place.
                            AgentRow(
                                model: model,
                                agent: agent,
                                depth: depths[index],
                                now: snapshot.timestamp,
                                capsuleColumn: column,
                                intelligenceWidth: intelligenceWidth
                            )
                            .transition(.asymmetric(insertion: fadeIn, removal: .identity))
                            .zIndex(Double(rows.count - index))
                        }
                    }
                    .overlayPreferenceValue(BadgeAnchors.self) { anchors in
                        GeometryReader { proxy in
                            SubagentConnectors(tree: tree, anchors: anchors, proxy: proxy, color: DashboardPalette.rule(
                                dark: colorScheme == .dark,
                                increasedContrast: contrast == .increased
                            ))
                        }
                        .allowsHitTesting(false)
                        .accessibilityHidden(true)
                    }
                }
                if hidden > 0 || (expanded && total > limit) {
                    AgentsDisclosure(
                        expanded: expanded,
                        hidden: hidden,
                        total: total,
                        l10n: l10n,
                        reduceMotion: model.accessibility.reduceMotion,
                        action: toggle
                    )
                    .transition(fadeIn)
                }
            }
        }
        .animation(model.dashboardResizeMotion, value: rows.map(\.id))
        #if DEBUG
        .onReceive(NotificationCenter.default.publisher(for: MotionHarness.toggleAgents)) { _ in toggle() }
        #endif
    }

    /// Under Reduce Motion nothing moves, so appearing rows carry their own fade.
    private var fadeIn: AnyTransition {
        model.accessibility.reduceMotion ? .opacity.animation(Motion.fade) : .opacity
    }

    private func toggle() {
        let settings = model.settings
        let motion = model.dashboardResizeMotion
        guard settings.agentsExpanded else {
            isCollapsing = false
            withAnimation(motion) { settings.agentsExpanded = true }
            return
        }
        isCollapsing = true
        withAnimation(motion) {
            settings.agentsExpanded = false
        } completion: {
            if !settings.agentsExpanded { isCollapsing = false }
        }
    }
}

/// Badge frames of the visible rows, keyed by agent id, for drawing sub-agent connectors.
struct BadgeAnchors: PreferenceKey {
    static var defaultValue: [String: Anchor<CGRect>] { [:] }

    static func reduce(value: inout [String: Anchor<CGRect>], nextValue: () -> [String: Anchor<CGRect>]) {
        value.merge(nextValue()) { $1 }
    }
}

/// Thin elbow lines from each parent row's badge down to the badges of its own sub-agents.
private struct SubagentConnectors: View {
    let tree: AgentTree
    let anchors: [String: Anchor<CGRect>]
    let proxy: GeometryProxy
    let color: Color

    var body: some View {
        Path { path in
            for (index, parentIndex) in tree.parents.enumerated() {
                guard let parentIndex, let parent = badge(parentIndex), let child = badge(index) else { continue }
                let x = parent.midX
                let radius: CGFloat = 3
                let endX = child.minX + 1
                guard endX > x + radius else {
                    // Past the deepest indent a group's rows stack, so link to the row right above.
                    if let above = badge(index - 1) {
                        path.move(to: CGPoint(x: child.midX, y: above.maxY + 2))
                        path.addLine(to: CGPoint(x: child.midX, y: child.minY - 2))
                    }
                    continue
                }
                path.move(to: CGPoint(x: x, y: parent.maxY + 2))
                path.addLine(to: CGPoint(x: x, y: child.midY - radius))
                path.addQuadCurve(to: CGPoint(x: x + radius, y: child.midY), control: CGPoint(x: x, y: child.midY))
                path.addLine(to: CGPoint(x: endX, y: child.midY))
            }
        }
        .stroke(color, style: StrokeStyle(lineWidth: 1, lineCap: .round, lineJoin: .round))
    }

    private func badge(_ index: Int) -> CGRect? {
        anchors[tree.rows[index].id].map { proxy[$0] }
    }
}

private struct AgentsEmptyState: View {
    let text: String

    var body: some View {
        HStack(spacing: Spacing.s) {
            RestingGauge(lineWidth: 1.6)
                .frame(width: DashboardLayout.badgeWidth + 2, height: DashboardLayout.badgeWidth + 2)
            Text(text)
                .font(DashboardFont.row)
                .foregroundStyle(.dashboardSecondary)
                .lineLimit(2)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(.vertical, Spacing.xxs)
        .accessibilityElement(children: .combine)
    }
}
