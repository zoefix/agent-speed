import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// The live values a `character` orb floats beside her besides the speed (DESIGN 2a.6, Readouts): network speed,
/// 性能, 思考 and 首字, the cost rate and the running models, each card with the quiet rule of DESIGN 10 (loud while
/// there is something to say, quiet 10 s after the last loud moment). The renderer shows a card while it is loud and
/// switched on.
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
    /// 首字, the orb's first-content latency in ms (METRICS 6.9 *Readouts of a set*); nil while it has none.
    var latencyMs: Double?
    /// The running models, fastest first; the last ones are kept while the card is quiet.
    var models: [OrbModelReading] = []
    /// 額度: the quotas of the orb's sources whose app runs and is signed in, in source order (QUOTAS 6).
    var quota: [QuotaCardItem] = []
    /// Loud at the last tick.
    var networkLoud = false
    var performanceLoud = false
    var costLoud = false
    var modelsLoud = false
}

/// One running model of the models card: its id, its shown speed (tok/s, nil before a timed response) and the vendor
/// whose accent marks it (CHARACTERS 7).
struct OrbModelReading: Equatable {
    var model: String
    var speed: Int?
    var vendor: String?
}

/// The quiet rule of the models card: loud while a model runs (an agent of the orb is not idle); when the last one
/// stops, the card keeps its rows until it has faded (DESIGN 2a.6, *Which cards show*).
struct OrbModelsQuietRule {
    private(set) var rows: [OrbModelReading] = []
    private(set) var quietAt = -TimeInterval.infinity

    mutating func apply(_ next: [OrbModelReading], at time: TimeInterval) {
        if !next.isEmpty {
            rows = next
            quietAt = time + OrbLineQuietRule.quietAfter
        } else if time >= quietAt + OrbLineReading.fadeOut {
            rows = []
        }
    }

    func isLoud(at time: TimeInterval) -> Bool { !rows.isEmpty && time < quietAt }
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
