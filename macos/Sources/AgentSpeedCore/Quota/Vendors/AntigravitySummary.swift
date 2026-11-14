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

    /// agy's usage report (QUOTAS 9.4 *CLI usage report*): `status` SUCCESS, `command.name` usage and
    /// `command.data` a summary in snake case; nil for anything else or a report without a window.
    static func cliWindows(_ stdout: Data) -> [QuotaWindow]? {
        guard let report = (try? JSONParser.parse(stdout))?.object, report["status"]?.string == "SUCCESS",
              let command = report["command"]?.object, command["name"]?.string == "usage",
              var summary = command["data"]?.object else { return nil }
        if let groups = summary["groups"]?.array {
            summary["groups"] = .array(groups.map { group in
                guard var members = group.object else { return group }
                members = camelCase(members, ["displayName": "name"])
                if let buckets = members["buckets"]?.array {
                    members["buckets"] = .array(buckets.map { bucket in
                        bucket.object.map { .object(camelCase($0, cliBucketNames)) } ?? bucket
                    })
                }
                return .object(members)
            })
        }
        let windows = summaryWindows(.object(summary))
        return windows.isEmpty ? nil : windows
    }

    static let cliBucketNames = ["bucketId": "id", "displayName": "name", "remainingFraction": "remaining_fraction",
                                 "resetTime": "reset_time"]

    /// Each camel-case member the object lacks, from its snake-case name.
    private static func camelCase(_ members: [String: JSONValue], _ names: [String: String]) -> [String: JSONValue] {
        var out = members
        for (camel, snake) in names where out[camel] == nil {
            if let value = members[snake] { out[camel] = value }
        }
        return out
    }

    /// `agy --version`: the trimmed output as `major.minor.patch` in digits; nil for anything else.
    static func agyVersion(_ text: String) -> [Int]? {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let parts = trimmed.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy { ("0"..."9").contains($0) } }) else { return nil }
        let numbers = parts.compactMap { Int($0) }
        return numbers.count == 3 ? numbers : nil
    }

    /// At least 1.1.11: older print modes sent an unknown slash command to the model.
    static func agySupported(_ version: [Int]) -> Bool {
        version.lexicographicallyPrecedes([1, 1, 11]) == false
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
