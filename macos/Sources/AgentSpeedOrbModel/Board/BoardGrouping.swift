import AgentSpeedCore
import Foundation

/// A root agent and all its descendants (DESIGN 2c, Ranking).
public struct BoardGroup: Equatable, Sendable, Identifiable {
    /// The root agent's id.
    public var id: String { root.id }
    public var root: AgentStatus
    /// Every descendant, by live value, latest activity, id.
    public var subs: [AgentStatus]
    /// The sum of the members' live values in the board's metric.
    public var live: Double
    /// The descendants' part of `live`.
    public var subLive: Double
    /// The latest activity of any member.
    public var lastActive: Date
    /// Members in phase `generating`.
    public var generating: Int
    /// The members' live intelligence sums (METRICS 6.9 Group intelligence).
    public var sums = AgentGroupSums.zero

    /// The highest performance score of its members (METRICS 6.9 Sets and groups).
    public var intelligence: AgentIntelligence { sums.intelligenceForm }

    /// The root first, then its descendants.
    public var members: [AgentStatus] { [root] + subs }

    /// The group's 性能 as its row draws it: the highest member score with the supplier's provisional mark and N.
    public var intelligenceReading: PerfSetReading { PerfSetReading(score: sums.intelligence.score, agents: members) }

    /// The member whose score the row shows, else the root (DESIGN 12: a group row's card is the supplier's, a group
    /// without a score shows its root's).
    public var cardAgent: AgentStatus {
        guard let id = sums.intelligence.score.agentId, let member = members.first(where: { $0.id == id }) else { return root }
        return member
    }
}

/// Groups a board's agents by their root (DESIGN 2c, Content 2; METRICS 6.7 parent links).
public enum BoardGrouping {
    /// Below this live value (tok/s) a group or agent counts as idle (DESIGN 2c, Ranking stability).
    public static let idle = 0.05

    /// `agents` is the board's agents list (METRICS 6.7 filtered by 6.8, so a parent outside the set is already
    /// cleared). A sub-agent whose parent is not listed is its own root; cycles resolve to the agent itself. Groups
    /// come in the order of their roots in `agents`.
    public static func groups(_ agents: [AgentStatus], metric: SpeedMetric) -> [BoardGroup] {
        var index: [String: Int] = [:]
        for (position, agent) in agents.enumerated() where index[agent.id] == nil { index[agent.id] = position }
        func root(_ start: Int) -> Int {
            var current = start
            var visited: Set<Int> = [start]
            while let parent = agents[current].parentId, let next = index[parent], next != current {
                guard visited.insert(next).inserted else { return start }
                current = next
            }
            return current
        }
        var order: [Int] = []
        var members: [Int: [Int]] = [:]
        for position in agents.indices where index[agents[position].id] == position {
            let top = root(position)
            if top == position {
                order.append(position)
            } else {
                members[top, default: []].append(position)
            }
        }
        return order.map { top in
            let rootAgent = agents[top]
            let subs = (members[top] ?? []).map { agents[$0] }.sorted { lhs, rhs in
                let a = lhs.live.value(for: metric), b = rhs.live.value(for: metric)
                if a != b { return a > b }
                if lhs.lastActivity != rhs.lastActivity { return lhs.lastActivity > rhs.lastActivity }
                return lhs.id < rhs.id
            }
            var subLive = 0.0
            var lastActive = rootAgent.lastActivity
            var generating = rootAgent.phase == .generating ? 1 : 0
            var sums = AgentGroupSums()
            sums.add(rootAgent)
            for sub in subs {
                subLive += max(0, sub.live.value(for: metric))
                lastActive = max(lastActive, sub.lastActivity)
                if sub.phase == .generating { generating += 1 }
                sums.add(sub)
            }
            return BoardGroup(
                root: rootAgent,
                subs: subs,
                live: max(0, rootAgent.live.value(for: metric)) + subLive,
                subLive: subLive,
                lastActive: lastActive,
                generating: generating,
                sums: sums
            )
        }
    }

    /// What the stable ranking needs of each group.
    public static func rankEntries(_ groups: [BoardGroup]) -> [BoardRankEntry] {
        groups.map { BoardRankEntry(id: $0.id, live: $0.live, lastActiveMs: $0.lastActive.timeIntervalSince1970 * 1000) }
    }
}
