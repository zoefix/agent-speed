import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// The `character` clip's cards on the simulated clock (DESIGN 2a.6, Readouts): network speed, 性能 and 思考 and the
/// cost rate follow the tour's speed through the app's own quiet rules, ticking like the engine every 500 ms.
/// `AGENT_SPEED_ORB_READOUTS` switches cards: a comma list of `CharacterReadout` names, each optionally prefixed with
/// `-` to switch it off (`model` turns the model chip on). `AGENT_SPEED_ORB_LATENCY` gives the 首字 row a value (ms) and
/// `AGENT_SPEED_ORB_MODELS` the models card its rows: a comma list of `id=tok/s` (`id=-` before a timed response), the
/// vendor resolved from the price table.
@MainActor
struct CharacterDemoReadings {
    private var network = OrbLineQuietRule()
    private var performance = OrbLineQuietRule()
    private var cost = OrbCostQuietRule()
    private var nextTick = -Double.infinity
    let readouts: CharacterReadouts
    let latencyMs: Double?
    let models: [OrbModelReading]

    init(environment: [String: String], prices: PriceTable? = nil) {
        latencyMs = environment["AGENT_SPEED_ORB_LATENCY"].flatMap(Double.init)
        models = (environment["AGENT_SPEED_ORB_MODELS"] ?? "").split(separator: ",").compactMap { item in
            let parts = item.split(separator: "=", maxSplits: 1).map(String.init)
            guard let id = parts.first, !id.isEmpty else { return nil }
            let speed = parts.count > 1 ? Double(parts[1]).map(perfRoundHalfAway) : nil
            return OrbModelReading(model: id, speed: speed,
                                   vendor: CharacterVendors.resolve(model: id, source: .opencode, prices: prices).vendor)
        }
        var readouts = CharacterReadouts.default
        for item in (environment["AGENT_SPEED_ORB_READOUTS"] ?? "").split(separator: ",") {
            let off = item.hasPrefix("-")
            guard let readout = CharacterReadout(rawValue: String(off ? item.dropFirst() : item)) else { continue }
            readouts[readout] = !off
        }
        self.readouts = readouts
    }

    /// The readings at `time` with the tour at `value` tok/s.
    mutating func update(value: Double, time: TimeInterval, display: CostDisplay) -> OrbCharacterReadings? {
        guard time >= nextTick else { return nil }
        nextTick = time + 0.5
        let wobble = 1 + 0.35 * sin(time * 1.7) + 0.15 * sin(time * 4.3)
        let rx = value * 52 * wobble, tx = 0.22 * rx + 120 * max(0, sin(time * 0.9))
        network.apply(TrafficRate(rx: rx, tx: tx), bits: false, at: time)
        let generating = value > 1
        let score = generating
            ? PerfSetReading(form: .live, shown: Int(760 + 3.2 * value + 40 * sin(time / 5)), provisional: value < 20, count: 1,
                             agentId: nil)
            : PerfSetReading.none
        performance.apply(score: score, thinking: generating ? value * (0.28 + 0.2 * sin(time / 3)) : 0,
                          generating: generating, at: time)
        let reading = CostReading(microUsdPerSecond: value * OrbRuntime.previewMicroUsdPerToken, liveOutput: value)
        cost.apply(display.live(reading, agentCount: 3), generating: generating, at: time)
        return OrbCharacterReadings(
            readouts: readouts, network: network.reading, bits: false, performance: performance.reading, cost: cost.money,
            latencyMs: latencyMs, models: models,
            networkLoud: network.reading.map { time < $0.quietAt } ?? false,
            performanceLoud: performance.reading.map { time < $0.quietAt } ?? false,
            costLoud: cost.isLoud(at: time),
            modelsLoud: !models.isEmpty
        )
    }
}
