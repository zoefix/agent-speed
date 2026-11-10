import Foundation
import Testing
@testable import AgentSpeedCore

/// `spec/fixtures/quota-cases.json` and the cache entries of `spec/fixtures/quota/` (QUOTAS 10).
enum QuotaFixtures {
    nonisolated(unsafe) static let cases: [String: Any] = (try? Fixtures.json("quota-cases.json")) ?? [:]
    static let now: Int64 = (cases["now"] as? NSNumber)?.int64Value ?? 0
    static let timeZone = TimeZone(identifier: "Asia/Taipei")!
    static let languages = ["zh-Hant", "zh-Hans", "en", "ja"]

    static func entry(_ name: String) throws -> [UInt8] {
        [UInt8](try Data(contentsOf: Fixtures.root.appendingPathComponent("quota/\(name)")))
    }

    static func list(_ key: String) -> [[String: Any]] { cases[key] as? [[String: Any]] ?? [] }

    static func window(_ object: [String: Any]) -> QuotaWindow {
        QuotaWindow(kind: QuotaWindowKind(rawValue: object["kind"] as? String ?? "") ?? .other,
                    name: object["name"] as? String, minutes: (object["minutes"] as? NSNumber)?.intValue,
                    usedPercent: (object["usedPercent"] as? NSNumber)?.doubleValue ?? .nan,
                    resetsAtMs: (object["resetsAtMs"] as? NSNumber)?.int64Value)
    }

    static func windows(_ value: Any?) -> [QuotaWindow] {
        (value as? [[String: Any]] ?? []).map(window)
    }

    /// The shared string table, as the app's `Localizer` reads it.
    static let strings: [String: [String: String]] = {
        let url = Fixtures.root.deletingLastPathComponent().appendingPathComponent("strings.json")
        let data = (try? Data(contentsOf: url)) ?? Data()
        let table = (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
        return table.compactMapValues { $0 as? [String: String] }
    }()

    static func format(_ language: String) -> QuotaFormat {
        let locale = switch language {
        case "zh-Hant": Locale(identifier: "zh-Hant-TW")
        case "zh-Hans": Locale(identifier: "zh-Hans-CN")
        case "ja": Locale(identifier: "ja-JP")
        default: Locale(identifier: "en-US")
        }
        return QuotaFormat(strings: QuotaStrings { strings[$0]?[language] ?? $0 }, locale: locale, timeZone: timeZone)
    }

    /// A Codex rollout line as the parser reads it.
    static func extract(_ object: Any) throws -> LineExtraction<CodexLine> {
        let data = try JSONSerialization.data(withJSONObject: object)
        return data.withUnsafeBytes { CodexLineExtractor.extract($0) }
    }
}

extension QuotaSnapshot {
    /// The fixture's form of a snapshot.
    init?(fixture value: Any?, source: SourceKind) {
        guard let object = value as? [String: Any], let observed = (object["observedMs"] as? NSNumber)?.int64Value else {
            return nil
        }
        self.init(source: source, observedMs: observed, windows: QuotaFixtures.windows(object["windows"]))
    }
}
