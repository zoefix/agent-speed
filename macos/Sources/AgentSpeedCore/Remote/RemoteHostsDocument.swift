import Foundation

/// The settings value `remoteHosts` (REMOTE 2.1): at most 8 hosts in the order added. Decoding is tolerant: a host
/// without a valid id is skipped, a repeated id keeps the first, more than 8 hosts are cut; unknown members of the
/// document and of each host are kept.
public struct RemoteHostsDocument: Equatable, Sendable {
    public static let maxHosts = 8
    public static let currentVersion = 1

    public var version: Int
    public private(set) var hosts: [RemoteHost]
    var extra: [String: PreservedJSON] = [:]

    public init(hosts: [RemoteHost] = []) {
        version = Self.currentVersion
        self.hosts = Self.normalized(hosts)
    }

    public static let empty = RemoteHostsDocument()

    public var canAdd: Bool { hosts.count < Self.maxHosts }

    public func host(_ id: String) -> RemoteHost? {
        hosts.first { $0.id == id }
    }

    /// Appends a host; false at the limit or when its id is invalid or taken.
    @discardableResult
    public mutating func add(_ host: RemoteHost) -> Bool {
        guard canAdd, RemoteHostValidation.isValidId(host.id), self.host(host.id) == nil else { return false }
        hosts.append(host)
        return true
    }

    /// Replaces the host with the same id, keeping its position (and its unknown members unless `host` has some).
    @discardableResult
    public mutating func update(_ host: RemoteHost) -> Bool {
        guard let index = hosts.firstIndex(where: { $0.id == host.id }) else { return false }
        var updated = host
        if updated.extra.isEmpty { updated.extra = hosts[index].extra }
        hosts[index] = updated
        return true
    }

    public mutating func remove(_ id: String) {
        hosts.removeAll { $0.id == id }
    }

    /// The display name of every host: its custom name, else its automatic name made distinct with ` (2)`, ` (3)` …
    /// from every custom name and every earlier host's name (REMOTE 2.1), compared without case.
    public func displayNames() -> [String: String] {
        var taken = Set(hosts.compactMap { $0.name?.lowercased() })
        var names: [String: String] = [:]
        for host in hosts {
            if let name = host.name {
                names[host.id] = name
                continue
            }
            let base = host.automaticName
            var candidate = base
            var counter = 2
            while taken.contains(candidate.lowercased()) {
                candidate = "\(base) (\(counter))"
                counter += 1
            }
            taken.insert(candidate.lowercased())
            names[host.id] = candidate
        }
        return names
    }

    public func displayName(of id: String) -> String? {
        displayNames()[id]
    }

    // MARK: Serialization

    /// Stable JSON (sorted keys), written atomically by the settings store.
    public func data() -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        return (try? encoder.encode(self)) ?? Data()
    }

    /// Nil when the data is not a document; the caller then starts from `empty`.
    public init?(data: Data) {
        guard let decoded = try? JSONDecoder().decode(RemoteHostsDocument.self, from: data) else { return nil }
        self = decoded
    }

    static func normalized(_ hosts: [RemoteHost]) -> [RemoteHost] {
        var seen: Set<String> = []
        let valid = hosts.filter { RemoteHostValidation.isValidId($0.id) && seen.insert($0.id).inserted }
        return Array(valid.prefix(maxHosts))
    }
}

extension RemoteHostsDocument: Codable {
    enum CodingKeys: String, CodingKey, CaseIterable {
        case version, hosts
    }

    private struct LossyHost: Decodable {
        let value: RemoteHost?

        init(from decoder: Decoder) throws {
            value = try? RemoteHost(from: decoder)
        }
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let hosts = (try? container.decodeIfPresent([LossyHost].self, forKey: .hosts)) ?? []
        self.init(hosts: hosts.compactMap(\.value))
        version = max(Self.currentVersion, (try? container.decodeIfPresent(Int.self, forKey: .version)) ?? Self.currentVersion)
        extra = try PreservedJSON.unknownMembers(in: decoder, known: Set(CodingKeys.allCases.map(\.stringValue)))
    }

    public func encode(to encoder: Encoder) throws {
        try PreservedJSON.encode(extra, to: encoder)
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(version, forKey: .version)
        try container.encode(hosts, forKey: .hosts)
    }
}
