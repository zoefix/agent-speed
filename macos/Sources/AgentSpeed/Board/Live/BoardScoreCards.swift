import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// The score cards of a board (DESIGN 2c *Tooltip and score card*; DESIGN 12 *Score card*) while `intelligence` is on:
/// a group row's is the card of the member whose score it shows (a group without one: its root's), a sub row's its
/// sub-agent's, the hero's the supplying agent's with `perf.card.top` from two scored agents. Their facts are the row's
/// former tooltip lines that the card's header does not already carry (project, source and model): the agent's
/// recorded effort and speed, then the row's speed. No card names a cost.
@MainActor
enum BoardScoreCards {
    static func group(_ group: BoardGroup, l10n: Localizer) -> PerfCardModel {
        let agent = group.cardAgent
        let speed = SpeedFormat.speed(group.live)
        let line = group.subs.isEmpty ? l10n("mac.boards.tooltip.speed", speed)
            : l10n("mac.boards.tooltip.speedSubs", speed, SpeedFormat.speed(group.subLive))
        return PerfCardModel(agent: agent, facts: modeLines(agent, l10n) + [line], subAgents: group.subs.count)
    }

    static func sub(_ agent: AgentStatus, metric: SpeedMetric, l10n: Localizer) -> PerfCardModel {
        PerfCardModel(agent: agent, facts: agentFacts(agent, metric: metric, l10n: l10n))
    }

    /// Nil while the board's 性能 is none (nothing opens on the hero then).
    static func hero(_ tick: BoardTick, l10n: Localizer) -> PerfCardModel? {
        let reading = tick.metrics.intelligence
        guard reading.form != .none, let id = reading.agentId,
              let agent = tick.groups.lazy.flatMap(\.members).first(where: { $0.id == id }) else { return nil }
        return PerfCardModel(agent: agent, facts: agentFacts(agent, metric: tick.metric, l10n: l10n), topCount: reading.count)
    }

    /// The card of a row by its key (a group's or a sub-agent's id); nil when no such row is shown.
    static func row(_ key: String, in tick: BoardTick, l10n: Localizer) -> PerfCardModel? {
        for planned in tick.rows where planned.key == key {
            switch planned.kind {
            case .group(let index) where index < tick.groups.count:
                return group(tick.groups[index], l10n: l10n)
            case .sub(let index, let sub) where index < tick.groups.count && sub < tick.groups[index].subs.count:
                return self.sub(tick.groups[index].subs[sub], metric: tick.metric, l10n: l10n)
            default:
                return nil
            }
        }
        return nil
    }

    private static func agentFacts(_ agent: AgentStatus, metric: SpeedMetric, l10n: Localizer) -> [String] {
        modeLines(agent, l10n) + [l10n("mac.boards.tooltip.speed", SpeedFormat.speed(max(0, agent.live.value(for: metric))))]
    }

    /// The effort and speed lines of section 4 for what is recorded (as the prototype's card), then a top rung's
    /// explanation.
    private static func modeLines(_ agent: AgentStatus, _ l10n: Localizer) -> [String] {
        let capsule = EffortCapsule(agent.current)
        let lines = [capsule.effortLine(l10n), capsule.speedLine(l10n)].compactMap { $0 }
        guard !lines.isEmpty else { return [] }
        return lines + capsule.tooltipLines(l10n).dropFirst(2)
    }
}
