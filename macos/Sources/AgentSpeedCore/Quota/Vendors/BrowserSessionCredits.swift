import Foundation

/// The two browser-session sources (QUOTAS 9.9): Qoder's big-model credits and WorkBuddy's resource packages.
public enum BrowserSessionCredits {
    /// `totalQuota` and `sharedQuota` summed into one `credits` window.
    static func qoderWindows(_ body: JSONValue) -> [QuotaWindow] {
        guard let members = body.object else { return [] }
        var used = 0.0, limit = 0.0, seen = false
        var percents: [Double] = []
        for key in ["totalQuota", "sharedQuota"] {
            guard let summary = members[key]?["quotaSummary"]?.object else { continue }
            let value = VendorValue.pick(summary, "usedValue", "used_value").flatMap(VendorValue.number)
            let cap = VendorValue.pick(summary, "limitValue", "limit_value").flatMap(VendorValue.number)
            if let value, let cap, cap > 0 {
                used += value
                limit += cap
                seen = true
            } else if let percent = VendorValue.pick(summary, "usagePercentage", "usage_percentage").flatMap(VendorValue.number) {
                percents.append(percent)
            }
        }
        let resets = VendorValue.pick(members, "nextResetAt", "next_reset_at").flatMap(VendorValue.timeMs)
        if seen { return [VendorValue.window(.credits, used / limit * 100, resets)] }
        if let highest = percents.max() { return [VendorValue.window(.credits, highest, resets)] }
        return []
    }

    /// Nil unless `code == 0`: the `credits` packages summed into one monthly window without a reset time.
    static func workBuddyWindows(_ body: JSONValue) -> [QuotaWindow]? {
        guard let code = VendorValue.strictNumber(body["code"]), code == 0 else { return nil }
        var total = 0.0, remain = 0.0, seen = false
        for package in VendorValue.object(body["data"])["Packages"]?.array ?? [] {
            guard let package = package.object, package["CapacityUnit"]?.string == "credits",
                  let cycle = VendorValue.number(package["CycleTotalCapacity"]),
                  let left = VendorValue.number(package["CycleRemainCapacity"]) else { continue }
            total += cycle
            remain += left
            seen = true
        }
        guard seen, total > 0 else { return [] }
        return [VendorValue.window(.month, (total - remain) / total * 100, nil)]
    }
}
