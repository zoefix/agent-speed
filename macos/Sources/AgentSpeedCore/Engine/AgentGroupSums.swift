import Foundation

/// A board group's live sums (METRICS 6.9 *Sets and groups*, *Live thinking*): its members' thinking sums added (never
/// an average of their values) and the highest of their performance scores. Boards show no cost per group (METRICS 9.5).
public struct AgentGroupSums: Equatable, Sendable {
    /// Σ of the members' thinking sums; `score` is the highest member value.
    public var intelligence = IntelligenceSums.zero

    public init() {}

    public static let zero = AgentGroupSums()

    /// Adds one member.
    public mutating func add(_ agent: AgentStatus) {
        intelligence.add(agent.intelligence)
    }

    /// The group's 性能: the highest live score of its members, else the highest resting one, else none.
    public var intelligenceForm: AgentIntelligence { AgentIntelligence(intelligence.score) }
}
