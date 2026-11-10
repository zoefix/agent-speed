import Foundation

/// `GET <base>/api/monitor/usage/quota/limit` of the GLM Coding Plan (QUOTAS 9.8), for ZCode.
public enum ZaiLimits {
    /// Minutes per `unit`.
    static let units: [Int: Int] = [1: VendorValue.dayMinutes, 3: 60, 5: 1, 6: VendorValue.weekMinutes]

    /// Nil unless `success == true` and `code == 200`.
    static func windows(_ body: JSONValue, nowMs: Int64) -> [QuotaWindow]? {
        guard body["success"]?.bool == true, let code = VendorValue.strictNumber(body["code"]), code == 200 else { return nil }
        var windows: [QuotaWindow] = []
        for item in VendorValue.object(body["data"])["limits"]?.array ?? [] {
            guard let item = item.object else { continue }
            let usage = VendorValue.number(item["usage"]), current = VendorValue.number(item["currentValue"])
            let used: Double? = if let usage, let current, usage > 0 { current / usage * 100 } else { VendorValue.number(item["percentage"]) }
            guard let used else { continue }
            var resets = VendorValue.timeMs(item["nextResetTime"])
            let type = item["type"]?.string
            if type == "TIME_LIMIT" {
                windows.append(VendorValue.window(.month, used, resets, name: "MCP"))
                continue
            }
            guard type == "TOKENS_LIMIT" || type == "CREDIT_LIMIT",
                  let unitValue = VendorValue.strictNumber(item["unit"]), unitValue == unitValue.rounded(),
                  let unit = units[Int(unitValue)], let count = VendorValue.number(item["number"]) else { continue }
            let minutes = Int(clamping: VendorValue.truncated(count * Double(unit)))
            let kind = VendorValue.kind(minutes: minutes)
            if kind == .fiveHour, let at = resets, at - nowMs > 301 * 60_000 { resets = nil }
            windows.append(VendorValue.window(kind, used, resets, minutes: kind == .other ? minutes : nil))
        }
        return windows
    }
}
