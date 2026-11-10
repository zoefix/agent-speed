import Foundation

/// One remote host of the settings (REMOTE 2.1). `extra` keeps the members a newer version wrote, so saving never
/// drops them.
public struct RemoteHost: Equatable, Sendable, Identifiable {
    /// The host id of this computer (REMOTE 2.2); never a remote host's id.
    public static let localId = "local"
    public static let defaultRelayFolder = "~/.cache/agent-speed"

    /// Upper-case UUID string, never reused.
    public var id: String
    /// What the user would type after `ssh`; validated by `RemoteHostValidation.destinationIssue`.
    public var destination: String
    /// Custom display name (1–32 characters, trimmed); nil = automatic.
    public var name: String?
    public var enabled: Bool
    /// nil = `~/.cache/agent-speed`.
    public var relayFolder: String?
    /// The host's Claude Code status line bridge (REMOTE 11.3); absent = false.
    public var quotaClaudeCli: Bool
    var extra: [String: PreservedJSON] = [:]

    public init(
        id: String = UUID().uuidString,
        destination: String,
        name: String? = nil,
        enabled: Bool = true,
        relayFolder: String? = nil,
        quotaClaudeCli: Bool = false
    ) {
        self.id = id
        self.destination = destination
        self.name = RemoteHostValidation.cleanName(name)
        self.enabled = enabled
        self.relayFolder = relayFolder.flatMap { $0.isEmpty ? nil : $0 }
        self.quotaClaudeCli = quotaClaudeCli
    }

    /// The folder on the host, as inserted (unquoted) into the remote commands.
    public var effectiveRelayFolder: String {
        RemoteHostValidation.normalizedFolder(relayFolder ?? Self.defaultRelayFolder)
    }

    /// Both `destination` and `relayFolder` pass REMOTE 2.1; otherwise the host shows `invalidDestination`.
    public var isConnectable: Bool {
        RemoteHostValidation.destinationIssue(destination) == nil
            && (relayFolder.map { RemoteHostValidation.folderIssue($0) == nil } ?? true)
    }

    /// The host part of `destination`: without `ssh://`, user and port.
    public var automaticName: String {
        RemoteHostValidation.automaticName(for: destination)
    }

    /// What reconnects a running connection when it changes (an edit): everything but the name and the switch.
    var connectionIdentity: [String] {
        [destination, effectiveRelayFolder]
    }
}

extension RemoteHost: Codable {
    enum CodingKeys: String, CodingKey, CaseIterable {
        case id, destination, name, enabled, relayFolder, quotaClaudeCli
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let id = try container.decode(String.self, forKey: .id)
        guard RemoteHostValidation.isValidId(id) else {
            throw DecodingError.dataCorruptedError(forKey: .id, in: container, debugDescription: "invalid host id")
        }
        func value<T: Decodable>(_ type: T.Type, _ key: CodingKeys) -> T? {
            (try? container.decodeIfPresent(type, forKey: key)) ?? nil
        }
        self.init(
            id: id,
            destination: value(String.self, .destination) ?? "",
            name: value(String.self, .name),
            enabled: value(Bool.self, .enabled) ?? true,
            relayFolder: value(String.self, .relayFolder),
            quotaClaudeCli: value(Bool.self, .quotaClaudeCli) ?? false
        )
        extra = try PreservedJSON.unknownMembers(in: decoder, known: Set(CodingKeys.allCases.map(\.stringValue)))
    }

    public func encode(to encoder: Encoder) throws {
        try PreservedJSON.encode(extra, to: encoder)
        var container = encoder.container(keyedBy: CodingKeys.self)
        try container.encode(id, forKey: .id)
        try container.encode(destination, forKey: .destination)
        try container.encode(name, forKey: .name)
        try container.encode(enabled, forKey: .enabled)
        try container.encode(relayFolder, forKey: .relayFolder)
        if quotaClaudeCli { try container.encode(true, forKey: .quotaClaudeCli) }
    }
}
