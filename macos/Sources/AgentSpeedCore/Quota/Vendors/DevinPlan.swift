import Foundation

/// The plan Devin Desktop caches in its `state.vscdb` (QUOTAS 9.3): a daily and a weekly remaining percent. Several
/// rows are several accounts; the greatest `endTimestamp` wins. `accountIdentityText` is never read.
public enum DevinPlan {
    static let keyPatterns = ["windsurf.reactSettings.cachedPlanInfoData%", "windsurf.settings.cachedPlanInfo%"]

    /// `rows`: the query's `(key, value)` pairs, values as UTF-8 bytes.
    static func windows(_ rows: [(key: String, value: Data)]) -> [QuotaWindow] {
        var best: (end: Double, plan: [String: JSONValue])?
        for row in rows {
            guard row.value.count <= 1 << 20, let plan = (try? JSONParser.parse(row.value))?.object else { continue }
            let end = VendorValue.number(plan["endTimestamp"]).flatMap { $0 == 0 ? nil : $0 } ?? 0
            if best.map({ end > $0.end }) ?? true { best = (end, plan) }
        }
        guard let plan = best?.plan else { return [] }
        var windows: [QuotaWindow] = []
        for (prefix, kind, minutes) in [("daily", QuotaWindowKind.other, Optional(VendorValue.dayMinutes)),
                                        ("weekly", .sevenDay, nil)] {
            if plan["hide\(prefix.prefix(1).uppercased())\(prefix.dropFirst())Quota"]?.bool == true { continue }
            guard let remaining = VendorValue.number(plan["\(prefix)RemainingPercent"]) else { continue }
            windows.append(VendorValue.window(kind, 100 - remaining, VendorValue.timeMs(plan["\(prefix)ResetAtUnix"]),
                                              minutes: minutes))
        }
        return windows
    }

    /// `<folder>/logs/<YYYYMMDDTHHMMSS>`: a launch folder's name read as local time.
    static func launchMs(_ name: String, timeZone: TimeZone) -> Int64? {
        let bytes = Array(name.utf8)
        guard bytes.count == 15, bytes[8] == UInt8(ascii: "T"),
              bytes.enumerated().allSatisfy({ $0.offset == 8 || (48...57).contains($0.element) }) else { return nil }
        func number(_ range: Range<Int>) -> Int { bytes[range].reduce(0) { $0 * 10 + Int($1 - 48) } }
        var parts = DateComponents()
        parts.year = number(0..<4)
        parts.month = number(4..<6)
        parts.day = number(6..<8)
        parts.hour = number(9..<11)
        parts.minute = number(11..<13)
        parts.second = number(13..<15)
        guard (1...12).contains(parts.month!), (1...31).contains(parts.day!), parts.hour! < 24, parts.minute! < 60,
              parts.second! < 60 else { return nil }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        guard let date = calendar.date(from: parts) else { return nil }
        return Clock.milliseconds(date)
    }
}
