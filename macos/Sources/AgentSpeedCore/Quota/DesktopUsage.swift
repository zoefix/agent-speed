import Foundation

/// The usage response Claude Desktop caches (QUOTAS 4.2, 4.3): which entries hold it and the windows of its body.
enum DesktopUsage {
    /// The decoded body's size limit.
    static let bodyLimit = 1 << 20

    /// A usage entry: its URL, when the response was made and its windows.
    struct Entry: Equatable {
        var url: String
        var observedMs: Int64
        var windows: [QuotaWindow]
    }

    /// `https://claude.ai/api/organizations/<uuid>/usage`, optionally followed by `?` and any query.
    static func isUsageURL(_ url: String) -> Bool {
        let prefix = "https://claude.ai/api/organizations/"
        guard url.hasPrefix(prefix) else { return false }
        let rest = Array(url.utf8.dropFirst(prefix.utf8.count))
        let groups = [8, 4, 4, 4, 12]
        var index = 0
        for (k, length) in groups.enumerated() {
            guard index + length <= rest.count, rest[index..<(index + length)].allSatisfy(isHex) else { return false }
            index += length
            if k < groups.count - 1 {
                guard index < rest.count, rest[index] == UInt8(ascii: "-") else { return false }
                index += 1
            }
        }
        let tail = rest[index...]
        let usage = Array("/usage".utf8)
        guard tail.starts(with: usage) else { return false }
        let after = tail.dropFirst(usage.count)
        return after.isEmpty || after.first == UInt8(ascii: "?")
    }

    private static func isHex(_ byte: UInt8) -> Bool {
        (UInt8(ascii: "0")...UInt8(ascii: "9")).contains(byte) || (UInt8(ascii: "a")...UInt8(ascii: "f")).contains(byte)
            || (UInt8(ascii: "A")...UInt8(ascii: "F")).contains(byte)
    }

    /// Whether a key (from an entry's first bytes) names a usage response.
    static func isUsageKey(_ key: String) -> Bool { isUsageURL(SimpleCacheEntry.url(ofKey: key)) }

    /// The entry of a whole cache file, nil unless it is a usage response with status 200 and a JSON object body.
    /// `mtimeMs` stands in for a missing `date` header.
    static func entry(_ bytes: [UInt8], mtimeMs: Int64) -> Entry? {
        guard let file = SimpleCacheEntry(bytes) else { return nil }
        let url = SimpleCacheEntry.url(ofKey: file.key)
        guard isUsageURL(url), let info = CachedResponseInfo(file.responseInfo), info.status == 200,
              let coding = ContentDecoding.coding(info.headers["content-encoding"]),
              let decoded = ContentDecoding.decode(Array(file.body), coding: coding, limit: bodyLimit),
              let body = try? JSONParser.parse(Data(decoded)), body.object != nil else { return nil }
        let observed = info.headers["date"].flatMap(CachedResponseInfo.dateMs) ?? mtimeMs
        return Entry(url: url, observedMs: observed, windows: windows(body))
    }

    /// The account's windows (QUOTAS 4.3): `five_hour`, `seven_day`, every scoped weekly limit by model, then Opus and
    /// Sonnet weeks unless a scoped limit named them already.
    static func windows(_ body: JSONValue) -> [QuotaWindow] {
        var found: [QuotaWindow] = []
        for (field, kind) in [("five_hour", QuotaWindowKind.fiveHour), ("seven_day", .sevenDay)] {
            if let window = body[field], let used = number(window["utilization"]) {
                found.append(QuotaWindow(kind: kind, usedPercent: used, resetsAtMs: resetMs(window["resets_at"])))
            }
        }
        var names: Set<String> = []
        for item in body["limits"]?.array ?? [] {
            guard item.object != nil, item["kind"]?.string == "weekly_scoped", let used = number(item["percent"]),
                  let name = item["scope"]?["model"]?["display_name"]?.string,
                  !name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                  names.insert(QuotaText.fold(name)).inserted else { continue }
            found.append(QuotaWindow(kind: .modelWeek, name: name, usedPercent: used, resetsAtMs: resetMs(item["resets_at"])))
        }
        for (field, name) in [("seven_day_opus", "Opus"), ("seven_day_sonnet", "Sonnet")] {
            guard let window = body[field], let used = number(window["utilization"]),
                  names.insert(QuotaText.fold(name)).inserted else { continue }
            found.append(QuotaWindow(kind: .modelWeek, name: name, usedPercent: used, resetsAtMs: resetMs(window["resets_at"])))
        }
        return found
    }

    /// A finite number, never a boolean.
    static func number(_ value: JSONValue?) -> Double? {
        guard let double = value?.literal?.double, double.isFinite else { return nil }
        return double
    }

    /// ISO 8601 with up to 6 fractional digits and an offset; nil otherwise.
    static func resetMs(_ value: JSONValue?) -> Int64? {
        value?.string.flatMap(Timestamp.parse)
    }

    /// The newest observation wins; ties go to the newer file (QUOTAS 4.4).
    static func newest<T>(_ items: [(entry: Entry, mtimeMs: Int64, tag: T)]) -> (entry: Entry, mtimeMs: Int64, tag: T)? {
        var best: (entry: Entry, mtimeMs: Int64, tag: T)?
        for item in items {
            guard let current = best else {
                best = item
                continue
            }
            if (item.entry.observedMs, item.mtimeMs) > (current.entry.observedMs, current.mtimeMs) { best = item }
        }
        return best
    }
}
