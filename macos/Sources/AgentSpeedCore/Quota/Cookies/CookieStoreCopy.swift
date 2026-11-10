import Foundation

/// A browser's cookie database copied with its `-wal` (and `-shm`) into a private temporary folder, read there and
/// removed right after (QUOTAS 9.10): the browser's own files are never opened by SQLite.
enum CookieStoreCopy {
    /// Runs `body` with the copy's path; nil when the store cannot be copied.
    static func read<T>(_ store: URL, _ body: (String) throws -> T) -> T? {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("AgentSpeed-cookies-\(UUID().uuidString)", isDirectory: true)
        guard (try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: false,
                                                        attributes: [.posixPermissions: 0o700])) != nil else { return nil }
        defer { try? FileManager.default.removeItem(at: folder) }
        let copy = folder.appendingPathComponent("store.sqlite")
        do {
            try FileManager.default.copyItem(at: store, to: copy)
            for suffix in ["-wal", "-shm"] {
                let side = URL(fileURLWithPath: store.path + suffix)
                if FileStat.stat(side.path)?.isRegularFile == true {
                    try FileManager.default.copyItem(at: side, to: URL(fileURLWithPath: copy.path + suffix))
                }
            }
            return try body(copy.path)
        } catch {
            return nil
        }
    }

    /// The `WHERE` arguments of a site's rows: the site, `.site`, and any subdomain.
    static func hostArguments(_ site: String) -> (String, String, String) {
        let escaped = site.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "%", with: "\\%")
            .replacingOccurrences(of: "_", with: "\\_")
        return (site, "." + site, "%." + escaped)
    }
}

/// A Chromium `Cookies` database: the site's rows with their `meta` version.
enum ChromeCookieStore {
    /// Microseconds from 1601-01-01 to 1970-01-01.
    static let windowsEpochOffsetUs: Int64 = 11_644_473_600_000_000

    static func read(_ store: URL, site: String) -> (cookies: [BrowserCookie], metaVersion: Int)? {
        CookieStoreCopy.read(store) { path in
            let db = try SQLiteConnection(path: path, mode: .readWriteCreate)
            defer { db.close() }
            var version = 0
            let meta = try db.prepare("SELECT value FROM meta WHERE key = 'version'")
            if try meta.step() { version = Int(meta.string(0) ?? "") ?? Int(meta.int64(0)) }
            let (exact, dotted, pattern) = CookieStoreCopy.hostArguments(site)
            let rows = try db.prepare("""
                SELECT host_key, name, value, encrypted_value, expires_utc, last_access_utc FROM cookies
                WHERE host_key = ?1 OR host_key = ?2 OR host_key LIKE ?3 ESCAPE '\\'
                """)
            rows.bind(1, exact)
            rows.bind(2, dotted)
            rows.bind(3, pattern)
            var cookies: [BrowserCookie] = []
            try rows.forEachRow { row in
                let encrypted = row.withBlob(3) { [UInt8]($0) }
                cookies.append(BrowserCookie(host: row.string(0) ?? "", name: row.string(1) ?? "", value: row.string(2) ?? "",
                                             encrypted: encrypted, expiresS: seconds(row.int64(4)),
                                             lastAccessMs: milliseconds(row.int64(5))))
                if cookies.count >= 256 { throw SQLiteError(code: 0, message: "too many rows") }
            }
            return (cookies, version)
        }
    }

    /// `expires_utc` (µs since 1601, 0 for a session cookie) in Unix seconds.
    static func seconds(_ chromeUs: Int64) -> Int64 {
        chromeUs <= 0 ? 0 : max(1, (chromeUs - windowsEpochOffsetUs) / 1_000_000)
    }

    static func milliseconds(_ chromeUs: Int64) -> Int64 {
        chromeUs <= 0 ? 0 : (chromeUs - windowsEpochOffsetUs) / 1000
    }
}

/// Firefox's `cookies.sqlite` (`moz_cookies`): plain values.
enum FirefoxCookieStore {
    static func profiles(home: URL) -> [URL] {
        let root = home.appendingPathComponent("Library/Application Support/Firefox")
        let ini = root.appendingPathComponent("profiles.ini")
        guard FileStat.stat(ini.path).map({ $0.size <= 1 << 20 }) == true,
              let text = try? String(contentsOf: ini, encoding: .utf8) else { return [] }
        return parseProfiles(text, root: root)
    }

    /// Every `[Profile…]` section's `Path`, relative to the Firefox folder unless `IsRelative=0`.
    static func parseProfiles(_ text: String, root: URL) -> [URL] {
        var folders: [URL] = []
        var inProfile = false, path: String?, relative = true
        func flush() {
            if inProfile, let path, !path.isEmpty {
                folders.append(relative ? root.appendingPathComponent(path) : URL(fileURLWithPath: path))
            }
            path = nil
            relative = true
        }
        for raw in text.split(whereSeparator: \.isNewline) {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("[") {
                flush()
                inProfile = line.hasPrefix("[Profile")
            } else if let equals = line.firstIndex(of: "=") {
                let key = line[..<equals], value = String(line[line.index(after: equals)...])
                if key == "Path" { path = value }
                if key == "IsRelative" { relative = value != "0" }
            }
        }
        flush()
        return folders
    }

    static func read(_ store: URL, site: String) -> [BrowserCookie]? {
        CookieStoreCopy.read(store) { path in
            let db = try SQLiteConnection(path: path, mode: .readWriteCreate)
            defer { db.close() }
            let (exact, dotted, pattern) = CookieStoreCopy.hostArguments(site)
            let rows = try db.prepare("""
                SELECT host, name, value, expiry, lastAccessed FROM moz_cookies
                WHERE host = ?1 OR host = ?2 OR host LIKE ?3 ESCAPE '\\'
                """)
            rows.bind(1, exact)
            rows.bind(2, dotted)
            rows.bind(3, pattern)
            var cookies: [BrowserCookie] = []
            try rows.forEachRow { row in
                let expiry = row.int64(3)
                cookies.append(BrowserCookie(host: row.string(0) ?? "", name: row.string(1) ?? "", value: row.string(2) ?? "",
                                             expiresS: expiry > 100_000_000_000 ? expiry / 1000 : expiry,
                                             lastAccessMs: row.int64(4) / 1000))
                if cookies.count >= 256 { throw SQLiteError(code: 0, message: "too many rows") }
            }
            return cookies
        }
    }

    /// `LastVersion=144.0_20251020/20251020` of the profile's `compatibility.ini` → `144.0`.
    static func version(profile: URL) -> String? {
        let file = profile.appendingPathComponent("compatibility.ini")
        guard FileStat.stat(file.path).map({ $0.size <= 64 << 10 }) == true,
              let text = try? String(contentsOf: file, encoding: .utf8) else { return nil }
        for line in text.split(whereSeparator: \.isNewline) where line.hasPrefix("LastVersion=") {
            let value = line.dropFirst("LastVersion=".count).split(separator: "_").first.map(String.init)
            if let value, !value.isEmpty, value.allSatisfy({ $0.isNumber || $0 == "." }) { return value }
        }
        return nil
    }
}
