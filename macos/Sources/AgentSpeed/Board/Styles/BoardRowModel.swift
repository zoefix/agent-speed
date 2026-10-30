import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// What one planned row shows (DESIGN 2c Content 2), independent of the style that draws it. Equal models draw equal
/// images, so a row's model is its revision.
struct BoardRowModel: Hashable {
    enum Kind: Hashable {
        /// A group: its rank (1 = first place, which gets the leader treatment).
        case group(rank: Int)
        case sub
        /// `boards.moreSubs` / `boards.moreGroups`.
        case more
    }

    var kind: Kind
    var name: String
    var detail = ""
    /// The value column: METRICS 7 text, `–` for an idle row, empty for none.
    var value = ""
    var idle = false
    var phase: AgentPhase = .idle
    var sources: [SourceKind] = []
    var capsule: EffortCapsule?
    /// Share of `live_A` in 1/200 steps (glass and neon underline).
    var share = 0
    /// The row's own heat in 1/100 steps (value color and underline).
    var heat = 0
    /// Timing's interval column: `boards.leader` on the first group, else the gap to the group above.
    var interval = ""
    /// The 性能 cell (DESIGN 2c Live metrics, *Ranking rows*); nil without the column (switched off, more rows).
    var intel: BoardRowScore?
    /// A group row's second line (M and L): the detail moves under the name.
    var twoLine = false

    var rank: Int? {
        if case .group(let rank) = kind { return rank }
        return nil
    }

    var isLeader: Bool { rank == 1 }
    var source: SourceKind? { sources.first }
    var shareFraction: Double { Double(share) / 200 }
    var heatFraction: Double { Double(heat) / 100 }
}

/// A row's 性能 (DESIGN 2c *Ranking rows*; DESIGN 12 *Values and forms*): a group row's highest member score, a sub
/// row's own, in compact form; live in the style's live row color, resting and none (`–`) in its resting color, the
/// provisional mark under a provisional score (a group's follows the member that supplies it).
struct BoardRowScore: Hashable {
    var shown: Int?
    var live: Bool
    var provisional: Bool

    static let none = BoardRowScore(shown: nil, live: false, provisional: false)

    init(shown: Int?, live: Bool, provisional: Bool) {
        self.shown = shown
        self.live = shown != nil && live
        self.provisional = shown != nil && provisional
    }

    init(_ reading: PerfSetReading) {
        self.init(shown: reading.shown, live: reading.form == .live, provisional: reading.isProvisional)
    }

    var text: String { shown.map(PerfFormat.compact) ?? "–" }
}

@MainActor
enum BoardRowModels {
    /// The models of `tick.rows`, in order. `twoLine`: group rows draw their second line (the board's size allows it).
    static func make(_ tick: BoardTick, l10n: Localizer, twoLine: Bool = false) -> [BoardRowModel] {
        let groups = tick.groups
        func heat(_ live: Double) -> Int { Int((HeatPalette.heat(value: live, redline: tick.redline) * 100).rounded()) }
        func share(_ live: Double) -> Int { Int((BoardContentRules.share(live, of: tick.value) * 200).rounded()) }
        func value(_ live: Double) -> (String, Bool) {
            live > BoardGrouping.idle ? (SpeedFormat.speed(live), false) : ("–", true)
        }
        func name(_ agent: AgentStatus) -> String {
            agent.project.isEmpty ? l10n(agent.source.titleKey) : agent.project
        }
        let intelligence = tick.metrics.readouts.intelligence
        func intel(_ reading: PerfSetReading, into model: inout BoardRowModel) {
            guard intelligence else { return }
            model.intel = BoardRowScore(reading)
        }

        return tick.rows.map { row in
            switch row.kind {
            case .group(let index):
                let group = groups[index]
                let root = group.root
                let detail = [root.model.flatMap { $0.isEmpty ? nil : $0 },
                              group.subs.isEmpty ? nil : l10n("boards.subCount", group.subs.count)]
                    .compactMap { $0 }.joined(separator: " · ")
                let (text, idle) = value(group.live)
                let interval = index == 0 ? l10n("boards.leader")
                    : BoardContentRules.interval(ahead: groups[index - 1].live, live: group.live)
                var model = BoardRowModel(kind: .group(rank: index + 1), name: name(root), detail: detail, value: text, idle: idle,
                                          phase: group.generating > 0 ? .generating : root.phase, sources: [root.source],
                                          capsule: EffortCapsule(root.current, l10n), share: share(group.live), heat: heat(group.live), interval: interval)
                intel(group.intelligenceReading, into: &model)
                model.twoLine = twoLine
                return model
            case .sub(let index, let subIndex):
                let root = groups[index].root
                let agent = groups[index].subs[subIndex]
                let live = agent.live.value(for: tick.metric)
                let (text, idle) = value(live)
                // A sub-agent's model is shown only when it differs from its root's.
                let model = agent.model.flatMap { $0.isEmpty || $0 == root.model ? nil : $0 } ?? ""
                var row = BoardRowModel(kind: .sub, name: name(agent), detail: model, value: text, idle: idle, phase: agent.phase,
                                        sources: [agent.source], capsule: EffortCapsule(agent.current, l10n), share: share(live), heat: heat(live))
                intel(PerfSetReading(agent: agent), into: &row)
                return row
            case .moreSubs(let index, let count, let live):
                return BoardRowModel(kind: .more, name: l10n("boards.moreSubs", count, SpeedFormat.speed(live)),
                                     sources: [groups[index].root.source])
            case .moreGroups(let count, let live):
                let hidden = groups.suffix(count).prefix(3).map(\.root.source)
                return BoardRowModel(kind: .more, name: l10n("boards.moreGroups", count, SpeedFormat.speed(live)),
                                     sources: Array(hidden))
            }
        }
    }

    /// The live value behind each row of `tick.rows` (more rows: their sum).
    static func lives(_ tick: BoardTick) -> [Double] {
        tick.rows.map { row in
            switch row.kind {
            case .group(let index): tick.groups[index].live
            case .sub(let index, let sub): tick.groups[index].subs[sub].live.value(for: tick.metric)
            case .moreSubs(_, _, let live), .moreGroups(_, let live): live
            }
        }
    }

    /// The shared capsule column: as wide as the widest capsule among the rows, 0 when none shows.
    static func capsuleColumn(_ rows: [BoardRowModel], compact: Bool, capsules: BoardCapsuleDrawing) -> CGFloat {
        rows.compactMap { row in row.capsule.flatMap { capsules.capsuleSize(for: $0, compact: compact)?.width } }.max() ?? 0
    }
}
