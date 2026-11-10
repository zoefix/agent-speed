import Foundation

/// The price table as a `start.prices` / `prices.table` value: a JSON object validated by the strict parser and
/// written compactly (insignificant whitespace removed, every literal kept as written), so it fits one line.
public struct RemotePriceTableJSON: Equatable, Sendable {
    public let text: String

    public init?(_ data: Data) {
        guard data.count <= RelayLineEncoder.maxLineBytes, (try? JSONParser.parse(data))?.object != nil else { return nil }
        text = Self.compact(data)
    }

    static func compact(_ data: Data) -> String {
        var out: [UInt8] = []
        out.reserveCapacity(data.count)
        var inString = false
        var escaped = false
        for byte in data {
            if inString {
                out.append(byte)
                if escaped {
                    escaped = false
                } else if byte == UInt8(ascii: "\\") {
                    escaped = true
                } else if byte == UInt8(ascii: "\"") {
                    inString = false
                }
            } else if byte == UInt8(ascii: "\"") {
                inString = true
                out.append(byte)
            } else if ![0x20, 0x09, 0x0A, 0x0D].contains(byte) {
                out.append(byte)
            }
        }
        return String(decoding: out, as: UTF8.self)
    }
}

/// The app → relay lines of REMOTE 5.3, each a compact JSON object ending in a newline (at most 4 MiB).
public enum RelayLineEncoder {
    public static let maxLineBytes = 4 << 20

    public static func start(todayStartMs: Int64, sources: Set<SourceKind>, zone: RemoteZone,
                             prices: RemotePriceTableJSON, quota: RemoteQuotaConfig? = nil) -> Data? {
        let keys = SourceKind.allCases.filter(sources.contains).map { quoted($0.rawValue) }.joined(separator: ",")
        let quotaMember = quota.map { ",\"quota\":\(quotaObject($0))" } ?? ""
        return line("{\"t\":\"start\",\"protocol\":\(RemoteProtocol.version),\"todayStartMs\":\(todayStartMs),"
            + "\"sources\":[\(keys)],\"zone\":\(zoneObject(zone)),\"prices\":\(prices.text)\(quotaMember)}")
    }

    /// REMOTE 11.1: sent whenever the quota config changes.
    public static func quota(_ config: RemoteQuotaConfig) -> Data? {
        line("{\"t\":\"quota\",\"config\":\(quotaObject(config))}")
    }

    static func quotaObject(_ config: RemoteQuotaConfig) -> String {
        let keys = SourceKind.allCases.filter(config.sources.contains).map { quoted($0.rawValue) }.joined(separator: ",")
        return "{\"sources\":[\(keys)],\"network\":\(config.network),\"grokLive\":\(config.grokLive),"
            + "\"claudeBridge\":\(config.claudeBridge)}"
    }

    public static func ping(id: Int64) -> Data? {
        line("{\"t\":\"ping\",\"id\":\(id)}")
    }

    public static func today(todayStartMs: Int64) -> Data? {
        line("{\"t\":\"today\",\"todayStartMs\":\(todayStartMs)}")
    }

    public static func prices(_ table: RemotePriceTableJSON) -> Data? {
        line("{\"t\":\"prices\",\"table\":\(table.text)}")
    }

    public static func zone(_ zone: RemoteZone) -> Data? {
        line("{\"t\":\"zone\",\"id\":\(quoted(zone.id)),\"tzif\":\(quoted(zone.tzif.base64EncodedString()))}")
    }

    /// `digests`: `"<day>/<source>"` → digest, in key order so a request is reproducible.
    public static func history(digests: [String: String]) -> Data? {
        let members = digests.sorted { $0.key.utf8.lexicographicallyPrecedes($1.key.utf8) }
            .map { "\(quoted($0.key)):\(quoted($0.value))" }
            .joined(separator: ",")
        return line("{\"t\":\"history\",\"digests\":{\(members)}}")
    }

    public static func watch(_ on: Bool) -> Data? {
        line("{\"t\":\"watch\",\"on\":\(on)}")
    }

    private static func zoneObject(_ zone: RemoteZone) -> String {
        "{\"id\":\(quoted(zone.id)),\"tzif\":\(quoted(zone.tzif.base64EncodedString()))}"
    }

    private static func quoted(_ text: String) -> String {
        ReportJSON.quoted(text)
    }

    private static func line(_ text: String) -> Data? {
        let data = Data((text + "\n").utf8)
        return data.count <= maxLineBytes ? data : nil
    }
}
