import Foundation

/// `GET api.kimi.com/coding/v1/usages` (QUOTAS 9.5): the current `usages` shape, else the legacy `limits` and
/// `usage`.
public enum KimiUsage {
    static let timeUnits = ["TIME_UNIT_MINUTE": 1, "TIME_UNIT_HOUR": 60, "TIME_UNIT_DAY": VendorValue.dayMinutes,
                            "TIME_UNIT_WEEK": VendorValue.weekMinutes]

    static func reset(_ members: [String: JSONValue]) -> Int64? {
        VendorValue.pick(members, "resetTime", "reset_time", "resetAt", "reset_at").flatMap(VendorValue.timeMs)
    }

    /// `used / limit × 100`, with `used = limit − remaining` when `used` is missing.
    static func counts(_ members: [String: JSONValue]) -> Double? {
        guard let limit = VendorValue.number(members["limit"]), limit > 0 else { return nil }
        var used = VendorValue.number(members["used"])
        if used == nil, let remaining = VendorValue.number(members["remaining"]) { used = limit - remaining }
        return used.map { $0 / limit * 100 }
    }

    static func windows(_ body: JSONValue) -> [QuotaWindow] {
        var windows: [QuotaWindow] = []
        if let usages = body["usages"]?.object {
            for (key, kind) in [("limit_5h", QuotaWindowKind.fiveHour), ("limit_7d", .sevenDay)] {
                if let window = usages[key]?.object, let ratio = VendorValue.number(window["used_ratio"]) {
                    windows.append(VendorValue.window(kind, ratio * 100, reset(window)))
                }
            }
            for key in ["limit_month_total", "limit_month"] {
                if let window = usages[key]?.object, let ratio = VendorValue.number(window["used_ratio"]) {
                    windows.append(VendorValue.window(.month, ratio * 100, reset(window)))
                    break
                }
            }
            if !windows.isEmpty { return windows }
        }
        for item in body["limits"]?.array ?? [] {
            guard let item = item.object else { continue }
            let window = VendorValue.object(item["window"])
            let detail = VendorValue.object(item["detail"])
            guard let duration = VendorValue.number(window["duration"]),
                  let unit = window["timeUnit"]?.string.flatMap({ timeUnits[$0] }),
                  let used = counts(detail) else { continue }
            let minutes = Int(clamping: VendorValue.truncated(duration * Double(unit)))
            let kind = VendorValue.kind(minutes: minutes)
            windows.append(VendorValue.window(kind, used, reset(detail), minutes: kind == .other ? minutes : nil))
        }
        if let usage = body["usage"]?.object, !windows.contains(where: { $0.kind == .sevenDay }), let used = counts(usage) {
            windows.append(VendorValue.window(.sevenDay, used, reset(usage)))
        }
        return windows
    }
}
