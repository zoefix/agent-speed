import Foundation

/// Reads a browser session for a site (QUOTAS 9.10); `BrowserCookieReader` on macOS, a fixture in tests.
public protocol BrowserSessionReading: Sendable {
    func session(sites: [String], nowMs: Int64) -> BrowserSession?
    func resetRefusals()
}

extension BrowserCookieReader: BrowserSessionReading {}

/// Everything the vendor readers use from the outside world, replaceable in tests.
public struct QuotaVendorEnvironment: Sendable {
    public var home: URL
    /// The app's environment (`KIMI_CODE_HOME`, `GEMINI_CLI_HOME`, `GROK_HOME`, `FACTORY_API_KEY`).
    public var variables: [String: String]
    public var transport: any QuotaHTTPTransport
    /// `AgentSpeed/<version> (macOS)`.
    public var userAgent: String
    public var timeZone: @Sendable () -> TimeZone
    /// The ZCode key from the secret store, read right before its request.
    public var zcodeKey: @Sendable () -> String?
    public var browser: any BrowserSessionReading
    public var antigravity: any AntigravityLocating
    public var now: @Sendable () -> Int64

    public init(home: URL = FileManager.default.homeDirectoryForCurrentUser,
                variables: [String: String] = ProcessInfo.processInfo.environment,
                transport: any QuotaHTTPTransport = QuotaURLSessionTransport(),
                userAgent: String,
                timeZone: @escaping @Sendable () -> TimeZone = { TimeZone.current },
                zcodeKey: @escaping @Sendable () -> String?,
                browser: (any BrowserSessionReading)? = nil,
                antigravity: (any AntigravityLocating)? = nil,
                now: @escaping @Sendable () -> Int64 = { Int64((Date().timeIntervalSince1970 * 1000).rounded(.down)) }) {
        self.home = home
        self.variables = variables
        self.transport = transport
        self.userAgent = userAgent
        self.timeZone = timeZone
        self.zcodeKey = zcodeKey
        self.browser = browser ?? BrowserCookieReader(home: home)
        self.antigravity = antigravity ?? AntigravityProcessLocator(home: home)
        self.now = now
    }
}
