import AgentSpeedStatusLineKit
import Foundation
import Security

/// The install id of QUOTAS 10.1: 32 random bytes in `<data>/quota/estimate-salt` (the file keeps the name it had when
/// it held the account salt; 0600), created when first needed with a temporary file and a rename, used in hex. It
/// keys only the accounts without an id, never leaves the computer and is never logged.
public enum QuotaInstallId {
    public static let byteCount = 32

    public static func file(data: URL) -> URL {
        data.appendingPathComponent("quota/estimate-salt")
    }

    /// The stored id, else a new one written to the file; nil when neither works.
    public static func loadOrCreate(data: URL) -> String? {
        let url = file(data: data)
        if let stored = try? Data(contentsOf: url), stored.count == byteCount { return QuotaAccounts.hex(stored) }
        guard let bytes = randomBytes() else { return nil }
        return (try? QuotaEstimateFiles.writePrivate(bytes, to: url)) == nil ? nil : QuotaAccounts.hex(bytes)
    }

    /// An id that lives in memory only.
    static func ephemeral() -> String {
        randomBytes().map { QuotaAccounts.hex($0) } ?? UUID().uuidString.lowercased()
    }

    private static func randomBytes() -> Data? {
        var bytes = [UInt8](repeating: 0, count: byteCount)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { return nil }
        return Data(bytes)
    }
}

enum QuotaEstimateFiles {
    /// Writes with a temporary file (0600) and a rename, in a folder only the user can enter.
    static func writePrivate(_ data: Data, to url: URL) throws {
        let folder = url.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                attributes: [.posixPermissions: 0o700])
        guard StatusLineHelper.writePrivate(data, to: url) else { throw CocoaError(.fileWriteUnknown) }
    }
}
