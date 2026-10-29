import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation
import Observation

/// An event for one orb: a completed response of its sources, or its own new peak (METRICS 6.8).
enum OrbFeedEvent: Equatable {
    case response(output: Int, speed: Double?)
    case newPeak
}

/// What one orb shows (DESIGN 2b, Engine): its own gauge — heat, tiers and titles with its redline — and the readings
/// over its sources. The dashboard hero uses the all-sources feed; every floating orb has its own.
@MainActor
@Observable
final class OrbFeed {
    let gauge: LiveGauge
    /// Sources of the generating agents (agent dots), in agents order.
    var generating: [SourceKind] = []
    /// Today's tokens of the selected metric and the number of agents (hover info line).
    var todayTokens = 0
    var agentCount = 0
    /// The colour rim of an orb tracking a subset; nil for all sources.
    var bezel: OrbBezel?
    /// Set while every source of the orb is unavailable: `source.disabled` or `source.notFound`.
    var unavailableTitleKey: String?
    /// The orb's output redline, for the heat of response pulses.
    var redlineOutput = 400.0
    /// tok/s or the money unit (DESIGN 9); heat, tiers and titles always follow tokens.
    var unit: OrbUnit = .tokens
    /// The money readout while `unit` is `.cost`.
    var money: LiveMoney?
    /// The two-line readout while it is on (DESIGN 10): network speed, or 性能 and 思考 of the orb's agents.
    var lines: OrbLines?

    @ObservationIgnored private var listeners: [UUID: @MainActor (OrbFeedEvent) -> Void] = [:]

    init(gauge: LiveGauge = LiveGauge()) {
        self.gauge = gauge
    }

    @discardableResult
    func addListener(_ handler: @escaping @MainActor (OrbFeedEvent) -> Void) -> UUID {
        let token = UUID()
        listeners[token] = handler
        return token
    }

    func removeListener(_ token: UUID) {
        listeners[token] = nil
    }

    func emit(_ event: OrbFeedEvent) {
        for handler in listeners.values { handler(event) }
    }

    func set(generating: [SourceKind], todayTokens: Int, agentCount: Int) {
        if self.generating != generating { self.generating = generating }
        if self.todayTokens != todayTokens { self.todayTokens = todayTokens }
        if self.agentCount != agentCount { self.agentCount = agentCount }
    }
}
