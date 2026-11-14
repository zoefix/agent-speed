import Foundation
import os
@testable import AgentSpeedCore

/// Answers quota requests from a script by URL and records them; no network.
final class ScriptedQuotaTransport: QuotaHTTPTransport, Sendable {
    enum Answer: Sendable {
        case status(Int, String, dateMs: Int64? = nil)
        case failure(QuotaHTTPError)
    }

    private struct State {
        var answers: [String: [Answer]] = [:]
        var requests: [QuotaHTTPRequest] = []
    }

    private let state = OSAllocatedUnfairLock(initialState: State())

    /// Queues answers for a URL; the last one repeats.
    func script(_ url: String, _ answers: Answer...) {
        state.withLock { $0.answers[url, default: []].append(contentsOf: answers) }
    }

    var requests: [QuotaHTTPRequest] { state.withLock { $0.requests } }
    var urls: [String] { requests.map(\.url.absoluteString) }

    func reset() { state.withLock { $0.requests = [] } }

    func send(_ request: QuotaHTTPRequest) async throws(QuotaHTTPError) -> QuotaHTTPResponse {
        let answer = state.withLock { state -> Answer? in
            state.requests.append(request)
            let key = request.url.absoluteString
            guard var queue = state.answers[key], !queue.isEmpty else { return nil }
            let next = queue.count > 1 ? queue.removeFirst() : queue[0]
            state.answers[key] = queue
            return next
        }
        switch answer {
        case .status(let code, let body, let date)?:
            return QuotaHTTPResponse(status: code, body: (200..<300).contains(code) ? Data(body.utf8) : Data(), dateMs: date)
        case .failure(let error)?:
            throw error
        case nil:
            throw .transport("offline")
        }
    }
}

struct FixedAntigravity: AntigravityLocating {
    var found: [AntigravityServer]
    var agyRuns = false
    func servers() -> [AntigravityServer] { found }
    func scan() -> AntigravityScan { AntigravityScan(servers: found, agyRuns: agyRuns) }
}

/// agy's usage report from a script: whether a binary exists, and what each report returns.
final class FixedReport: AntigravityReporting, @unchecked Sendable {
    var binary: Bool
    var answer: (windows: [QuotaWindow], observedMs: Int64)?
    private(set) var reports = 0
    init(binary: Bool = true, answer: (windows: [QuotaWindow], observedMs: Int64)?) {
        self.binary = binary
        self.answer = answer
    }
    func available() -> Bool { binary }
    func report() -> (windows: [QuotaWindow], observedMs: Int64)? {
        reports += 1
        return answer
    }
}

final class FixedBrowser: BrowserSessionReading, @unchecked Sendable {
    var session: BrowserSession?
    private(set) var reads = 0
    init(_ session: BrowserSession?) { self.session = session }
    func session(sites: [String], nowMs: Int64) -> BrowserSession? {
        reads += 1
        return session.flatMap { sites.contains($0.site) || sites.contains(where: { $0.hasSuffix("workbuddy.cn") }) ? $0 : nil }
    }
    func resetRefusals() {}
}

extension QuotaVendorEnvironment {
    static func test(home: URL, transport: ScriptedQuotaTransport, variables: [String: String] = [:],
                     zcodeKey: String? = nil, browser: FixedBrowser = FixedBrowser(nil),
                     antigravity: [AntigravityServer] = [], agyRuns: Bool = false, report: FixedReport? = nil,
                     nowMs: Int64 = QuotaServiceTests.now) -> QuotaVendorEnvironment {
        QuotaVendorEnvironment(home: home, variables: variables, transport: transport, userAgent: "AgentSpeed/9.9.9 (macOS)",
                               timeZone: { TimeZone(identifier: "Asia/Taipei")! }, zcodeKey: { zcodeKey }, browser: browser,
                               antigravity: FixedAntigravity(found: antigravity, agyRuns: agyRuns), antigravityReport: report,
                               now: { nowMs })
    }
}
