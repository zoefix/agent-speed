import Foundation

/// `GET api.factory.ai/api/billing/limits` (QUOTAS 9.7): the standard pool and the optional `Core` pool, each with
/// a 5-hour, weekly and monthly window.
public enum DroidLimits {
    /// The `FACTORY_API_KEY=` line of `~/.factory/.env` (an `export ` prefix and matching quotes allowed); every other
    /// line is passed over unread.
    static func key(fromEnvFile text: String) -> String? {
        for line in text.split(omittingEmptySubsequences: false, whereSeparator: { $0 == "\n" || $0 == "\r" }) {
            var stripped = line.trimmingCharacters(in: .whitespaces)
            if stripped.hasPrefix("export ") { stripped = String(stripped.dropFirst(7)).trimmingCharacters(in: .whitespaces) }
            guard stripped.hasPrefix("FACTORY_API_KEY=") else { continue }
            var value = String(stripped.dropFirst("FACTORY_API_KEY=".count)).trimmingCharacters(in: .whitespaces)
            if value.count >= 2, let first = value.first, first == value.last, first == "'" || first == "\"" {
                value = String(value.dropFirst().dropLast())
            }
            return value.isEmpty ? nil : value
        }
        return nil
    }

    static func windows(_ body: JSONValue, nowMs: Int64) -> [QuotaWindow] {
        let limits = VendorValue.object(body["limits"])
        var windows: [QuotaWindow] = []
        for (pool, name) in [("standard", String?.none), ("core", "Core")] {
            guard let pool = limits[pool]?.object else { continue }
            for (key, kind) in [("fiveHour", QuotaWindowKind.fiveHour), ("weekly", .sevenDay), ("monthly", .month)] {
                guard let window = pool[key]?.object, let used = VendorValue.number(window["usedPercent"]) else { continue }
                let end = VendorValue.timeMs(window["windowEnd"])
                if let remaining = VendorValue.number(window["secondsRemaining"]) {
                    windows.append(VendorValue.window(kind, used, nowMs &+ VendorValue.truncated(remaining * 1000), name: name))
                } else if let end, end <= nowMs {
                    windows.append(VendorValue.window(kind, 0, nil, name: name))
                } else {
                    windows.append(VendorValue.window(kind, used, end, name: name))
                }
            }
        }
        return windows
    }
}
