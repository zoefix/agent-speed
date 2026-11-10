import Foundation

/// One cookie row of a browser store, with the facts the choice needs (QUOTAS 9.10). `value` is plain text; a
/// Chromium row may carry `encrypted` instead.
struct BrowserCookie: Sendable {
    var host: String
    var name: String
    var value: String
    var encrypted: [UInt8] = []
    /// Unix seconds; 0 for a session cookie.
    var expiresS: Int64
    /// Unix milliseconds; 0 when the store keeps none.
    var lastAccessMs: Int64 = 0

    var needsDecryption: Bool { value.isEmpty && !encrypted.isEmpty }
}

/// The `Cookie` header of one site: every row whose host is the site or one of its subdomains and that has not expired,
/// joined in database order.
enum CookieHeader {
    static func matches(_ host: String, site: String) -> Bool {
        let bare = host.hasPrefix(".") ? String(host.dropFirst()) : host
        return bare == site || bare.hasSuffix("." + site) || host == "." + site
    }

    static func isLive(_ cookie: BrowserCookie, nowS: Int64) -> Bool {
        cookie.expiresS == 0 || cookie.expiresS > nowS
    }

    /// The rows a header would use.
    static func usable(_ cookies: [BrowserCookie], site: String, nowS: Int64) -> [BrowserCookie] {
        cookies.filter { matches($0.host, site: site) && isLive($0, nowS: nowS) }
    }

    /// A name or value that could break the header (a control character, `;` in a name) is never sent.
    static func isSendable(_ cookie: BrowserCookie) -> Bool {
        let unsafe: (Unicode.Scalar) -> Bool = { $0.value < 0x20 || $0.value == 0x7F }
        return !cookie.name.isEmpty && !cookie.name.unicodeScalars.contains { unsafe($0) || $0 == ";" || $0 == "=" }
            && !cookie.value.unicodeScalars.contains(where: unsafe)
    }

    /// Nil when no row is left.
    static func join(_ cookies: [BrowserCookie], site: String, nowS: Int64) -> String? {
        let parts = usable(cookies, site: site, nowS: nowS).filter(isSendable).map { "\($0.name)=\($0.value)" }
        return parts.isEmpty ? nil : parts.joined(separator: "; ")
    }
}
