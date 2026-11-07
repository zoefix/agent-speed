import Foundation

/// The settings value `lan` (LAN 2.1): the server and at most eight targets. Decoding is tolerant: a member that breaks
/// its rule falls back to its default, a target that breaks one is left out, and another `version` reads as the
/// defaults. `instance` and `server.key` stay nil until they are first needed.
public struct LanDocument: Equatable, Sendable {
    public static let maxTargets = 8
    public static let defaultPort = 7247
    public static let ports = 1024...65535
    public static let nameLimit = 64
    public static let targetKeyLimit = 128
    public static let keyAlphabet = Array("0123456789ABCDEFGHJKMNPQRSTVWXYZ")
    public static let keyLength = 12

    public struct Server: Equatable, Sendable {
        public var enabled: Bool
        public var port: Int
        /// The access key (12 Crockford Base32 characters); nil until the server is first turned on.
        public var key: String?

        public init(enabled: Bool = false, port: Int = LanDocument.defaultPort, key: String? = nil) {
            self.enabled = enabled
            self.port = port
            self.key = key
        }
    }

    public enum Kind: String, CaseIterable, Sendable {
        case udp, http
    }

    public struct Target: Equatable, Identifiable, Sendable {
        public var id: String
        public var name: String
        public var kind: Kind
        public var address: String
        public var key: String?
        public var enabled: Bool

        public init(id: String = LanDocument.newId(), name: String = "", kind: Kind, address: String,
                    key: String? = nil, enabled: Bool = true) {
            self.id = id
            self.name = name
            self.kind = kind
            self.address = address
            self.key = key
            self.enabled = enabled
        }

        /// The row's title: the name, or the address when the name is empty (DESIGN 14).
        public var displayName: String { name.isEmpty ? address : name }

        /// The parsed address; nil only for a target built in code with an invalid one.
        public var parsed: LanTargetAddress? { try? LanTargetAddress.parse(kind: kind, address).get() }
    }

    public var instance: String?
    public var server: Server
    public private(set) var targets: [Target]

    public init(instance: String? = nil, server: Server = Server(), targets: [Target] = []) {
        self.instance = instance
        self.server = server
        self.targets = []
        for target in targets { add(target) }
    }

    public static let empty = LanDocument()

    public var canAdd: Bool { targets.count < Self.maxTargets }

    /// The feed runs while the server is on or any target is enabled (LAN 3.3), given the master switch.
    public var wantsFeed: Bool { server.enabled || targets.contains(where: \.enabled) }

    /// Appends a target; false at the limit, with an id that is taken or invalid, or with an invalid address.
    @discardableResult
    public mutating func add(_ target: Target) -> Bool {
        guard canAdd, let normalized = Self.normalized(target), !targets.contains(where: { $0.id == normalized.id })
        else { return false }
        targets.append(normalized)
        return true
    }

    /// Replaces the target with the same id in place; false when there is none or the new values are invalid.
    @discardableResult
    public mutating func update(_ target: Target) -> Bool {
        guard let index = targets.firstIndex(where: { $0.id == target.id }), let normalized = Self.normalized(target)
        else { return false }
        targets[index] = normalized
        return true
    }

    public mutating func remove(_ id: String) {
        targets.removeAll { $0.id == id }
    }

    /// Creates the instance id the first time the document is used (LAN 2.1).
    public mutating func ensureInstance() {
        if instance == nil { instance = Self.newId() }
    }

    /// Turns the server on, creating the access key (and the instance id) when missing (LAN 2.1).
    public mutating func enableServer() {
        ensureInstance()
        if server.key == nil { server.key = Self.newKey() }
        server.enabled = true
    }

    // MARK: Keys and ids

    /// 12 characters of the Crockford Base32 alphabet from the system's cryptographic generator.
    public static func newKey() -> String {
        var generator = SystemRandomNumberGenerator()
        return String((0..<keyLength).map { _ in keyAlphabet[Int(generator.next(upperBound: UInt32(keyAlphabet.count)))] })
    }

    public static func isValidKey(_ key: String) -> Bool {
        key.count == keyLength && key.allSatisfy { keyAlphabet.contains($0) }
    }

    /// `K7Q4-M2X9-PT3D`.
    public static func displayKey(_ key: String) -> String {
        stride(from: 0, to: key.count, by: 4).map { start -> String in
            let from = key.index(key.startIndex, offsetBy: start)
            let to = key.index(from, offsetBy: min(4, key.count - start))
            return String(key[from..<to])
        }.joined(separator: "-")
    }

    public static func newId() -> String { UUID().uuidString.lowercased() }

    static func isValidId(_ value: String) -> Bool {
        value.range(of: "^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                    options: .regularExpression) != nil
    }

    /// Trimmed text of `low`–`high` scalars without control characters, or nil.
    static func text(_ value: String, _ range: ClosedRange<Int>) -> String? {
        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard range.contains(trimmed.unicodeScalars.count),
              !trimmed.unicodeScalars.contains(where: { $0.value < 0x20 || $0.value == 0x7F }) else { return nil }
        return trimmed
    }

    private static func normalized(_ target: Target) -> Target? {
        guard isValidId(target.id) else { return nil }
        let address = target.address.trimmingCharacters(in: .whitespacesAndNewlines)
        guard case .success = LanTargetAddress.parse(kind: target.kind, address) else { return nil }
        var result = target
        result.address = address
        result.name = text(target.name, 0...nameLimit) ?? ""
        result.key = target.key.flatMap { text($0, 1...targetKeyLimit) }
        return result
    }

    // MARK: Serialization

    /// From the stored JSON object (or anything else, which reads as the defaults).
    public init(json: Any?) {
        self.init()
        guard let document = json as? [String: Any], Self.integer(document["version"]) == 1 else { return }
        if let instance = document["instance"] as? String, Self.isValidId(instance) { self.instance = instance }
        if let server = document["server"] as? [String: Any] {
            if let enabled = Self.boolean(server["enabled"]) { self.server.enabled = enabled }
            if let port = Self.integer(server["port"]), Self.ports.contains(port) { self.server.port = port }
            if let key = server["key"] as? String, Self.isValidKey(key) { self.server.key = key }
        }
        for entry in document["targets"] as? [Any] ?? [] {
            guard targets.count < Self.maxTargets else { break }
            guard let entry = entry as? [String: Any], let id = entry["id"] as? String,
                  let kind = (entry["kind"] as? String).flatMap(Kind.init(rawValue:)),
                  let address = entry["address"] as? String else { continue }
            let key: String? = (entry["key"] as? String).flatMap { Self.text($0, 1...Self.targetKeyLimit) }
            add(Target(id: id, name: entry["name"] as? String ?? "", kind: kind, address: address, key: key,
                       enabled: Self.boolean(entry["enabled"]) ?? true))
        }
    }

    public init(data: Data) {
        self.init(json: try? JSONSerialization.jsonObject(with: data))
    }

    /// The JSON object as stored (null for missing values).
    public var jsonObject: [String: Any] {
        [
            "version": 1,
            "instance": instance ?? NSNull(),
            "server": ["enabled": server.enabled, "port": server.port, "key": server.key ?? NSNull()] as [String: Any],
            "targets": targets.map { target -> [String: Any] in
                ["id": target.id, "name": target.name, "kind": target.kind.rawValue, "address": target.address,
                 "key": target.key ?? NSNull(), "enabled": target.enabled]
            },
        ]
    }

    public func data() -> Data {
        (try? JSONSerialization.data(withJSONObject: jsonObject, options: [.sortedKeys])) ?? Data()
    }

    private static func integer(_ value: Any?) -> Int? {
        guard let number = value as? NSNumber, CFGetTypeID(number) != CFBooleanGetTypeID(),
              !CFNumberIsFloatType(number), abs(number.int64Value) < 1 << 40 else { return nil }
        return number.intValue
    }

    private static func boolean(_ value: Any?) -> Bool? {
        guard let number = value as? NSNumber, CFGetTypeID(number) == CFBooleanGetTypeID() else { return nil }
        return number.boolValue
    }
}
