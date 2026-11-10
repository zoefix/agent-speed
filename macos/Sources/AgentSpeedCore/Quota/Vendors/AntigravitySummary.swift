import Foundation

/// The language server's answers (QUOTAS 9.4): `RetrieveUserQuotaSummary` groups of buckets, or the IDE server's
/// `GetUserStatus` model configs.
public enum AntigravitySummary {
    /// A group's model family from its display name.
    static func groupName(_ display: String?) -> String? {
        let folded = (display ?? "").lowercased()
        if folded.contains("gemini"), folded.contains("argon") { return "Gemini Argon" }
        if folded.contains("gemini") { return "Gemini" }
        if folded.contains("claude") || folded.contains("gpt") || folded.contains("third") { return "Claude/GPT" }
        return display.flatMap { $0.isEmpty ? nil : $0 }
    }

    /// The window a bucket covers, from its `window`, `bucketId` and `displayName`; nil for any other.
    static func kind(_ bucket: [String: JSONValue]) -> QuotaWindowKind? {
        let text = ["window", "bucketId", "displayName"].map { VendorValue.text(bucket[$0]) }.joined(separator: " ")
            .lowercased().replacingOccurrences(of: "_", with: "-")
        if ["weekly", "week", "7d"].contains(where: text.contains) { return .sevenDay }
        if ["5h", "5-hour", "five-hour", "five hour", "session"].contains(where: text.contains) { return .fiveHour }
        return nil
    }

    /// `remainingFraction` as a number, a `{case, value}` oneof, or under `remaining`.
    static func remaining(_ bucket: [String: JSONValue]) -> Double? {
        if let value = VendorValue.strictNumber(bucket["remainingFraction"]) { return value }
        if let oneOf = bucket["remainingFraction"]?.object, oneOf["case"]?.string == "remainingFraction",
           let value = VendorValue.strictNumber(oneOf["value"]) {
            return value
        }
        return VendorValue.strictNumber(bucket["remaining"]?["remainingFraction"])
    }

    static func summaryWindows(_ body: JSONValue) -> [QuotaWindow] {
        let root = body["response"]?.object ?? body.object ?? [:]
        var windows: [QuotaWindow] = []
        for group in root["groups"]?.array ?? [] {
            guard let group = group.object else { continue }
            let name = groupName(group["displayName"]?.string)
            for bucket in group["buckets"]?.array ?? [] {
                guard let bucket = bucket.object, bucket["disabled"]?.bool != true,
                      let kind = kind(bucket), let remaining = remaining(bucket) else { continue }
                windows.append(VendorValue.window(kind, (1 - remaining) * 100, VendorValue.timeMs(bucket["resetTime"]),
                                                  name: name))
            }
        }
        return windows
    }

    static func statusWindows(_ body: JSONValue) -> [QuotaWindow] {
        let configs = body["userStatus"]?["cascadeModelConfigData"]?["clientModelConfigs"]?.array ?? []
        return configs.compactMap { config in
            guard let info = config["quotaInfo"]?.object, let remaining = VendorValue.strictNumber(info["remainingFraction"]) else {
                return nil
            }
            let label = config["label"]?.string.flatMap { $0.isEmpty ? nil : $0 }
            return VendorValue.window(.fiveHour, (1 - remaining) * 100, VendorValue.timeMs(info["resetTime"]), name: label)
        }
    }
}
