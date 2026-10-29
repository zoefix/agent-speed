import AgentSpeedCore
import Foundation

/// The menu bar block's sample moments (DESIGN 12, Values and forms; DESIGN 10, Menu bar and taskbar): the agents of
/// all enabled sources with their performance scores, modelled on the prototype's menu bar section, and their thinking
/// sums.
@MainActor
enum IntelligenceBarPreviewData {
    struct Scenario {
        var name: String
        var agents: [AgentStatus]
        /// Output tok/s in the window (`Σ w`): 0 is nothing generated.
        var output: Double
        var thinking: Double
        /// How long the values have shown; a zero thinking rate goes after its 3 s hold.
        var held: TimeInterval = 0

        /// The set's sums: the thinking rate and the highest score over `agents` (METRICS 6.9, Sets and groups).
        var sums: IntelligenceSums {
            var sums = IntelligenceSums()
            sums.output = output
            sums.thinking = thinking
            sums.thinkingBase = output
            sums.score = agents.reduce(PerfSetValue.empty) { $0.merged(with: $1.intelligence.score) }
            return sums
        }
    }

    static let hold: TimeInterval = 3

    /// Every form of 性能 and the provisional mark, with 思考 shown, read as 0 within its hold, and hidden after it; then
    /// the widest numbers. Indices 3, 4, 5 and 11 carry the provisional mark.
    static let scenarios: [Scenario] = [
        Scenario(name: "none · quiet", agents: [agent("a", "atlas", .codex, .live, nil)], output: 0, thinking: 0),
        Scenario(name: "none · fewer than 3 timed", agents: [agent("a", "atlas", .codex, .live, nil)], output: 180,
                 thinking: 128),
        Scenario(name: "live 1,286 · top of 4", agents: crew, output: 532, thinking: 128),
        Scenario(name: "live 1,048 · provisional · top of 2", agents: [
            agent("e", "ember", .claudeCode, .live, 1048.2, provisional: true), agent("f", "fjord", .codex, .live, 702),
        ], output: 180, thinking: 64),
        Scenario(name: "live 642 · provisional", agents: [agent("g", "gale", .codex, .live, 642.4, provisional: true)],
                 output: 96, thinking: 4.2),
        Scenario(name: "live 8,888 · provisional", agents: [agent("h", "halo", .claudeCode, .live, 8888, provisional: true)],
                 output: 2_600, thinking: 1_280),
        Scenario(name: "live 1,286 · thinking 0", agents: [agent("n", "nova", .codex, .live, 1286.4)], output: 40,
                 thinking: 0),
        Scenario(name: "live 1,286 · thinking 0 · 3 s", agents: [agent("n", "nova", .codex, .live, 1286.4)], output: 40,
                 thinking: 0, held: hold),
        Scenario(name: "live 1,048 · provisional · 3 s",
                 agents: [agent("e", "ember", .claudeCode, .live, 1048.2, provisional: true)],
                 output: 40, thinking: 0, held: hold),
        Scenario(name: "resting 1,452 · top of 2", agents: resting, output: 0, thinking: 0),
        Scenario(name: "resting 1,452 · top of 2 · 3 s", agents: resting, output: 0, thinking: 0, held: hold),
        Scenario(name: "resting 893 · provisional · 3 s",
                 agents: [agent("p", "pier", .codex, .resting, 893, provisional: true)],
                 output: 0, thinking: 0, held: hold),
        Scenario(name: "live 12.8k · 12.8k", agents: [agent("h", "halo", .claudeCode, .live, 12_840)],
                 output: 26_000, thinking: 12_800),
        Scenario(name: "live 1.28M · 1.20M", agents: [agent("h", "halo", .claudeCode, .live, 1_284_000)],
                 output: 2_400_000, thinking: 1_200_000),
    ]

    /// The prototype's four live agents: nova supplies 1,286.
    private static var crew: [AgentStatus] {
        [
            agent("n", "nova", .codex, .live, 1286.4), agent("o", "orbit", .claudeCode, .live, 1123),
            agent("q", "quill", .claudeCode, .live, 942.7), agent("e", "ember", .codex, .live, 702.1, provisional: true),
        ]
    }

    private static var resting: [AgentStatus] {
        [agent("n", "nova", .codex, .resting, 1452.3), agent("o", "orbit", .claudeCode, .resting, 893)]
    }

    /// One agent with `score` in `form` (nil: fewer than three timed requests).
    static func agent(_ id: String, _ project: String, _ source: SourceKind, _ form: PerfValueForm, _ score: Double?,
                      provisional: Bool = false) -> AgentStatus {
        let path = "/preview/bar/\(id).jsonl"
        var intelligence = IntelligenceSums()
        intelligence.score = PerfSetValue(form: form, score: score, agentId: path, lastLineMs: nil, provisional: provisional)
        let live = form == .live ? 120.0 : 0
        return AgentStatus(
            id: path, source: source, project: project, model: source == .codex ? "gpt-5.5" : "claude-opus-5-5",
            phase: form == .live ? .generating : .idle, isSubagent: false, live: Rate(output: live, total: live * 26),
            recentSpeed: live > 0 ? live : nil, lastActivity: Date(), intelligence: intelligence
        )
    }
}
