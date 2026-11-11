import AgentSpeedStatusLineKit
import Foundation
import Security

/// The install salt of QUOTAS 10.1: 32 random bytes in `<data>/quota/estimate-salt` (0600), created when first
/// needed with a temporary file and a rename. Never logged.
public enum QuotaEstimateSalt {
    public static let byteCount = 32

    public static func file(data: URL) -> URL {
        data.appendingPathComponent("quota/estimate-salt")
    }

    /// The stored salt, else a new one written to the file; nil when neither works.
    public static func loadOrCreate(data: URL) -> Data? {
        let url = file(data: data)
        if let stored = try? Data(contentsOf: url), stored.count == byteCount { return stored }
        var bytes = [UInt8](repeating: 0, count: byteCount)
        guard SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes) == errSecSuccess else { return nil }
        let salt = Data(bytes)
        return (try? QuotaEstimateFiles.writePrivate(salt, to: url)) == nil ? nil : salt
    }

    /// 64 lowercase hex digits, for the remote quota config (REMOTE 11.1).
    public static func hex(_ salt: Data) -> String {
        salt.map { String(format: "%02x", $0) }.joined()
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
