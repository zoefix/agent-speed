import Foundation
import Testing
@testable import AgentSpeedCore

/// Readers for `spec/fixtures/perf-score-cases.json` and comparisons in its tolerance: numbers within 1e-9, relative
/// above 1 (METRICS 6.9).
enum PerfFixture {
    static var cases: [String: Any] { (try? Fixtures.json("perf-score-cases.json")) ?? [:] }

    static func group(_ name: String) -> [[String: Any]] { cases[name] as? [[String: Any]] ?? [] }

    static func names(_ group: String) -> [String] { self.group(group).compactMap { $0.string("name") } }

    static func spec(_ group: String, _ name: String) throws -> [String: Any] {
        try #require(self.group(group).first { $0.string("name") == name })
    }

    static func source(_ value: [String: Any]) throws -> SourceKind {
        try #require(SourceKind(rawValue: value.string("source") ?? ""))
    }

    /// A response in the engine form of 3.3.
    static func response(_ value: [String: Any]) throws -> ResponseRecord {
        ResponseRecord(
            key: try #require(value.string("key")), source: try source(value),
            agentId: value.string("agentId") ?? "", model: value.string("model"), startMs: value.int64("startMs"),
            endMs: try #require(value.int64("endMs")),
            usage: TokenUsage(input: value.int("input"), cachedInput: value.int("cachedInput"), output: value.int("output"),
                              reasoning: value.int("reasoning")),
            isFinal: value["final"] as? Bool ?? true, firstMs: value.int64("firstMs"))
    }

    /// A stored action of 6.10.
    static func action(_ value: [String: Any]) throws -> ToolAction {
        let digest = try #require(ToolDigest(hex: value.string("digest") ?? ""))
        return ToolAction(call: ToolCall(tool: value.string("tool") ?? "", digest: digest),
                          callMs: try #require(value.int64("callMs")), resultMs: value.int64("resultMs"),
                          failed: value["failed"] as? Bool, model: value.string("model"))
    }

    /// A 6.11 record, with its day, source and model when it names them.
    static func record(_ value: [String: Any]) throws -> PerfDayRecord {
        var record = PerfRecord()
        record.n = value.int("n")
        record.outputSum = value.int64("outputSum") ?? 0
        record.durationMsSum = value.int64("durationMsSum") ?? 0
        record.latencyBins = try #require(value["latencyBins"] as? [Int])
        record.reasonN = value.int("reasonN")
        record.reasonOutput = value.int64("reasonOutput") ?? 0
        record.reasonDurationMsSum = value.int64("reasonDurationMsSum") ?? 0
        record.cacheN = value.int("cacheN")
        record.cacheInput = value.int64("cacheInput") ?? 0
        record.cacheCached = value.int64("cacheCached") ?? 0
        record.toolN = value.int("toolN")
        record.toolLnSum = value.double("toolLnSum") ?? 0
        record.toolFailed = value.int("toolFailed")
        record.activeMs = value.int64("activeMs") ?? 0
        let key = PerfRecordKey(day: value.string("day") ?? "", source: (try? source(value)) ?? .codex,
                                model: value.string("model") ?? "")
        return PerfDayRecord(key: key, record: record)
    }

    // MARK: - As JSON objects (the fixture's shape)

    static func components(_ components: [PerfComponent]) -> [[String: Any]] {
        components.map { component in
            ["id": component.id.rawValue, "present": component.isPresent, "reason": component.reason?.rawValue ?? NSNull(),
             "samples": component.samples, "measure": component.measure ?? NSNull(), "ratio": component.ratio ?? NSNull(),
             "subScore": component.subScore ?? NSNull()]
        }
    }

    static func record(_ record: PerfRecord) -> [String: Any] {
        ["n": record.n, "outputSum": record.outputSum, "durationMsSum": record.durationMsSum,
         "latencyBins": record.latencyBins, "reasonN": record.reasonN, "reasonOutput": record.reasonOutput,
         "reasonDurationMsSum": record.reasonDurationMsSum, "cacheN": record.cacheN, "cacheInput": record.cacheInput,
         "cacheCached": record.cacheCached, "toolN": record.toolN, "toolLnSum": record.toolLnSum,
         "toolFailed": record.toolFailed, "activeMs": record.activeMs]
    }

    static func pooled(_ pooled: PerfPooled) -> [String: Any] {
        ["n": pooled.n, "tools": pooled.tools, "activeMs": pooled.activeMs, "score": pooled.score ?? NSNull(),
         "shown": pooled.shown ?? NSNull(), "components": components(pooled.components)]
    }

    // MARK: - Comparison

    /// Structural equality; numbers within 1e-9, relative above 1, as `perf_score.close`.
    static func close(_ lhs: Any?, _ rhs: Any?) -> Bool {
        let a = lhs is NSNull ? nil : lhs
        let b = rhs is NSNull ? nil : rhs
        switch (a, b) {
        case (nil, nil): return true
        case (nil, _), (_, nil): return false
        case let (x as String, y as String): return x == y
        case let (x as [String: Any], y as [String: Any]):
            return Set(x.keys) == Set(y.keys) && x.keys.allSatisfy { close(x[$0], y[$0]) }
        case let (x as [Any], y as [Any]):
            return x.count == y.count && zip(x, y).allSatisfy { close($0, $1) }
        case let (x as NSNumber, y as NSNumber):
            if CFGetTypeID(x) == CFBooleanGetTypeID() || CFGetTypeID(y) == CFBooleanGetTypeID() {
                return CFGetTypeID(x) == CFGetTypeID(y) && x.boolValue == y.boolValue
            }
            let p = x.doubleValue
            let q = y.doubleValue
            return abs(p - q) <= 1e-9 * max(1, abs(p), abs(q))
        case let (x as Bool, y as Bool): return x == y
        case let (x as Int, y as NSNumber): return close(NSNumber(value: x), y)
        case let (x as Int64, y as NSNumber): return close(NSNumber(value: x), y)
        case let (x as Double, y as NSNumber): return close(NSNumber(value: x), y)
        default: return false
        }
    }

    /// The paths where `actual` and `expected` differ, for a readable failure.
    static func differences(_ actual: Any?, _ expected: Any?, _ label: String = "") -> [String] {
        if close(actual, expected) { return [] }
        if let x = actual as? [String: Any], let y = expected as? [String: Any] {
            return Set(x.keys).union(y.keys).sorted().flatMap { differences(x[$0], y[$0], label + "." + $0) }
        }
        if let x = actual as? [Any], let y = expected as? [Any], x.count == y.count {
            return zip(x, y).enumerated().flatMap { differences($0.element.0, $0.element.1, label + "[\($0.offset)]") }
        }
        return ["\(label): \(String(describing: actual)) != \(String(describing: expected))"]
    }
}
