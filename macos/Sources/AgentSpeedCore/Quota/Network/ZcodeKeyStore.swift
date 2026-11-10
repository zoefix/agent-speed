import Foundation
import Security

/// Where the ZCode key lives (QUOTAS 9.8): the platform's secret store only, never the settings file.
public protocol ZcodeKeyStoring: Sendable {
    /// Whether a key is saved, without reading it.
    func hasKey() -> Bool
    /// The key, read right before its request.
    func read() -> String?
    @discardableResult func save(_ key: String) -> Bool
    func remove()
}

/// The login keychain: a generic password with service `AgentSpeed` and account `zcode-api-key`.
public struct KeychainZcodeKeyStore: ZcodeKeyStoring {
    public static let service = "AgentSpeed"
    public static let account = "zcode-api-key"

    public init() {}

    private var base: [String: Any] {
        [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: Self.service,
         kSecAttrAccount as String: Self.account]
    }

    public func hasKey() -> Bool {
        var query = base
        query[kSecReturnAttributes as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        return SecItemCopyMatching(query as CFDictionary, nil) == errSecSuccess
    }

    public func read() -> String? {
        var query = base
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    public func save(_ key: String) -> Bool {
        let data = Data(key.utf8)
        let update = SecItemUpdate(base as CFDictionary, [kSecValueData as String: data] as CFDictionary)
        if update == errSecSuccess { return true }
        guard update == errSecItemNotFound else { return false }
        var item = base
        item[kSecValueData as String] = data
        item[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
        item[kSecAttrLabel as String] = "Agent Speed – ZCode API key"
        return SecItemAdd(item as CFDictionary, nil) == errSecSuccess
    }

    public func remove() {
        SecItemDelete(base as CFDictionary)
    }
}

/// A key kept in memory (previews and tests).
public final class MemoryZcodeKeyStore: ZcodeKeyStoring, @unchecked Sendable {
    private let lock = NSLock()
    private var key: String?

    public init(_ key: String? = nil) {
        self.key = key
    }

    public func hasKey() -> Bool { lock.withLock { key != nil } }
    public func read() -> String? { lock.withLock { key } }
    public func save(_ key: String) -> Bool {
        lock.withLock { self.key = key }
        return true
    }
    public func remove() { lock.withLock { key = nil } }
}

/// A pasted key as saved: surrounding spaces and line breaks dropped, nothing else changed; nil when empty or when
/// it could not go into a header.
public enum ZcodeKey {
    public static func cleaned(_ text: String) -> String? {
        let key = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !key.isEmpty, key.utf8.count <= 4096, key.utf8.allSatisfy({ (0x21...0x7E).contains($0) }) else { return nil }
        return key
    }
}
