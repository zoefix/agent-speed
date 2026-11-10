import Foundation

/// The requests of QUOTAS 9.2–9.9, each with only the headers its section lists plus `User-Agent`
/// (`AgentSpeed/<version> (macOS)`; WorkBuddy sends the browser's own).
public enum QuotaRequests {
    static let json = "application/json"

    public enum ZaiRegion: String, CaseIterable, Sendable {
        case global
        case bigmodelCn = "bigmodel-cn"

        public var host: String { self == .global ? QuotaHosts.zai : QuotaHosts.bigmodel }
    }

    static func grokBilling(token: String, userAgent: String) -> QuotaHTTPRequest {
        QuotaHTTPRequest(.get, URL(string: "https://\(QuotaHosts.grok)/v1/billing?format=credits")!, headers: [
            QuotaHeader("Accept", json), QuotaHeader("Authorization", "Bearer \(token)"),
            QuotaHeader("x-xai-token-auth", "xai-grok-cli"), QuotaHeader("User-Agent", userAgent),
        ])
    }

    static func kimiUsages(token: String, userAgent: String) -> QuotaHTTPRequest {
        QuotaHTTPRequest(.get, URL(string: "https://\(QuotaHosts.kimi)/coding/v1/usages")!, headers: [
            QuotaHeader("Accept", json), QuotaHeader("Authorization", "Bearer \(token)"), QuotaHeader("User-Agent", userAgent),
        ])
    }

    static let geminiLoadBody = Data(#"{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}"#.utf8)

    static func geminiLoad(token: String, userAgent: String) -> QuotaHTTPRequest {
        QuotaHTTPRequest(.post, URL(string: "https://\(QuotaHosts.gemini)/v1internal:loadCodeAssist")!,
                         headers: geminiHeaders(token, userAgent), body: geminiLoadBody)
    }

    static func geminiQuota(token: String, project: String?, userAgent: String) -> QuotaHTTPRequest {
        let body = project.map { "{\"project\":\(ReportJSON.quoted($0))}" } ?? "{}"
        return QuotaHTTPRequest(.post, URL(string: "https://\(QuotaHosts.gemini)/v1internal:retrieveUserQuota")!,
                                headers: geminiHeaders(token, userAgent), body: Data(body.utf8))
    }

    private static func geminiHeaders(_ token: String, _ userAgent: String) -> [QuotaHeader] {
        [QuotaHeader("Authorization", "Bearer \(token)"), QuotaHeader("Content-Type", json), QuotaHeader("User-Agent", userAgent)]
    }

    static func droidLimits(key: String, userAgent: String) -> QuotaHTTPRequest {
        QuotaHTTPRequest(.get, URL(string: "https://\(QuotaHosts.factory)/api/billing/limits")!, headers: [
            QuotaHeader("Accept", json), QuotaHeader("Authorization", "Bearer \(key)"), QuotaHeader("User-Agent", userAgent),
        ])
    }

    static func zaiLimits(key: String, region: ZaiRegion, userAgent: String) -> QuotaHTTPRequest {
        QuotaHTTPRequest(.get, URL(string: "https://\(region.host)/api/monitor/usage/quota/limit")!, headers: [
            QuotaHeader("Authorization", "Bearer \(key)"), QuotaHeader("User-Agent", userAgent),
        ])
    }

    /// `site` is `qoder.com` or `qoder.com.cn`, where the cookies came from.
    static func qoderCredits(cookie: String, site: String, userAgent: String) -> QuotaHTTPRequest? {
        guard site == QuotaHosts.qoder || site == QuotaHosts.qoderCn else { return nil }
        return QuotaHTTPRequest(.get, URL(string: "https://\(site)/api/v2/me/usages/big_model_credits")!, headers: [
            QuotaHeader("Cookie", cookie), QuotaHeader("Origin", "https://\(site)"), QuotaHeader("Referer", "https://\(site)/"),
            QuotaHeader("Accept", json), QuotaHeader("User-Agent", userAgent),
        ])
    }

    /// The browser's user agent: the server ties the session to it.
    static func workBuddySummary(cookie: String, browserUserAgent: String) -> QuotaHTTPRequest {
        QuotaHTTPRequest(.post, URL(string: "https://\(QuotaHosts.workbuddy)/billing/meter/get-user-resource-summary")!,
                         headers: [QuotaHeader("Content-Type", json), QuotaHeader("Cookie", cookie),
                                   QuotaHeader("User-Agent", browserUserAgent)],
                         body: Data("{}".utf8))
    }

    public enum AntigravityCall: String, Sendable {
        case summary = "RetrieveUserQuotaSummary"
        case status = "GetUserStatus"

        var body: Data {
            switch self {
            case .summary: Data(#"{"forceRefresh":true}"#.utf8)
            case .status:
                Data(#"{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}"#.utf8)
            }
        }
    }

    /// Loopback only; the token is left out when there is none.
    static func antigravity(_ call: AntigravityCall, scheme: String, port: Int, token: String?, userAgent: String) -> QuotaHTTPRequest? {
        guard scheme == "http" || scheme == "https", (1...65535).contains(port),
              let url = URL(string: "\(scheme)://\(QuotaHosts.loopback):\(port)/exa.language_server_pb.LanguageServerService/\(call.rawValue)")
        else { return nil }
        var headers = [QuotaHeader("Connect-Protocol-Version", "1"), QuotaHeader("Content-Type", json)]
        if let token, !token.isEmpty { headers.append(QuotaHeader("X-Codeium-Csrf-Token", token)) }
        headers.append(QuotaHeader("User-Agent", userAgent))
        return QuotaHTTPRequest(.post, url, headers: headers, body: call.body)
    }
}
