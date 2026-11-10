import CommonCrypto
import Foundation
import Testing
@testable import AgentSpeedCore

/// Browser cookie stores built in a temporary home (QUOTAS 9.10): no real browser or keychain is touched.
@Suite(.serialized)
struct QuotaCookieStoreTests {
    /// A keychain stand-in that counts what was asked.
    final class Passwords: SafeStoragePasswords, @unchecked Sendable {
        var items: [String: String]
        private(set) var asked: [String] = []

        init(_ items: [String: String]) {
            self.items = items
        }

        func password(service: String, account: String) -> [UInt8]? {
            asked.append(service)
            return items[service].map { Array($0.utf8) }
        }
    }

    static func encrypt(_ plain: String, password: String, host: String? = nil) -> [UInt8] {
        let key = ChromeCookieCrypto.cbcKey(password: Array(password.utf8), iterations: 1003)!
        var body = Array(plain.utf8)
        if let host { body = Array(SHA256Digest.of(host)) + body }
        let iv = [UInt8](repeating: 0x20, count: 16)
        var out = [UInt8](repeating: 0, count: body.count + 16)
        var moved = 0
        _ = CCCrypt(CCOperation(kCCEncrypt), CCAlgorithm(kCCAlgorithmAES), CCOptions(kCCOptionPKCS7Padding), key, key.count,
                    iv, body, body.count, &out, out.count, &moved)
        return Array("v10".utf8) + out[0..<moved]
    }

    /// Microseconds since 1601 of a Unix time in seconds.
    static func chromeTime(_ seconds: Int64) -> Int64 { seconds * 1_000_000 + ChromeCookieStore.windowsEpochOffsetUs }

    static func chromeStore(at url: URL, version: Int, rows: [(String, String, String, [UInt8], Int64, Int64)]) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let db = try SQLiteConnection(path: url.path, mode: .readWriteCreate)
        defer { db.close() }
        try db.execute("CREATE TABLE meta(key LONGVARCHAR NOT NULL UNIQUE PRIMARY KEY, value LONGVARCHAR)")
        try db.execute("INSERT INTO meta VALUES('version', '\(version)')")
        try db.execute("""
            CREATE TABLE cookies(creation_utc INTEGER NOT NULL, host_key TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL,
            encrypted_value BLOB NOT NULL, expires_utc INTEGER NOT NULL, last_access_utc INTEGER NOT NULL)
            """)
        for row in rows {
            let insert = try db.prepare("INSERT INTO cookies VALUES(0, ?1, ?2, ?3, ?4, ?5, ?6)")
            insert.bind(1, row.0)
            insert.bind(2, row.1)
            insert.bind(3, row.2)
            row.3.withUnsafeBytes { insert.bind(4, blob: $0) }
            insert.bind(5, row.4)
            insert.bind(6, row.5)
            try insert.run()
        }
    }

    static func firefoxStore(at url: URL, rows: [(String, String, String, Int64, Int64)]) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let db = try SQLiteConnection(path: url.path, mode: .readWriteCreate)
        defer { db.close() }
        try db.execute("CREATE TABLE moz_cookies(id INTEGER PRIMARY KEY, host TEXT, name TEXT, value TEXT, expiry INTEGER, lastAccessed INTEGER)")
        for row in rows {
            let insert = try db.prepare("INSERT INTO moz_cookies(host, name, value, expiry, lastAccessed) VALUES(?1, ?2, ?3, ?4, ?5)")
            insert.bind(1, row.0)
            insert.bind(2, row.1)
            insert.bind(3, row.2)
            insert.bind(4, row.3)
            insert.bind(5, row.4)
            try insert.run()
        }
    }

    static let nowS: Int64 = 1_794_338_773

    @Test func chromeRowsOfTheSiteOnlyWithTheHostHash() throws {
        let home = try TempDirectory()
        let store = home.file("Library/Application Support/Google/Chrome/Default/Cookies")
        try Self.chromeStore(at: store, version: 24, rows: [
            (".qoder.com", "sid", "", Self.encrypt("s3cret", password: "pw", host: ".qoder.com"), 0, Self.chromeTime(Self.nowS - 10)),
            ("qoder.com", "plain", "p1", [], Self.chromeTime(Self.nowS + 600), Self.chromeTime(Self.nowS - 20)),
            ("old.qoder.com", "gone", "x", [], Self.chromeTime(Self.nowS - 60), Self.chromeTime(Self.nowS - 5)),
            ("evilqoder.com", "evil", "y", [], 0, Self.chromeTime(Self.nowS)),
        ])
        let (cookies, version) = try #require(ChromeCookieStore.read(store, site: "qoder.com"))
        #expect(version == 24)
        #expect(cookies.map(\.name) == ["sid", "plain", "gone"])
        let passwords = Passwords(["Chrome Safe Storage": "pw"])
        let reader = BrowserCookieReader(home: home.url, passwords: passwords)
        let session = try #require(reader.session(sites: ["qoder.com", "qoder.com.cn"], nowMs: Self.nowS * 1000))
        #expect(session.cookie == "sid=s3cret; plain=p1")
        #expect(session.site == "qoder.com")
        #expect(session.browser == .chrome)
        #expect(passwords.asked == ["Chrome Safe Storage"])
        // The browser's own database is never changed, and the private copy is gone.
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: FileManager.default.temporaryDirectory.path)
            .filter { $0.hasPrefix("AgentSpeed-cookies-") }
        #expect(leftovers.isEmpty)
    }

    @Test func aRefusedKeychainSkipsTheBrowserAndIsNotAskedAgain() throws {
        let home = try TempDirectory()
        try Self.chromeStore(at: home.file("Library/Application Support/Google/Chrome/Default/Cookies"), version: 23, rows: [
            ("www.workbuddy.cn", "wb", "", Self.encrypt("v", password: "pw"), 0, Self.chromeTime(Self.nowS)),
        ])
        let passwords = Passwords([:])
        let reader = BrowserCookieReader(home: home.url, passwords: passwords)
        #expect(reader.session(sites: ["workbuddy.cn"], nowMs: Self.nowS * 1000) == nil)
        #expect(reader.session(sites: ["workbuddy.cn"], nowMs: Self.nowS * 1000) == nil)
        #expect(passwords.asked == ["Chrome Safe Storage"])
        reader.resetRefusals()
        passwords.items["Chrome Safe Storage"] = "pw"
        #expect(reader.session(sites: ["workbuddy.cn"], nowMs: Self.nowS * 1000)?.cookie == "wb=v")
    }

    @Test func theMostRecentlyUsedProfileWinsAndFirefoxNeedsNoKeychain() throws {
        let home = try TempDirectory()
        try Self.chromeStore(at: home.file("Library/Application Support/Google/Chrome/Default/Cookies"), version: 23, rows: [
            ("qoder.com", "old", "", Self.encrypt("o", password: "pw"), 0, Self.chromeTime(Self.nowS - 3600)),
        ])
        try Self.chromeStore(at: home.file("Library/Application Support/Google/Chrome/Profile 2/Cookies"), version: 23, rows: [
            ("qoder.com", "mid", "m", [], 0, Self.chromeTime(Self.nowS - 1800)),
        ])
        let firefox = home.file("Library/Application Support/Firefox")
        try home.write("Library/Application Support/Firefox/profiles.ini", "[General]\nStartWithLastProfile=1\n\n[Profile0]\nName=default\nIsRelative=1\nPath=Profiles/abc.default\n")
        try Self.firefoxStore(at: firefox.appendingPathComponent("Profiles/abc.default/cookies.sqlite"), rows: [
            (".qoder.com.cn", "cn", "c1", Self.nowS + 99, (Self.nowS - 5) * 1_000_000),
        ])
        try home.write("Library/Application Support/Firefox/Profiles/abc.default/compatibility.ini", "[Compatibility]\nLastVersion=144.0_20251020/20251020\n")
        let passwords = Passwords(["Chrome Safe Storage": "pw"])
        let reader = BrowserCookieReader(home: home.url, passwords: passwords)
        let session = try #require(reader.session(sites: ["qoder.com", "qoder.com.cn"], nowMs: Self.nowS * 1000))
        #expect(session.browser == .firefox)
        #expect(session.site == "qoder.com.cn")
        #expect(session.cookie == "cn=c1")
        #expect(session.userAgent == "Mozilla/5.0 (Macintosh; Intel Mac OS X 10.15; rv:144.0) Gecko/20100101 Firefox/144.0")
        #expect(passwords.asked.isEmpty)
        #expect(FirefoxCookieStore.parseProfiles("[Profile1]\nPath=/abs/p\nIsRelative=0\n", root: firefox).map(\.path) == ["/abs/p"])
    }

    @Test func safariBinaryCookies() throws {
        func le32(_ value: Int) -> [UInt8] { withUnsafeBytes(of: UInt32(value).littleEndian, Array.init) }
        func be32(_ value: Int) -> [UInt8] { withUnsafeBytes(of: UInt32(value).bigEndian, Array.init) }
        func record(host: String, name: String, value: String, expiresS: Double) -> [UInt8] {
            let strings = [host, name, "/", value].map { Array($0.utf8) + [0] }
            var offsets: [Int] = []
            var cursor = 56
            for string in strings {
                offsets.append(cursor)
                cursor += string.count
            }
            var bytes = le32(cursor) + le32(0) + le32(0) + le32(0) + le32(offsets[0]) + le32(offsets[1]) + le32(offsets[2])
                + le32(offsets[3]) + [UInt8](repeating: 0, count: 8)
            bytes += withUnsafeBytes(of: (expiresS - SafariBinaryCookies.macEpochOffset).bitPattern.littleEndian, Array.init)
            bytes += withUnsafeBytes(of: (Double(Self.nowS - 100) - SafariBinaryCookies.macEpochOffset).bitPattern.littleEndian, Array.init)
            return bytes + strings.flatMap { $0 }
        }
        let records = [record(host: ".workbuddy.cn", name: "s", value: "safari", expiresS: Double(Self.nowS + 3600)),
                       record(host: "other.com", name: "o", value: "x", expiresS: Double(Self.nowS + 3600))]
        var page = be32(0x100) + le32(records.count)
        var offset = 8 + 4 * records.count + 4
        for item in records {
            page += le32(offset)
            offset += item.count
        }
        page += le32(0) + records.flatMap { $0 }
        let file = Array("cook".utf8) + be32(1) + be32(page.count) + page + [UInt8](repeating: 0, count: 8)
        let cookies = try #require(SafariBinaryCookies.parse(file))
        #expect(cookies.map(\.name) == ["s", "o"])
        #expect(cookies[0].expiresS == Self.nowS + 3600)
        #expect(CookieHeader.join(cookies, site: "workbuddy.cn", nowS: Self.nowS) == "s=safari")
        #expect(SafariBinaryCookies.parse(Array(file.prefix(20))) == nil)
        #expect(SafariBinaryCookies.parse(Array("nope".utf8) + file.dropFirst(4)) == nil)
    }
}

/// SHA-256 bytes for test vectors.
enum SHA256Digest {
    static func of(_ text: String) -> [UInt8] {
        var digest = [UInt8](repeating: 0, count: Int(CC_SHA256_DIGEST_LENGTH))
        let bytes = Array(text.utf8)
        _ = CC_SHA256(bytes, CC_LONG(bytes.count), &digest)
        return digest
    }
}
