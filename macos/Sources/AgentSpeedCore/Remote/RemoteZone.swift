import Foundation

/// The app's time zone as `start.zone` and `zone` send it (REMOTE 5.3, 8.1): the IANA id and its TZif data.
public struct RemoteZone: Equatable, Sendable {
    public var id: String
    public var tzif: Data

    /// `TZif` (RFC 8536).
    static let magic = Data("TZif".utf8)

    public init?(id: String, tzif: Data) {
        guard Self.isTZif(tzif), !id.isEmpty, id.utf8.count <= 64,
              id.utf8.allSatisfy({ RemoteHostValidation.isNameByte($0) || $0 == UInt8(ascii: "/") || $0 == UInt8(ascii: "+") })
        else { return nil }
        self.id = id
        self.tzif = tzif
    }

    public init?(_ zone: TimeZone) {
        self.init(id: zone.identifier, tzif: (zone as NSTimeZone).data)
    }

    static func isTZif(_ data: Data) -> Bool {
        data.count >= 44 && data.prefix(4) == magic
    }

    /// The current zone. A zone without TZif data (a fixed offset such as `GMT+0100`) is sent as the `Etc/GMT∓N` zone
    /// of the same whole-hour offset, else as UTC.
    public static func current(_ zone: TimeZone = .current) -> RemoteZone {
        if let remote = RemoteZone(zone) { return remote }
        let seconds = zone.secondsFromGMT()
        if seconds % 3600 == 0 {
            let hours = -seconds / 3600
            let id = hours == 0 ? "Etc/GMT" : "Etc/GMT\(hours > 0 ? "+" : "")\(hours)"
            if let fixed = TimeZone(identifier: id), let remote = RemoteZone(fixed) { return remote }
        }
        let utc = TimeZone(identifier: "UTC").map { ($0 as NSTimeZone).data } ?? Data()
        return RemoteZone(id: "UTC", tzif: utc) ?? RemoteZone(id: "UTC", tzif: utcFallback)!
    }

    /// A minimal version 1 TZif file for UTC (one type, no transitions), if the system has none.
    private static let utcFallback: Data = {
        var bytes = Array("TZif".utf8) + [0] + [UInt8](repeating: 0, count: 15)
        let counts: [UInt32] = [0, 0, 0, 0, 1, 4]
        for count in counts { bytes += withUnsafeBytes(of: count.bigEndian, Array.init) }
        bytes += [0, 0, 0, 0, 0, 0] + Array("UTC".utf8) + [0]
        return Data(bytes)
    }()
}
