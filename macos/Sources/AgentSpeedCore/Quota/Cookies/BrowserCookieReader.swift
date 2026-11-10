import CryptoKit
import Foundation
import os

/// A site's session as one browser profile holds it: the `Cookie` header and that browser's user agent (QUOTAS 9.9).
public struct BrowserSession: Sendable {
    public var site: String
    public var cookie: String
    public var userAgent: String
    public var browser: BrowserKind

    public init(site: String, cookie: String, userAgent: String, browser: BrowserKind) {
        self.site = site
        self.cookie = cookie
        self.userAgent = userAgent
        self.browser = browser
    }

    /// The first 8 bytes of the SHA-256 of the header (QUOTAS 9.1): a new sign-in is a new account.
    public var fingerprint: QuotaFingerprint { QuotaFingerprint(cookie) }
}

/// Where a site's cookies live on this Mac (QUOTAS 9.10): Chrome, Edge, Brave, Arc, Vivaldi, Chromium (each profile),
/// Firefox (each profile of `profiles.ini`), then Safari. Only the site's rows are read, from private copies; among the
/// profiles with an unexpired cookie, the most recently used one wins, and only its browser's keychain item is asked
/// for. A browser whose item the user refused is not asked again while the app runs.
public final class BrowserCookieReader: @unchecked Sendable {
    struct Candidate {
        var browser: BrowserKind
        var site: String
        var cookies: [BrowserCookie]
        var metaVersion = 0
        var chromium: ChromiumBrowser?
        var firefoxProfile: URL?
        var newestAccessMs: Int64
    }

    private let home: URL
    private let passwords: any SafeStoragePasswords
    private let lock = NSLock()
    private var refused: Set<BrowserKind> = []

    public init(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                passwords: any SafeStoragePasswords = KeychainSafeStoragePasswords()) {
        self.home = home
        self.passwords = passwords
    }

    /// The session for the first of `sites` any profile has; nil when no browser has one.
    public func session(sites: [String], nowMs: Int64) -> BrowserSession? {
        lock.lock()
        defer { lock.unlock() }
        let nowS = nowMs / 1000
        var candidates = self.candidates(sites: sites, nowS: nowS)
        // The most recently used profile first; equal times keep the browser order.
        candidates = candidates.enumerated().sorted { lhs, rhs in
            lhs.element.newestAccessMs != rhs.element.newestAccessMs
                ? lhs.element.newestAccessMs > rhs.element.newestAccessMs : lhs.offset < rhs.offset
        }.map(\.element)
        for candidate in candidates {
            guard let cookies = readable(candidate),
                  let header = CookieHeader.join(cookies, site: candidate.site, nowS: nowS) else { continue }
            return BrowserSession(site: candidate.site, cookie: header, userAgent: userAgent(candidate),
                                  browser: candidate.browser)
        }
        return nil
    }

    /// Every profile's unexpired rows of the sites, without decrypting anything.
    func candidates(sites: [String], nowS: Int64) -> [Candidate] {
        var found: [Candidate] = []
        func add(_ base: Candidate) {
            let usable = CookieHeader.usable(base.cookies, site: base.site, nowS: nowS)
            guard !usable.isEmpty else { return }
            var candidate = base
            candidate.cookies = usable
            candidate.newestAccessMs = usable.map(\.lastAccessMs).max() ?? 0
            found.append(candidate)
        }
        for browser in ChromiumBrowser.all(home: home) where !refused.contains(browser.kind) {
            for store in browser.cookieStores() {
                for site in sites {
                    guard let (cookies, version) = ChromeCookieStore.read(store, site: site) else { continue }
                    add(Candidate(browser: browser.kind, site: site, cookies: cookies, metaVersion: version,
                                  chromium: browser, newestAccessMs: 0))
                }
            }
        }
        for profile in FirefoxCookieStore.profiles(home: home) {
            let store = profile.appendingPathComponent("cookies.sqlite")
            guard FileStat.stat(store.path)?.isRegularFile == true else { continue }
            for site in sites {
                guard let cookies = FirefoxCookieStore.read(store, site: site) else { continue }
                add(Candidate(browser: .firefox, site: site, cookies: cookies, firefoxProfile: profile, newestAccessMs: 0))
            }
        }
        let safari = SafariBinaryCookies.store(home: home)
        if FileStat.stat(safari.path)?.isRegularFile == true {
            for site in sites {
                guard let cookies = SafariBinaryCookies.read(safari, site: site) else { continue }
                add(Candidate(browser: .safari, site: site, cookies: cookies, newestAccessMs: 0))
            }
        }
        return found
    }

    /// The candidate's rows with every value in plain text; encrypted rows need the browser's keychain item.
    private func readable(_ candidate: Candidate) -> [BrowserCookie]? {
        guard candidate.cookies.contains(where: \.needsDecryption) else { return candidate.cookies }
        guard let browser = candidate.chromium else { return nil }
        guard var password = passwords.password(service: browser.safeStorageService, account: browser.safeStorageAccount),
              var key = ChromeCookieCrypto.cbcKey(password: password, iterations: ChromeCookieCrypto.macIterations) else {
            refused.insert(browser.kind)
            return nil
        }
        defer {
            password.withUnsafeMutableBufferPointer { $0.initialize(repeating: 0) }
            key.withUnsafeMutableBufferPointer { $0.initialize(repeating: 0) }
        }
        let cookies = candidate.cookies.compactMap { cookie -> BrowserCookie? in
            guard cookie.needsDecryption else { return cookie }
            guard let value = ChromeCookieCrypto.decryptCBC(cookie.encrypted, key: key, metaVersion: candidate.metaVersion)
            else { return nil }
            var plain = cookie
            plain.value = value
            plain.encrypted = []
            return plain
        }
        return cookies.isEmpty ? nil : cookies
    }

    private func userAgent(_ candidate: Candidate) -> String {
        switch candidate.browser {
        case .firefox:
            let version = candidate.firefoxProfile.flatMap(FirefoxCookieStore.version) ?? "144.0"
            return BrowserUserAgent.firefox(version: version)
        case .safari:
            return BrowserUserAgent.safari(version: SafariBinaryCookies.version ?? "18.0")
        default:
            return BrowserUserAgent.chrome(major: candidate.chromium?.majorVersion ?? 142)
        }
    }

    /// Forgets refusals (the user turned the switch off and on again).
    public func resetRefusals() {
        lock.lock()
        refused.removeAll()
        lock.unlock()
    }
}
