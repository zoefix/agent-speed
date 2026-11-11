import Foundation
import os
import Testing
@testable import AgentSpeedCore

/// The requests of QUOTAS 9.2–9.9: URL, method, only the listed headers plus the user agent, and the body.
struct QuotaRequestTests {
    static let ua = "AgentSpeed/9.9.9 (macOS)"

    static func headers(_ request: QuotaHTTPRequest) -> [String] { request.headers.map { "\($0.name): \($0.value)" } }
    static func body(_ request: QuotaHTTPRequest) -> String? { request.body.map { String(decoding: $0, as: UTF8.self) } }

    @Test func everyRoute() throws {
        let grok = QuotaRequests.grokBilling(token: "g", userAgent: Self.ua)
        #expect(grok.method == .get)
        #expect(grok.url.absoluteString == "https://cli-chat-proxy.grok.com/v1/billing?format=credits")
        #expect(Self.headers(grok) == ["Accept: application/json", "Authorization: Bearer g", "x-xai-token-auth: xai-grok-cli",
                                       "User-Agent: \(Self.ua)"])
        #expect(grok.body == nil)

        let kimi = QuotaRequests.kimiUsages(token: "k", userAgent: Self.ua)
        #expect(kimi.method == .get)
        #expect(kimi.url.absoluteString == "https://api.kimi.com/coding/v1/usages")
        #expect(Self.headers(kimi) == ["Accept: application/json", "Authorization: Bearer k", "User-Agent: \(Self.ua)"])

        let load = QuotaRequests.geminiLoad(token: "t", userAgent: Self.ua)
        #expect(load.method == .post)
        #expect(load.url.absoluteString == "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist")
        #expect(Self.headers(load) == ["Authorization: Bearer t", "Content-Type: application/json", "User-Agent: \(Self.ua)"])
        #expect(Self.body(load) == #"{"metadata":{"ideType":"GEMINI_CLI","pluginType":"GEMINI"}}"#)
        let quota = QuotaRequests.geminiQuota(token: "t", project: "proj-\"1", userAgent: Self.ua)
        #expect(quota.url.absoluteString == "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota")
        #expect(Self.body(quota) == #"{"project":"proj-\"1"}"#)
        #expect(Self.body(QuotaRequests.geminiQuota(token: "t", project: nil, userAgent: Self.ua)) == "{}")

        let droid = QuotaRequests.droidLimits(key: "fk", userAgent: Self.ua)
        #expect(droid.url.absoluteString == "https://api.factory.ai/api/billing/limits")
        #expect(Self.headers(droid) == ["Accept: application/json", "Authorization: Bearer fk", "User-Agent: \(Self.ua)"])

        let zai = QuotaRequests.zaiLimits(key: "z", region: .global, userAgent: Self.ua)
        #expect(zai.url.absoluteString == "https://api.z.ai/api/monitor/usage/quota/limit")
        #expect(Self.headers(zai) == ["Authorization: Bearer z", "User-Agent: \(Self.ua)"])
        #expect(QuotaRequests.zaiLimits(key: "z", region: .bigmodelCn, userAgent: Self.ua).url.absoluteString
                == "https://open.bigmodel.cn/api/monitor/usage/quota/limit")
        #expect(QuotaRequests.ZaiRegion(rawValue: "bigmodel-cn") == .bigmodelCn)

        let qoder = try #require(QuotaRequests.qoderCredits(cookie: "a=1", site: "qoder.com.cn", userAgent: Self.ua))
        #expect(qoder.method == .get)
        #expect(qoder.url.absoluteString == "https://qoder.com.cn/api/v2/me/usages/big_model_credits")
        #expect(Self.headers(qoder) == ["Cookie: a=1", "Origin: https://qoder.com.cn", "Referer: https://qoder.com.cn/",
                                        "Accept: application/json", "User-Agent: \(Self.ua)"])
        #expect(QuotaRequests.qoderCredits(cookie: "a=1", site: "evil.com", userAgent: Self.ua) == nil)

        let browserUA = BrowserUserAgent.chrome(major: 142)
        let workbuddy = QuotaRequests.workBuddySummary(cookie: "b=2", browserUserAgent: browserUA)
        #expect(workbuddy.method == .post)
        #expect(workbuddy.url.absoluteString == "https://www.workbuddy.cn/billing/meter/get-user-resource-summary")
        #expect(Self.headers(workbuddy) == ["Content-Type: application/json", "Cookie: b=2", "User-Agent: \(browserUA)"])
        #expect(Self.body(workbuddy) == "{}")

        let summary = try #require(QuotaRequests.antigravity(.summary, scheme: "https", port: 4242, token: "csrf", userAgent: Self.ua))
        #expect(summary.method == .post)
        #expect(summary.url.absoluteString
                == "https://127.0.0.1:4242/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary")
        #expect(Self.headers(summary) == ["Connect-Protocol-Version: 1", "Content-Type: application/json",
                                          "X-Codeium-Csrf-Token: csrf", "User-Agent: \(Self.ua)"])
        #expect(Self.body(summary) == #"{"forceRefresh":true}"#)
        #expect(summary.timeout == 2)
        let status = try #require(QuotaRequests.antigravity(.status, scheme: "http", port: 80, token: nil, userAgent: Self.ua))
        #expect(status.url.absoluteString == "http://127.0.0.1:80/exa.language_server_pb.LanguageServerService/GetUserStatus")
        #expect(status.header("X-Codeium-Csrf-Token") == nil)
        #expect(Self.body(status)
                == #"{"metadata":{"ideName":"antigravity","extensionName":"antigravity","ideVersion":"unknown","locale":"en"}}"#)
        #expect(QuotaRequests.antigravity(.summary, scheme: "ftp", port: 1, token: nil, userAgent: Self.ua) == nil)

        for request in [grok, kimi, load, droid, zai, qoder, workbuddy] {
            #expect(request.timeout == 10)
            #expect(request.maxBytes == 256 << 10)
            #expect(!"\(request)".contains("Bearer"))
        }
    }

    @Test func onlyTheHostsOfNetwork9() {
        for url in ["https://api.kimi.com/x", "https://open.bigmodel.cn/x", "http://127.0.0.1:8080/x", "https://127.0.0.1:1/x"] {
            #expect(QuotaHosts.permits(URL(string: url)), "\(url)")
        }
        for url in ["http://api.kimi.com/x", "https://claude.ai/api", "https://api.anthropic.com/v1", "https://api.kimi.com:8443/x",
                    "https://u@api.kimi.com/x", "http://127.0.0.1/x", "http://localhost:80/x", "https://kimi.com/x"] {
            #expect(!QuotaHosts.permits(URL(string: url)), "\(url)")
        }
    }

    @Test func cadence() {
        var cadence = QuotaCadence()
        let a = QuotaFingerprint("a"), b = QuotaFingerprint("b")
        #expect(cadence.isDue(nowMs: 0, credential: a))
        cadence.record(.success, nowMs: 0, credential: a)
        #expect(!cadence.isDue(nowMs: 299_999, credential: a))
        #expect(cadence.isDue(nowMs: 300_000, credential: a))
        #expect(cadence.isDue(nowMs: 1, credential: b))
        cadence.record(.failure, nowMs: 0, credential: a)
        #expect(!cadence.isDue(nowMs: 899_999, credential: a))
        #expect(cadence.isDue(nowMs: 900_000, credential: a))
        cadence.record(.denied, nowMs: 0, credential: a)
        #expect(cadence.isDenied(a))
        #expect(!cadence.isDue(nowMs: 100 * 3_600_000, credential: a))
        #expect(cadence.isDue(nowMs: 1, credential: b))
        #expect(QuotaFingerprint("a") == a && a != b)
    }
}

/// `QuotaURLSessionTransport` inside the URL loading system without network: methods, headers and bodies as sent, no
/// redirect followed, the cap, and the `Date` header.
@Suite(.serialized)
struct QuotaTransportTests {
    final class Stub: URLProtocol, @unchecked Sendable {
        struct Reply: Sendable {
            var status = 200
            var headers: [String: String] = [:]
            var chunks: [Data] = []
            var redirect: String?
        }

        struct Seen: Sendable {
            var method: String
            var headers: [String: String]
            var body: Data
        }

        static let replies = OSAllocatedUnfairLock<[String: Reply]>(initialState: [:])
        static let seen = OSAllocatedUnfairLock<[String: Seen]>(initialState: [:])

        override class func canInit(with request: URLRequest) -> Bool { true }
        override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

        override func startLoading() {
            guard let url = request.url, let reply = Self.replies.withLock({ $0[url.absoluteString] }) else {
                client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
                return
            }
            var body = request.httpBody ?? Data()
            if let stream = request.httpBodyStream {
                stream.open()
                var buffer = [UInt8](repeating: 0, count: 4096)
                while stream.hasBytesAvailable {
                    let count = stream.read(&buffer, maxLength: buffer.count)
                    if count <= 0 { break }
                    body.append(contentsOf: buffer[0..<count])
                }
                stream.close()
            }
            let seen = Seen(method: request.httpMethod ?? "", headers: request.allHTTPHeaderFields ?? [:], body: body)
            Self.seen.withLock { $0[url.absoluteString] = seen }
            if let target = reply.redirect, let next = URL(string: target) {
                let response = HTTPURLResponse(url: url, statusCode: 302, httpVersion: "HTTP/1.1", headerFields: ["Location": target])!
                client?.urlProtocol(self, wasRedirectedTo: URLRequest(url: next), redirectResponse: response)
                client?.urlProtocolDidFinishLoading(self)
                return
            }
            let response = HTTPURLResponse(url: url, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: reply.headers)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            for chunk in reply.chunks { client?.urlProtocol(self, didLoad: chunk) }
            client?.urlProtocolDidFinishLoading(self)
        }

        override func stopLoading() {}
    }

    static let transport = QuotaURLSessionTransport(protocolClasses: [Stub.self])

    /// The stub answers at once; the long timeout only keeps a starved CI runner from failing it.
    static func send(_ request: QuotaHTTPRequest) async throws(QuotaHTTPError) -> QuotaHTTPResponse {
        var request = request
        request.timeout = 60
        return try await transport.send(request)
    }

    @Test func aPostWithItsHeadersBodyAndDate() async throws {
        let request = QuotaRequests.workBuddySummary(cookie: "s=1", browserUserAgent: "Browser/1")
        Stub.replies.withLock {
            $0[request.url.absoluteString] = .init(headers: ["Date": "Tue, 10 Nov 2026 19:26:13 GMT", "Set-Cookie": "x=y"],
                                                   chunks: [Data(#"{"code":0}"#.utf8)])
        }
        let response = try await Self.send(request)
        #expect(response.status == 200)
        #expect(response.body == Data(#"{"code":0}"#.utf8))
        #expect(response.dateMs == 1_794_338_773_000)
        let seen = try #require(Stub.seen.withLock { $0[request.url.absoluteString] })
        #expect(seen.method == "POST")
        #expect(seen.body == Data("{}".utf8))
        #expect(seen.headers["Cookie"] == "s=1")
        #expect(seen.headers["User-Agent"] == "Browser/1")
        #expect(seen.headers["Content-Type"] == "application/json")
        #expect(seen.headers["Authorization"] == nil)
        #expect(Set(seen.headers.keys).isSubset(of: ["Cookie", "User-Agent", "Content-Type", "Accept-Language", "Content-Length"]))
    }

    @Test func redirectsAreNeverFollowed() async throws {
        let request = QuotaRequests.kimiUsages(token: "secret", userAgent: "UA")
        Stub.replies.withLock { $0[request.url.absoluteString] = .init(redirect: "https://api.kimi.com/elsewhere") }
        Stub.replies.withLock { $0["https://api.kimi.com/elsewhere"] = .init(chunks: [Data("{}".utf8)]) }
        await #expect(throws: QuotaHTTPError.redirected) { try await Self.send(request) }
        #expect(Stub.seen.withLock { $0["https://api.kimi.com/elsewhere"] } == nil)
    }

    @Test func theCapAndErrorStatuses() async throws {
        let big = QuotaRequests.droidLimits(key: "k", userAgent: "UA")
        Stub.replies.withLock { $0[big.url.absoluteString] = .init(chunks: [Data(count: 200 << 10), Data(count: 100 << 10)]) }
        await #expect(throws: QuotaHTTPError.tooLarge) { try await Self.send(big) }
        let declared = QuotaRequests.grokBilling(token: "g", userAgent: "UA")
        Stub.replies.withLock { $0[declared.url.absoluteString] = .init(headers: ["Content-Length": "300000"], chunks: [Data(count: 300_000)]) }
        await #expect(throws: QuotaHTTPError.tooLarge) { try await Self.send(declared) }
        let denied = QuotaRequests.zaiLimits(key: "z", region: .global, userAgent: "UA")
        Stub.replies.withLock { $0[denied.url.absoluteString] = .init(status: 401, chunks: [Data("nope".utf8)]) }
        let response = try await Self.send(denied)
        #expect(response.status == 401)
        #expect(response.body.isEmpty)
    }

    @Test func otherHostsAreRefusedBeforeAnything() async {
        var changed = QuotaRequests.kimiUsages(token: "t", userAgent: "UA")
        changed.url = URL(string: "https://api.anthropic.com/api/oauth/usage")!
        let request = changed
        await #expect(throws: QuotaHTTPError.disallowedURL) { try await Self.send(request) }
        #expect(Stub.seen.withLock { $0[request.url.absoluteString] } == nil)
    }
}
