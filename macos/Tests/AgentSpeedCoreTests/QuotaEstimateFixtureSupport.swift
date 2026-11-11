import Foundation
import Testing
@testable import AgentSpeedCore

/// `spec/fixtures/quota-estimate-cases.json` (QUOTAS 10, REPORTING-EXTENSIONS 11), with helpers that turn the
/// reference's values into the port's and compare outputs as the reference writes them.
enum EstimateFixtures {
    nonisolated(unsafe) static let cases: [String: Any] = (try? Fixtures.json("quota-estimate-cases.json")) ?? [:]
    static let minute: Int64 = 60_000
    static let hour: Int64 = 3_600_000
    static let day: Int64 = 86_400_000
    static let t0: Int64 = 1_794_000_000_000 / day * day

    static func list(_ value: Any?) -> [[String: Any]] { value as? [[String: Any]] ?? [] }
    static func object(_ value: Any?) -> [String: Any] { value as? [String: Any] ?? [:] }
    static func int(_ value: Any?) -> Int64? { (value as? NSNumber)?.int64Value }
    static func double(_ value: Any?) -> Double? { (value as? NSNumber)?.doubleValue }
    static func string(_ value: Any?) -> String? { value as? String }

    static func salt(_ hex: String) -> Data {
        var data = Data()
        var index = hex.startIndex
        while index < hex.endIndex {
            let next = hex.index(index, offsetBy: 2)
            data.append(UInt8(hex[index..<next], radix: 16)!)
            index = next
        }
        return data
    }

    /// Python's `round(x, digits)` closely enough for the fixtures, `inf` as None (the reference's `fnum`).
    static func fnum(_ x: Double?) -> Any {
        guard let x, x.isFinite else { return NSNull() }
        return (x * 1000).rounded(.toNearestOrEven) / 1000
    }

    static func round6(_ x: Double) -> Double { (x * 1e6).rounded(.toNearestOrEven) / 1e6 }

    /// The reference's `obs()` of a unit case.
    static func observation(_ o: [String: Any]) -> QuotaObservation {
        QuotaObservation(account: string(o["account"]) ?? "a", source: string(o["source"]) ?? "codex",
                         kind: QuotaWindowKind(rawValue: string(o["kind"]) ?? "") ?? .other, name: string(o["name"]),
                         minutes: Int(int(o["minutes"]) ?? 0), t: int(o["t"])!, tLo: int(o["tLo"]) ?? int(o["t"])!,
                         p: double(o["p"])!, resetsMs: int(o["R"])!, plan: string(o["plan"]), tier: string(o["tier"]))
    }

    /// `ledger_from`: account `a`, source `codex`, model `gpt-5.5` unless the row says otherwise.
    static func ledger(_ rows: [[String: Any]]) -> QuotaEstimateLedger {
        let ledger = QuotaEstimateLedger()
        for r in rows {
            ledger.add(account: string(r["account"]) ?? "a", source: string(r["source"]) ?? "codex",
                       model: string(r["model"]) ?? "gpt-5.5", endMs: int(r["at"])!, cost: int(r["cost"]),
                       requests: int(r["requests"]) ?? 1)
        }
        return ledger
    }

    static func window(_ w: [String: Any]) -> QuotaWindow {
        QuotaWindow(kind: QuotaWindowKind(rawValue: string(w["kind"]) ?? "") ?? .other, name: string(w["name"]),
                    minutes: int(w["minutes"]).map { Int($0) }, usedPercent: double(w["usedPercent"]) ?? .nan,
                    resetsAtMs: int(w["resetsAtMs"]))
    }

    static func codexLimits(_ object: [String: Any]) throws -> CodexRateLimits {
        let data = try JSONSerialization.data(withJSONObject: object)
        let parsed: CodexRateLimits? = data.withUnsafeBytes { buffer in
            var scanner = JSONScanner(buffer)
            return try? CodexRateLimits.scan(&scanner)
        }
        return try #require(parsed)
    }

    /// A value as JSON reads it back, so outputs compare like the fixture's.
    static func foundation(_ json: ReportJSON) -> Any {
        (try? JSONSerialization.jsonObject(with: json.compactData, options: [.fragmentsAllowed])) ?? NSNull()
    }

    static func foundation(_ data: Data) -> Any {
        (try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])) ?? NSNull()
    }

    /// The differences between two JSON values: numbers within a relative 1e-9, everything else equal.
    static func differences(_ actual: Any?, _ expected: Any?, path: String = "") -> [String] {
        let a = actual ?? NSNull(), e = expected ?? NSNull()
        if e is NSNull || a is NSNull {
            return (e is NSNull && a is NSNull) ? [] : ["\(path): \(a) != \(e)"]
        }
        if let en = e as? NSNumber {
            guard let an = a as? NSNumber else { return ["\(path): \(a) != \(e)"] }
            let eBool = CFGetTypeID(en) == CFBooleanGetTypeID(), aBool = CFGetTypeID(an) == CFBooleanGetTypeID()
            if eBool || aBool { return eBool == aBool && en.boolValue == an.boolValue ? [] : ["\(path): \(a) != \(e)"] }
            let x = an.doubleValue, y = en.doubleValue
            return abs(x - y) <= 1e-9 * max(1, abs(x), abs(y)) ? [] : ["\(path): \(x) != \(y)"]
        }
        if let es = e as? String { return (a as? String) == es ? [] : ["\(path): \(a) != \(es)"] }
        if let ea = e as? [Any] {
            guard let aa = a as? [Any] else { return ["\(path): not an array"] }
            guard aa.count == ea.count else { return ["\(path): \(aa.count) items != \(ea.count)"] }
            return zip(aa, ea).enumerated().flatMap { differences($0.element.0, $0.element.1, path: "\(path)/\($0.offset)") }
        }
        if let eo = e as? [String: Any] {
            guard let ao = a as? [String: Any] else { return ["\(path): not an object"] }
            let keys = Set(eo.keys).union(ao.keys)
            return keys.sorted().flatMap { differences(ao[$0], eo[$0], path: "\(path)/\($0)") }
        }
        return ["\(path): unexpected \(e)"]
    }
}
