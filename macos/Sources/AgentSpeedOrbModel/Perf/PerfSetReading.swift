import AgentSpeedCore

/// A set's 性能 as the surfaces draw it (METRICS 6.9 *Sets and groups*, DESIGN 12): the highest score with what a
/// `PerfSetValue` leaves out — N, the set's agents with a score in that form (accessibility and the card's eyebrow).
/// The provisional mark follows the supplying agent.
public struct PerfSetReading: Equatable, Sendable {
    public var form: PerfValueForm
    public var shown: Int?
    public var provisional: Bool
    public var count: Int
    public var agentId: String?

    public static let none = PerfSetReading(form: .none, shown: nil, provisional: false, count: 0, agentId: nil)

    public init(form: PerfValueForm, shown: Int?, provisional: Bool, count: Int, agentId: String?) {
        self.form = form
        self.shown = form == .none ? nil : shown
        self.provisional = form == .none ? false : provisional
        self.count = count
        self.agentId = agentId
    }

    /// `score` is the set's value over `agents` (the menu bar's agents list, an orb's or a board's set `A`, a group).
    public init(score: PerfSetValue, agents: [AgentStatus]) {
        guard score.form != .none, let shown = score.shown else {
            self = .none
            return
        }
        let count = agents.reduce(0) { $0 + ($1.intelligence.score.form == score.form ? 1 : 0) }
        self.init(form: score.form, shown: shown, provisional: score.provisional, count: max(1, count),
                  agentId: score.agentId)
    }

    /// One agent's own value (a dashboard row, a board sub row).
    public init(agent: AgentStatus) {
        self.init(score: agent.intelligence.score, agents: [agent])
    }

    /// Drawn with the provisional mark.
    public var isProvisional: Bool { form != .none && provisional }

    public var isResting: Bool { form == .resting }
}
