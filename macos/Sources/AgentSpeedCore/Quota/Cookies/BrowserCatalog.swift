import Foundation

/// The browsers whose cookies the session sources read (QUOTAS 9.10), in the order they are tried.
public enum BrowserKind: String, CaseIterable, Sendable {
    case chrome, edge, brave, arc, vivaldi, chromium, firefox, safari

    /// The name the consent dialog and logs use.
    public var displayName: String {
        switch self {
        case .chrome: "Chrome"
        case .edge: "Microsoft Edge"
        case .brave: "Brave"
        case .arc: "Arc"
        case .vivaldi: "Vivaldi"
        case .chromium: "Chromium"
        case .firefox: "Firefox"
        case .safari: "Safari"
        }
    }

    var isChromium: Bool { self != .firefox && self != .safari }
}

/// A Chromium-family browser on macOS: its user data folder and its keychain item `<Browser> Safe Storage`.
struct ChromiumBrowser: Sendable {
    var kind: BrowserKind
    var userData: URL
    var safeStorageService: String
    var safeStorageAccount: String

    static func all(home: URL) -> [ChromiumBrowser] {
        let support = home.appendingPathComponent("Library/Application Support")
        let rows: [(BrowserKind, String, String, String)] = [
            (.chrome, "Google/Chrome", "Chrome Safe Storage", "Chrome"),
            (.edge, "Microsoft Edge", "Microsoft Edge Safe Storage", "Microsoft Edge"),
            (.brave, "BraveSoftware/Brave-Browser", "Brave Safe Storage", "Brave"),
            (.arc, "Arc/User Data", "Arc Safe Storage", "Arc"),
            (.vivaldi, "Vivaldi", "Vivaldi Safe Storage", "Vivaldi"),
            (.chromium, "Chromium", "Chromium Safe Storage", "Chromium"),
        ]
        return rows.map { ChromiumBrowser(kind: $0.0, userData: support.appendingPathComponent($0.1),
                                          safeStorageService: $0.2, safeStorageAccount: $0.3) }
    }

    /// `Default`, then `Profile 1`, `Profile 2` … in number order; each with its `Cookies` (or `Network/Cookies`).
    func cookieStores() -> [URL] {
        let names = (try? FileManager.default.contentsOfDirectory(atPath: userData.path)) ?? []
        let profiles = names.filter { $0.hasPrefix("Profile ") }.sorted { lhs, rhs in
            let (a, b) = (Int(lhs.dropFirst(8)) ?? .max, Int(rhs.dropFirst(8)) ?? .max)
            return a != b ? a < b : lhs < rhs
        }
        return (["Default"] + profiles).compactMap { profile in
            let folder = userData.appendingPathComponent(profile)
            return [folder.appendingPathComponent("Cookies"), folder.appendingPathComponent("Network/Cookies")]
                .first { FileStat.stat($0.path)?.isRegularFile == true }
        }
    }

    /// The major version from `Last Version` (`142.0.7444.60` → 142), for WorkBuddy's user agent.
    var majorVersion: Int? {
        let file = userData.appendingPathComponent("Last Version")
        guard FileStat.stat(file.path).map({ $0.size <= 64 }) == true, let text = try? String(contentsOf: file, encoding: .utf8)
        else { return nil }
        return Int(text.trimmingCharacters(in: .whitespacesAndNewlines).split(separator: ".").first ?? "")
    }
}

/// The user agent the browser the cookies came from sends (QUOTAS 9.9, WorkBuddy).
enum BrowserUserAgent {
    static let macToken = "Macintosh; Intel Mac OS X 10_15_7"
    static let firefoxMacToken = "Macintosh; Intel Mac OS X 10.15"

    static func chrome(platform: String = macToken, major: Int) -> String {
        "Mozilla/5.0 (\(platform)) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/\(major).0.0.0 Safari/537.36"
    }

    static func firefox(platform: String = firefoxMacToken, version: String) -> String {
        "Mozilla/5.0 (\(platform); rv:\(version)) Gecko/20100101 Firefox/\(version)"
    }

    static func safari(version: String) -> String {
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/\(version) Safari/605.1.15"
    }
}
