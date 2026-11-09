import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// The live values a `character` orb floats beside her besides the speed (DESIGN 2a.6, Readouts): network speed,
/// 性能 and 思考, and the cost rate, each with the quiet rule of DESIGN 10 (loud while there is something to say, quiet
/// 10 s after the last loud moment). The renderer shows a card while it is loud and switched on.
struct OrbCharacterReadings: Equatable {
    /// The cards switched on (the orb's `characterReadouts`).
    var readouts: CharacterReadouts = .default
    /// Download over upload; nil while measuring is off or per-process traffic is unavailable.
    var network: OrbLineReading?
    /// The network unit cells are `Mb/s` wide.
    var bits = false
    /// 性能 and 思考 of the orb's agents.
    var performance: OrbLineReading?
    /// The live cost rate; nil before the first loud moment.
    var cost: LiveMoney?
    /// Loud at the last tick.
    var networkLoud = false
    var performanceLoud = false
    var costLoud = false
}

/// The quiet rule of the cost card: loud while money flows (or a generating set has no price, shown as `–`); its value
/// follows the live rate until the card has faded, then holds, so a quiet orb renders no frames.
struct OrbCostQuietRule {
    private(set) var money: LiveMoney?
    private(set) var quietAt = -TimeInterval.infinity

    mutating func apply(_ next: LiveMoney?, generating: Bool, at time: TimeInterval) {
        guard let next else {
            money = nil
            quietAt = -.infinity
            return
        }
        let loud = next.value > 0 || (next.unknown && generating)
        if loud { quietAt = time + OrbLineQuietRule.quietAfter }
        if loud || time < quietAt + OrbLineReading.fadeOut { money = next }
    }

    func isLoud(at time: TimeInterval) -> Bool { money != nil && time < quietAt }
}
