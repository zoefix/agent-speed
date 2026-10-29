import AgentSpeedOrbModel
import AppKit

/// The sub-gauge's table rows of one tick as text (DESIGN 2c Live metrics): 思考 and 費用 as they show (思考 hidden at
/// zero leaves its row out). Quiet values — `–`, a `0` of 思考 or 費用, the tag — are drawn quietly. 性能 is the
/// gauges' own (`BoardHeroScore`, `BoardScoreNumber`).
@MainActor
struct BoardGaugeReadings {
    var thinking: BoardGaugeReading?
    var cost: BoardGaugeReading?
    /// 思考 is switched on, shown or hidden (its cells stay).
    var thinkingOn: Bool
    var costOn: Bool
    var intelligenceOn: Bool

    /// `uppercase`: timing's Latin labels and units.
    init(_ tick: BoardMetricsTick, l10n: Localizer, uppercase: Bool = false) {
        let readouts = tick.readouts
        let locale = l10n.locale
        func cased(_ text: String) -> String { uppercase ? text.uppercased(with: locale) : text }
        intelligenceOn = readouts.intelligence
        thinkingOn = readouts.thinking
        costOn = readouts.cost
        if tick.showsThinking {
            thinking = BoardGaugeReading(label: cased(l10n("intel.thinking.glyph")), number: tick.thinking,
                                         unit: cased(l10n("unit.tokS")),
                                         quiet: tick.quiet || OrbThinkingLine.readsZero(tick.thinking))
        }
        if readouts.cost {
            let money = tick.cost
            cost = BoardGaugeReading(label: cased(l10n("cost.glyph")), number: money.number, unit: cased(money.unit),
                                     atLeast: money.atLeast, quiet: money.isQuiet, tag: money.state == .tag)
        }
    }

    /// The table's labels and units for its cells: every switched-on row's, so hiding 思考 never moves the table.
    static func cells(_ tick: BoardMetricsTick, l10n: Localizer, uppercase: Bool = false) -> (labels: [String], units: [String]) {
        let locale = l10n.locale
        func cased(_ text: String) -> String { uppercase ? text.uppercased(with: locale) : text }
        var labels: [String] = []
        var units: [String] = []
        if tick.readouts.thinking {
            labels.append(cased(l10n("intel.thinking.glyph")))
            units.append(cased(l10n("unit.tokS")))
        }
        if tick.readouts.cost {
            labels.append(cased(l10n("cost.glyph")))
            units.append(cased(tick.costUnit))
        }
        return (labels, units)
    }
}

/// What a sub-gauge needs to lay out one tick.
@MainActor
struct BoardGaugeFrame {
    /// The band the sub-gauge centres in: its top and height (board points).
    var top: CGFloat
    var height: CGFloat
    /// The sub-gauge's right edge (the side inset).
    var right: CGFloat
    var memory: BoardTickMemory
    var now: TimeInterval
    /// Reduce Motion: no count, every 性能 change a 0.15 s crossfade, the arc and the meter jump.
    var reduceMotion = false
}

extension BoardLabel {
    /// The left edge that centres the glyphs on `center` when the text carries a tracking of `tracking` em after its
    /// last glyph too (CSS letter-spacing, balanced by a `padding-left` of the same).
    func centredX(_ center: CGFloat, tracking: CGFloat) -> CGFloat {
        center - width / 2 + tracking * font.pointSize / 2
    }
}
