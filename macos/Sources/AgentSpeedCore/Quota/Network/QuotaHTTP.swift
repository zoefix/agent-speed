import Foundation

/// One quota request (QUOTAS 9.1, NETWORK 9): only the headers its section lists plus the user agent, HTTPS to one
/// of the hosts of NETWORK 9 (or the loopback language server), redirects refused, a body cap and a timeout. The
/// description never shows a header value: they carry credentials.
public struct QuotaHTTPRequest: Equatable, Sendable, CustomStringConvertible, CustomDebugStringConvertible {
    public enum Method: String, Sendable {
        case get = "GET"
        case post = "POST"
    }

    public static let maxBytes = 256 << 10
    public static let timeout: TimeInterval = 10
    public static let loopbackTimeout: TimeInterval = 2

    public var method: Method
    public var url: URL
    /// In order; `User-Agent` included.
    public var headers: [QuotaHeader]
    public var body: Data?
    public var timeout: TimeInterval
    public var maxBytes: Int

    public init(_ method: Method, _ url: URL, headers: [QuotaHeader], body: Data? = nil) {
        self.method = method
        self.url = url
        self.headers = headers
        self.body = body
        let loopback = QuotaHosts.isLoopback(url)
        timeout = loopback ? Self.loopbackTimeout : Self.timeout
        maxBytes = Self.maxBytes
    }

    public var isLoopback: Bool { QuotaHosts.isLoopback(url) }

    public func header(_ name: String) -> String? {
        headers.first { $0.name.caseInsensitiveCompare(name) == .orderedSame }?.value
    }

    public var description: String { "\(method.rawValue) \(url.host() ?? "?")" }
    public var debugDescription: String { description }
}

public struct QuotaHeader: Equatable, Sendable {
    public var name: String
    public var value: String

    public init(_ name: String, _ value: String) {
        self.name = name
        self.value = value
    }
}

/// The hosts of NETWORK 9; nothing else is ever asked, and never Anthropic.
public enum QuotaHosts {
    public static let grok = "cli-chat-proxy.grok.com"
    public static let kimi = "api.kimi.com"
    public static let gemini = "cloudcode-pa.googleapis.com"
    public static let factory = "api.factory.ai"
    public static let zai = "api.z.ai"
    public static let bigmodel = "open.bigmodel.cn"
    public static let qoder = "qoder.com"
    public static let qoderCn = "qoder.com.cn"
    public static let workbuddy = "www.workbuddy.cn"
    public static let loopback = "127.0.0.1"

    public static let remote: Set<String> = [grok, kimi, gemini, factory, zai, bigmodel, qoder, qoderCn, workbuddy]

    static func isLoopback(_ url: URL) -> Bool {
        url.host(percentEncoded: false) == loopback
    }

    /// HTTPS on port 443 to a listed host, or HTTP/HTTPS to 127.0.0.1 with a port; no user information.
    public static func permits(_ url: URL?) -> Bool {
        guard let url, let scheme = url.scheme?.lowercased(), let host = url.host(percentEncoded: false)?.lowercased(),
              url.user(percentEncoded: false) == nil, url.password(percentEncoded: false) == nil else { return false }
        if host == loopback { return (scheme == "http" || scheme == "https") && url.port != nil }
        guard scheme == "https", remote.contains(host) else { return false }
        return url.port.map { $0 == 443 } ?? true
    }
}

public struct QuotaHTTPResponse: Equatable, Sendable {
    public var status: Int
    /// Read for 2xx statuses only.
    public var body: Data
    /// The `Date` header.
    public var dateMs: Int64?

    public init(status: Int, body: Data = Data(), dateMs: Int64? = nil) {
        self.status = status
        self.body = body
        self.dateMs = dateMs
    }
}

public enum QuotaHTTPError: Error, Equatable, Sendable {
    case disallowedURL
    /// A redirect is never followed and counts as a failure.
    case redirected
    case tooLarge
    case transport(String)
}

/// The network seam: the app uses `QuotaURLSessionTransport`, tests a scripted one.
public protocol QuotaHTTPTransport: Sendable {
    func send(_ request: QuotaHTTPRequest) async throws(QuotaHTTPError) -> QuotaHTTPResponse
}
