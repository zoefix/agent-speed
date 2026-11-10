import Foundation

/// Safari's `Cookies.binarycookies` (QUOTAS 9.10): `cook`, a big-endian page count and page sizes, then pages of
/// little-endian cookie records. Reading the file needs Full Disk Access; without it the store is skipped.
enum SafariBinaryCookies {
    static let macEpochOffset: Double = 978_307_200
    static let maxFileBytes = 16 << 20

    static func store(home: URL) -> URL {
        home.appendingPathComponent("Library/Containers/com.apple.Safari/Data/Library/Cookies/Cookies.binarycookies")
    }

    /// The rows of `site` and its subdomains; nil when the file is unreadable or malformed.
    static func read(_ file: URL, site: String) -> [BrowserCookie]? {
        guard let stat = FileStat.stat(file.path), stat.isRegularFile, stat.size <= Int64(maxFileBytes),
              let data = try? Data(contentsOf: file, options: .uncached) else { return nil }
        return parse([UInt8](data))?.filter { CookieHeader.matches($0.host, site: site) }
    }

    static func parse(_ bytes: [UInt8]) -> [BrowserCookie]? {
        guard bytes.count >= 8, bytes[0..<4].elementsEqual(Array("cook".utf8)) else { return nil }
        let pages = Int(be32(bytes, 4))
        guard pages <= 4096, bytes.count >= 8 + 4 * pages else { return nil }
        var offset = 8 + 4 * pages
        var cookies: [BrowserCookie] = []
        for index in 0..<pages {
            let size = Int(be32(bytes, 8 + 4 * index))
            guard size >= 8, offset + size <= bytes.count else { return nil }
            guard let page = parsePage(Array(bytes[offset..<(offset + size)])) else { return nil }
            cookies += page
            offset += size
        }
        return cookies
    }

    private static func parsePage(_ page: [UInt8]) -> [BrowserCookie]? {
        guard be32(page, 0) == 0x0000_0100 else { return nil }
        let count = Int(le32(page, 4))
        guard count <= 65_536, page.count >= 8 + 4 * count else { return nil }
        var cookies: [BrowserCookie] = []
        for index in 0..<count {
            let start = Int(le32(page, 8 + 4 * index))
            guard start + 56 <= page.count else { return nil }
            let size = Int(le32(page, start))
            guard size >= 56, start + size <= page.count else { return nil }
            let record = Array(page[start..<(start + size)])
            func text(_ at: Int) -> String? {
                let from = Int(le32(record, at))
                guard from >= 56, from < record.count, let end = record[from...].firstIndex(of: 0) else { return nil }
                return String(bytes: record[from..<end], encoding: .utf8)
            }
            guard let host = text(16), let name = text(20), let value = text(28) else { return nil }
            let expiry = double(record, 40), created = double(record, 48)
            cookies.append(BrowserCookie(host: host, name: name, value: value,
                                         expiresS: expiry.isFinite && expiry > 0 ? Int64(expiry + macEpochOffset) : 0,
                                         lastAccessMs: created.isFinite ? Int64((created + macEpochOffset) * 1000) : 0))
        }
        return cookies
    }

    static func be32(_ bytes: [UInt8], _ at: Int) -> UInt32 {
        guard at + 4 <= bytes.count else { return 0 }
        return bytes[at..<(at + 4)].reduce(0) { $0 << 8 | UInt32($1) }
    }

    static func le32(_ bytes: [UInt8], _ at: Int) -> UInt32 {
        guard at + 4 <= bytes.count else { return 0 }
        return bytes[at..<(at + 4)].reversed().reduce(0) { $0 << 8 | UInt32($1) }
    }

    static func double(_ bytes: [UInt8], _ at: Int) -> Double {
        guard at + 8 <= bytes.count else { return .nan }
        let bits = bytes[at..<(at + 8)].reversed().reduce(UInt64(0)) { $0 << 8 | UInt64($1) }
        return Double(bitPattern: bits)
    }

    /// `CFBundleShortVersionString` of Safari, for WorkBuddy's user agent.
    static var version: String? {
        let plist = URL(fileURLWithPath: "/Applications/Safari.app/Contents/Info.plist")
        guard let data = try? Data(contentsOf: plist),
              let info = try? PropertyListSerialization.propertyList(from: data, format: nil) as? [String: Any] else { return nil }
        return info["CFBundleShortVersionString"] as? String
    }
}
