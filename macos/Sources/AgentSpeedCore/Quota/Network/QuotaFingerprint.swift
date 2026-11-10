import CryptoKit
import Foundation

/// The first 8 bytes of the SHA-256 of a credential (QUOTAS 9.1): a snapshot is keyed by it, so signing in to another
/// account drops the old account's numbers, and a refused credential is not sent again until it changes. The
/// credential itself is never kept.
public struct QuotaFingerprint: Hashable, Sendable {
    public let value: UInt64

    public init(_ credential: String) {
        self.init(bytes: Array(credential.utf8))
    }

    public init(bytes: [UInt8]) {
        let digest = SHA256.hash(data: bytes)
        value = digest.prefix(8).reduce(0) { $0 << 8 | UInt64($1) }
    }
}

/// A token read from an agent's sign-in file, for one request only.
struct QuotaToken: Sendable {
    var value: String
    /// Nil when the file names no expiry.
    var expiresAtMs: Int64?

    var fingerprint: QuotaFingerprint { QuotaFingerprint(value) }

    func isExpired(nowMs: Int64, marginMs: Int64 = 0) -> Bool {
        expiresAtMs.map { nowMs >= $0 - marginMs } ?? false
    }
}

/// The sign-ins of QUOTAS 9.2, 9.5–9.7. Each file is read right before a request, scanned in memory for the named
/// members only (a refresh token is skipped unread), and nothing of it is kept, written or logged.
enum QuotaCredentials {
    static let maxFileBytes = 1 << 20

    static func file(_ url: URL) -> Data? {
        guard let stat = FileStat.stat(url.path), stat.isRegularFile, stat.size <= Int64(maxFileBytes) else { return nil }
        return try? Data(contentsOf: url, options: .uncached)
    }

    static func folder(_ variable: String, _ environment: [String: String]) -> URL? {
        guard let value = environment[variable], !value.isEmpty else { return nil }
        return URL(fileURLWithPath: (value as NSString).expandingTildeInPath)
    }

    /// `$KIMI_CODE_HOME/credentials/kimi-code.json`, else `~/.kimi-code/…`, else the legacy `~/.kimi/…`.
    static func kimiFile(home: URL, environment: [String: String]) -> URL? {
        let candidates = [folder("KIMI_CODE_HOME", environment), home.appendingPathComponent(".kimi-code"),
                          home.appendingPathComponent(".kimi")]
            .compactMap { $0?.appendingPathComponent("credentials/kimi-code.json") }
        return candidates.first { FileStat.stat($0.path)?.isRegularFile == true }
    }

    /// `access_token` with `expires_at` in seconds (above 1e11, milliseconds).
    static func kimi(home: URL, environment: [String: String]) -> QuotaToken? {
        guard let url = kimiFile(home: home, environment: environment), let data = file(url) else { return nil }
        return token(data, value: ["access_token"], expiry: "expires_at") { VendorValue.epochMs($0) }
    }

    /// `<home>/.gemini/oauth_creds.json` (`$GEMINI_CLI_HOME`, else `~`): `access_token` with `expiry_date` in ms.
    static func gemini(home: URL, environment: [String: String]) -> QuotaToken? {
        let root = folder("GEMINI_CLI_HOME", environment) ?? home
        guard let data = file(root.appendingPathComponent(".gemini/oauth_creds.json")) else { return nil }
        return token(data, value: ["access_token"], expiry: "expiry_date") { VendorValue.truncated($0) }
    }

    /// The first entry of `$GROK_HOME/auth.json` (else `~/.grok/auth.json`): its `key`, else `access_token`, with
    /// `expires_at` (ISO 8601).
    static func grok(home: URL, environment: [String: String]) -> QuotaToken? {
        let root = folder("GROK_HOME", environment) ?? home.appendingPathComponent(".grok")
        guard let data = file(root.appendingPathComponent("auth.json")) else { return nil }
        return data.withUnsafeBytes { bytes -> QuotaToken? in
            var scanner = JSONScanner(bytes)
            do {
                try scanner.beginObject()
                guard try scanner.nextMember() != nil, try scanner.beginObjectIfPresent() else { return nil }
                var key: String?, access: String?, expires: Int64?
                while let member = try scanner.nextMember() {
                    if scanner.key(member, is: "key") {
                        key = try scanner.readNonEmptyString()
                    } else if scanner.key(member, is: "access_token") {
                        access = try scanner.readNonEmptyString()
                    } else if scanner.key(member, is: "expires_at") {
                        expires = try scanner.readString().flatMap(VendorValue.isoMs)
                    } else {
                        try scanner.skipValue()
                    }
                }
                return (key ?? access).map { QuotaToken(value: $0, expiresAtMs: expires) }
            } catch {
                return nil
            }
        }
    }

    /// `FACTORY_API_KEY` from the app's environment, else its line in `~/.factory/.env`. `settings.json` is never read.
    static func droid(home: URL, environment: [String: String]) -> String? {
        if let key = environment["FACTORY_API_KEY"]?.trimmingCharacters(in: .whitespaces), !key.isEmpty { return key }
        guard let data = file(home.appendingPathComponent(".factory/.env")) else { return nil }
        return DroidLimits.key(fromEnvFile: String(decoding: data, as: UTF8.self))
    }

    /// A top-level string member and a numeric expiry, everything else skipped.
    private static func token(_ data: Data, value names: [StaticString], expiry: StaticString,
                              toMs: (Double) -> Int64) -> QuotaToken? {
        data.withUnsafeBytes { bytes -> QuotaToken? in
            var scanner = JSONScanner(bytes)
            var value: String?, expires: Int64?
            do {
                try scanner.beginObject()
                while let member = try scanner.nextMember() {
                    if value == nil, names.contains(where: { scanner.key(member, is: $0) }) {
                        value = try scanner.readNonEmptyString()
                    } else if scanner.key(member, is: expiry) {
                        expires = try scanner.readDouble().map(toMs)
                    } else {
                        try scanner.skipValue()
                    }
                }
            } catch {
                return nil
            }
            return value.map { QuotaToken(value: $0, expiresAtMs: expires) }
        }
    }
}
