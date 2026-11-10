import AgentSpeedCore
import AgentSpeedOrbModel
import Foundation

/// The `character` clip's cards on the simulated clock (DESIGN 2a.6, Readouts): network speed, 性能 and 思考 and the
/// cost rate follow the tour's speed through the app's own quiet rules, ticking like the engine every 500 ms.
/// `AGENT_SPEED_ORB_READOUTS` switches cards: a comma list of `CharacterReadout` names, each optionally prefixed with
/// `-` to switch it off (`model` turns the model chip on). `AGENT_SPEED_ORB_LATENCY` gives the 首字 row a value (ms) and
/// `AGENT_SPEED_ORB_MODELS` the models card its rows: a comma list of `id=tok/s` (`id=-` before a timed response), the
/// vendor resolved from the price table. `AGENT_SPEED_ORB_QUOTA` gives the 額度 card its sources: `;`-separated
/// `source=label:percent:reset,…` with the reset as `+42m`, `+3h`, `+5d` from now, `-` unknown or `passed`, e.g.
/// `claudeDesktop=7d:94:+10h,Fable 7d:27:+10h;codex=7d:25:+5d`; `source@host=…` is that source on a remote host, and
/// a label may name a group (`Gemini 5h`), a month (`mo`) or credits.
@MainActor
struct CharacterDemoReadings {
    private var network = OrbLineQuietRule()
    private var performance = OrbLineQuietRule()
    private var cost = OrbCostQuietRule()
    private var nextTick = -Double.infinity
    let readouts: CharacterReadouts
    let latencyMs: Double?
    let models: [OrbModelReading]
    let quota: [QuotaCardItem]

    init(environment: [String: String], prices: PriceTable? = nil) {
        latencyMs = environment["AGENT_SPEED_ORB_LATENCY"].flatMap(Double.init)
        models = (environment["AGENT_SPEED_ORB_MODELS"] ?? "").split(separator: ",").compactMap { item in
            let parts = item.split(separator: "=", maxSplits: 1).map(String.init)
            guard let id = parts.first, !id.isEmpty else { return nil }
            let speed = parts.count > 1 ? Double(parts[1]).map(perfRoundHalfAway) : nil
            return OrbModelReading(model: id, speed: speed,
                                   vendor: CharacterVendors.resolve(model: id, source: .opencode, prices: prices).vendor)
        }
        quota = Self.quota(environment["AGENT_SPEED_ORB_QUOTA"] ?? "", nowMs: QuotaController.nowMs())
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
            latencyMs: latencyMs, models: models, quota: quota,
            networkLoud: network.reading.map { time < $0.quietAt } ?? false,
            performanceLoud: performance.reading.map { time < $0.quietAt } ?? false,
            costLoud: cost.isLoud(at: time),
            modelsLoud: !models.isEmpty
        )
    }

    /// The sources of `AGENT_SPEED_ORB_QUOTA`.
    static func quota(_ text: String, nowMs: Int64) -> [QuotaCardItem] {
        let order = SourceKind.allCases
        let items = text.split(separator: ";").compactMap { item -> QuotaCardItem? in
            let parts = item.split(separator: "=", maxSplits: 1).map(String.init)
            guard parts.count == 2 else { return nil }
            let key = parts[0].split(separator: "@", maxSplits: 1).map(String.init)
            guard let source = SourceKind(rawValue: key[0]) else { return nil }
            let windows = parts[1].split(separator: ",").compactMap { entry -> QuotaWindow? in
                let fields = entry.split(separator: ":").map(String.init)
                guard fields.count == 3, let used = Double(fields[1]) else { return nil }
                let (kind, name, minutes) = parseWindow(fields[0])
                return QuotaWindow(kind: kind, name: name, minutes: minutes, usedPercent: used,
                                   resetsAtMs: reset(fields[2], nowMs: nowMs))
            }
            let snapshot = QuotaSnapshot(source: source, observedMs: nowMs - 60_000, windows: windows)
            return QuotaCardItem(snapshot: snapshot, hostId: key.count > 1 ? key[1] : nil, hostName: key.count > 1 ? key[1] : nil)
        }
        // This Mac's sources first, then each host's, each in source order.
        return items.sorted { lhs, rhs in
            let (a, b) = (lhs.hostName ?? "", rhs.hostName ?? "")
            return a != b ? a < b : order.firstIndex(of: lhs.source)! < order.firstIndex(of: rhs.source)!
        }
    }

    /// `5h`, `7d`, `mo`, `credits` or a length (`1d`, `2h`, `45m`), after an optional group name.
    private static func parseWindow(_ label: String) -> (QuotaWindowKind, String?, Int?) {
        let words = label.split(separator: " ").map(String.init)
        let base = words.last ?? label
        let name = words.count > 1 ? words.dropLast().joined(separator: " ") : nil
        switch base {
        case "5h": return (.fiveHour, name, nil)
        case "7d": return (.sevenDay, name, nil)
        case "mo", "月": return (.month, name, nil)
        case "credits", "點數": return (.credits, name, nil)
        default:
            guard let unit = base.last, let n = Int(base.dropLast()) else { return (.other, name, 60) }
            return (.other, name, unit == "d" ? n * 1440 : unit == "h" ? n * 60 : n)
        }
    }

    private static func reset(_ text: String, nowMs: Int64) -> Int64? {
        if text == "passed" { return nowMs - 1000 }
        guard text.hasPrefix("+"), let unit = text.last, let n = Double(text.dropFirst().dropLast()) else { return nil }
        let ms = unit == "d" ? 86_400_000.0 : unit == "h" ? 3_600_000.0 : 60_000.0
        return nowMs + Int64(n * ms) + 30_000
    }
}
