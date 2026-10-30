import AgentSpeedCore
import AgentSpeedOrbModel
import SwiftUI

/// A group row's hover tooltip while the board's `intelligence` is off (DESIGN 2c Interaction; with it on, the row's
/// score card takes its place, `BoardScoreCards`): project and source, model, the effort and speed lines of section 4,
/// and the group speed with its sub-agents' share. It names no cost. The engine keeps no per-agent total for today, so
/// the prototype's "today's output" line has no data to show.
enum BoardTooltip {
    static let delay: TimeInterval = 0.35

    @MainActor
    static func lines(_ group: BoardGroup, metric: SpeedMetric, l10n: Localizer) -> [String] {
        let root = group.root
        let source = l10n(root.source.titleKey)
        var lines = [(root.project.isEmpty ? source : root.project) + " · " + source]
        if let model = root.model, !model.isEmpty { lines.append(model) }
        let capsule = EffortCapsule(root.current)
        if capsule.effort != nil || capsule.speed != nil { lines += capsule.tooltipLines(l10n) }
        let speed = SpeedFormat.speed(group.live)
        lines.append(group.subs.isEmpty ? l10n("mac.boards.tooltip.speed", speed)
            : l10n("mac.boards.tooltip.speedSubs", speed, SpeedFormat.speed(group.subLive)))
        return lines
    }
}

/// The tooltip's look: the source chip's material (DESIGN 2b) as a small card, 11 pt text, the first line semibold.
struct BoardTooltipView: View {
    let lines: [String]

    @Environment(\.colorScheme) private var colorScheme

    var body: some View {
        let dark = colorScheme == .dark
        VStack(alignment: .leading, spacing: 2) {
            ForEach(Array(lines.enumerated()), id: \.offset) { index, line in
                Text(line)
                    .font(.system(size: 11, weight: index == 0 ? .semibold : .regular))
                    .foregroundStyle(index == 0 ? AnyShapeStyle(.primary) : AnyShapeStyle(.secondary))
                    .monospacedDigit()
            }
        }
        .lineLimit(1)
        .fixedSize()
        .padding(.horizontal, 9)
        .padding(.vertical, 7)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(dark ? Color(.sRGB, red: 20 / 255, green: 22 / 255, blue: 30 / 255, opacity: 0.92)
                    : Color(.sRGB, red: 1, green: 1, blue: 1, opacity: 0.96))
        )
        .overlay(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .strokeBorder(dark ? Color.white.opacity(0.14) : Color.black.opacity(0.1), lineWidth: 0.5)
        )
        .shadow(color: .black.opacity(dark ? 0.35 : 0.14), radius: 5, y: 2)
        .padding(8)
        .accessibilityHidden(true)
    }
}
