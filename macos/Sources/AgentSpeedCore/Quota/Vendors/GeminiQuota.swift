import Foundation

/// Gemini CLI's Code Assist quota (QUOTAS 9.6): `loadCodeAssist` gives the project, `retrieveUserQuota` the daily
/// buckets per model, grouped by family with the lowest remaining fraction.
public enum GeminiQuota {
    static let familyOrder = ["Pro": 0, "Flash": 1, "Flash Lite": 2]

    static func family(_ modelId: String?) -> String? {
        let folded = (modelId ?? "").lowercased()
        if folded.contains("flash-lite") { return "Flash Lite" }
        if folded.contains("flash") { return "Flash" }
        if folded.contains("pro") { return "Pro" }
        return modelId
    }

    /// `cloudaicompanionProject`: a string, or an object's `id`.
    static func project(_ body: JSONValue) -> String? {
        switch body["cloudaicompanionProject"] {
        case .string(let id)? where !id.isEmpty: id
        case .object(let members)?: members["id"]?.string
        default: nil
        }
    }

    static func windows(_ body: JSONValue) -> [QuotaWindow] {
        var best: [String: (remaining: Double, resets: Int64?)] = [:]
        for bucket in body["buckets"]?.array ?? [] {
            guard let bucket = bucket.object, let remaining = VendorValue.strictNumber(bucket["remainingFraction"]),
                  let family = family(bucket["modelId"]?.string), !family.isEmpty else { continue }
            if best[family].map({ remaining < $0.remaining }) ?? true {
                best[family] = (remaining, VendorValue.timeMs(bucket["resetTime"]))
            }
        }
        let names = best.keys.sorted { lhs, rhs in
            let (a, b) = (familyOrder[lhs] ?? 3, familyOrder[rhs] ?? 3)
            return a != b ? a < b : lhs < rhs
        }
        return names.map { name in
            VendorValue.window(.other, (1 - best[name]!.remaining) * 100, best[name]!.resets, name: name,
                               minutes: VendorValue.dayMinutes)
        }
    }
}
