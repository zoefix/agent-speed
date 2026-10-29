import AgentSpeedCore
import AgentSpeedOrbModel

/// Everything the score card shows (DESIGN 12, *Score card*): the header, the score with its usual chip, scale and
/// summary, the components, the work signals, the facts.
struct PerfCardModel: Equatable, Sendable {
    struct Header: Equatable, Sendable {
        var source: SourceKind
        var project: String
        /// The model of the newest timed request, else the agent's current model.
        var model: String?
        /// A group row's sub-agents (`boards.subCount`).
        var subAgents: Int
    }

    var header: Header
    var score: PerfCardScore
    /// The row's former tooltip lines, after `perf.card.sample`.
    var facts: [String]
}

/// The score block and components of a card.
struct PerfCardScore: Equatable, Sendable {
    var source: SourceKind
    var form: PerfValueForm
    var shown: Int?
    var provisional: Bool
    var resting: Bool { form == .resting }
    /// The sample size (有效計時請求).
    var n: Int
    var tools: Int
    /// N on the hero's card: the set's agents with a score in that form.
    var topCount: Int
    var baseline: Double?
    var vsUsualPercent: Int?
    var components: [PerfComponent]
    var signals: WorkSignals

    func component(_ id: PerfComponentId) -> PerfComponent? { components.first { $0.id == id } }
}

extension PerfCardModel {
    /// The card of `agent`: `facts` are its row's former tooltip lines; a group row passes its sub-agents, a board hero
    /// the set's N.
    init(agent: AgentStatus, facts: [String], subAgents: Int = 0, topCount: Int = 1) {
        let evaluation = agent.perf ?? .none(source: agent.source)
        let value = agent.intelligence.score
        let form: PerfValueForm = evaluation.score == nil ? .none : value.form == .none ? .live : value.form
        let model = evaluation.model.flatMap { $0.isEmpty ? nil : $0 } ?? agent.model
        self.init(
            header: Header(source: agent.source, project: agent.project, model: model, subAgents: subAgents),
            score: PerfCardScore(source: agent.source, form: form, shown: evaluation.shown,
                                 provisional: evaluation.provisional, n: evaluation.n, tools: evaluation.tools,
                                 topCount: topCount, baseline: evaluation.baseline,
                                 vsUsualPercent: evaluation.vsUsualPercent, components: evaluation.components,
                                 signals: agent.signals),
            facts: facts)
    }
}
