import Foundation
import Security

/// The password of a Chromium browser's keychain item `<Browser> Safe Storage` (QUOTAS 9.10). Reading it makes macOS
/// ask the user once per browser; a refusal leaves that browser out.
public protocol SafeStoragePasswords: Sendable {
    /// The password bytes, or nil when there is no item or the user refused.
    func password(service: String, account: String) -> [UInt8]?
}

/// The login keychain. Only used after the user allowed reading browser sign-ins; nothing is cached.
public struct KeychainSafeStoragePasswords: SafeStoragePasswords {
    public init() {}

    public func password(service: String, account: String) -> [UInt8]? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecMatchLimit as String: kSecMatchLimitOne,
            kSecReturnData as String: true,
        ]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess, let data = result as? Data else {
            return nil
        }
        return [UInt8](data)
    }
}
