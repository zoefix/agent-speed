import Foundation

/// One request a round decided on, with its credential inside; it lives only until the request is done.
struct FetchPlan: Sendable {
    enum Kind: Sendable {
        /// One request; `parse` turns a 2xx JSON body into windows, nil when it is not the expected answer.
        case single(QuotaHTTPRequest, parse: @Sendable (JSONValue, Int64) -> [QuotaWindow]?)
        /// `loadCodeAssist` (when the token's project is not known yet), then `retrieveUserQuota`.
        case gemini(token: String, project: String??, userAgent: String)
        /// The servers' addresses in order, until one answers.
        case antigravity([AntigravityServer], userAgent: String)
        /// The browser's session, read off the actor (macOS may ask for a keychain item meanwhile), then one request
        /// unless that very session was refused before.
        case browser(reader: any BrowserSessionReading, sites: [String], refused: QuotaFingerprint?, userAgent: String)
    }

    var route: QuotaRoute
    var credential: QuotaFingerprint
    var kind: Kind
}

struct FetchResult: Sendable {
    /// A browser route found no session, or only the one refused before; nothing was asked.
    enum Skipped: Sendable {
        case noSession
        case refused
    }

    var route: QuotaRoute
    var credential: QuotaFingerprint
    var outcome: QuotaCadence.Outcome
    var skipped: Skipped?
    /// The site a browser session came from.
    var site: String?
    var windows: [QuotaWindow] = []
    var observedMs: Int64?
    var statusCode: Int?
    var geminiProject: String??
    var forgetTokens: Set<Int32> = []
}

extension QuotaVendorService {
    /// Reads the route's credential and decides whether to ask; nil when nothing goes out this round.
    func plan(_ route: QuotaRoute, _ round: QuotaVendorRound) -> FetchPlan? {
        var state = states[route] ?? RouteState()
        defer { states[route] = state }
        let env = environment
        let ua = env.userAgent
        let now = round.nowMs
        switch route {
        case .grokBuild, .devin:
            return nil
        case .grokBuildLive:
            guard let token = credential(QuotaCredentials.grok(home: env.home, environment: env.variables), &state) else { return nil }
            guard !expired(token, now: now, margin: Self.grokExpiryMarginMs, &state) else { return nil }
            guard gate(route, round, credential: token.fingerprint, state: &state) else { return nil }
            return FetchPlan(route: route, credential: token.fingerprint, kind: .single(
                QuotaRequests.grokBilling(token: token.value, userAgent: ua), parse: { body, _ in GrokBilling.fromResponse(body) }))
        case .kimi:
            guard let token = credential(QuotaCredentials.kimi(home: env.home, environment: env.variables), &state) else { return nil }
            guard !expired(token, now: now, &state) else { return nil }
            guard gate(route, round, credential: token.fingerprint, state: &state) else { return nil }
            return FetchPlan(route: route, credential: token.fingerprint, kind: .single(
                QuotaRequests.kimiUsages(token: token.value, userAgent: ua), parse: { body, _ in KimiUsage.windows(body) }))
        case .gemini:
            guard let token = credential(QuotaCredentials.gemini(home: env.home, environment: env.variables), &state) else { return nil }
            guard !expired(token, now: now, &state) else { return nil }
            guard gate(route, round, credential: token.fingerprint, state: &state) else { return nil }
            let known: String?? = state.geminiProject.flatMap { $0.credential == token.fingerprint ? .some($0.project) : nil }
            return FetchPlan(route: route, credential: token.fingerprint, kind: .gemini(token: token.value, project: known, userAgent: ua))
        case .droid:
            let key = QuotaCredentials.droid(home: env.home, environment: env.variables).map { QuotaToken(value: $0) }
            guard let token = credential(key, &state) else { return nil }
            guard gate(route, round, credential: token.fingerprint, state: &state) else { return nil }
            return FetchPlan(route: route, credential: token.fingerprint, kind: .single(
                QuotaRequests.droidLimits(key: token.value, userAgent: ua), parse: { body, now in DroidLimits.windows(body, nowMs: now) }))
        case .zcode:
            // The keychain is read only when a request could go out; a new key forgets the route first.
            guard round.networkEnabled, asks(route, round) else {
                _ = gate(route, round, credential: state.account ?? QuotaFingerprint(""), state: &state)
                return nil
            }
            if let last = state.cadence.lastRequestMs {
                let wait = state.cadence.lastOutcome == .success ? QuotaCadence.intervalMs : QuotaCadence.failureWaitMs
                if round.nowMs - last < wait || state.cadence.lastOutcome == .denied { return nil }
            }
            guard let key = env.zcodeKey(), Self.isHeaderSafe(key) else {
                // No key saved: the key field itself says what to do.
                state.reading.status = nil
                state.reading.signedIn = false
                return nil
            }
            state.reading.signedIn = true
            let fingerprint = QuotaFingerprint(key + "|" + round.zaiRegion.rawValue)
            guard gate(route, round, credential: fingerprint, state: &state) else { return nil }
            let region = round.zaiRegion
            return FetchPlan(route: route, credential: fingerprint, kind: .single(
                QuotaRequests.zaiLimits(key: key, region: region, userAgent: ua), parse: { body, now in ZaiLimits.windows(body, nowMs: now) }))
        case .antigravity:
            return antigravityPlan(round, &state)
        case .qoder, .workbuddy:
            return browserPlan(route, round, &state)
        }
    }

    /// A token that can go into a header; otherwise the route has no sign-in.
    private func credential(_ token: QuotaToken?, _ state: inout RouteState) -> QuotaToken? {
        guard let token, Self.isHeaderSafe(token.value) else {
            state.reading.status = .noCredential
            state.reading.signedIn = false
            return nil
        }
        return token
    }

    /// The token's own expiry has passed: no request, and the last snapshot stays with its age.
    private func expired(_ token: QuotaToken, now: Int64, margin: Int64 = 0, _ state: inout RouteState) -> Bool {
        guard token.isExpired(nowMs: now, marginMs: margin) else {
            state.reading.signedIn = true
            return false
        }
        state.reading.status = .expired
        state.reading.signedIn = false
        return true
    }

    /// Printable ASCII only: a credential never breaks a header.
    static func isHeaderSafe(_ value: String) -> Bool {
        !value.isEmpty && value.utf8.count <= 8192 && value.utf8.allSatisfy { (0x21...0x7E).contains($0) || $0 == 0x20 }
    }

    // MARK: Antigravity (QUOTAS 9.4)

    /// The servers are looked for only when a request could go out: the agent runs (or the dashboard is open), the
    /// network is on and the last request is old enough.
    private func antigravityPlan(_ round: QuotaVendorRound, _ state: inout RouteState) -> FetchPlan? {
        guard round.networkEnabled else {
            state.reading.status = .networkOff
            return nil
        }
        guard round.dashboardOpen || round.running.contains(.antigravity) else {
            antigravityRuns = false
            state.reading.status = .notRunning
            return nil
        }
        if let last = state.cadence.lastRequestMs {
            let wait = state.cadence.lastOutcome == .success ? QuotaCadence.intervalMs : QuotaCadence.failureWaitMs
            if round.nowMs - last < wait { return nil }
        }
        var servers = environment.antigravity.servers()
        antigravityRuns = !servers.isEmpty
        forgottenTokens.formIntersection(servers.map(\.pid))
        guard !servers.isEmpty else {
            state.reading.status = .notRunning
            state.reading.signedIn = false
            return nil
        }
        for index in servers.indices where forgottenTokens.contains(servers[index].pid) {
            for endpoint in servers[index].endpoints.indices { servers[index].endpoints[endpoint].token = nil }
        }
        let identity = servers.map { server in
            "\(server.pid):" + server.endpoints.map { "\($0.scheme)\($0.port)\($0.token ?? "")" }.joined(separator: ",")
        }.joined(separator: ";")
        let fingerprint = QuotaFingerprint(identity)
        guard gate(.antigravity, round, credential: fingerprint, state: &state) else { return nil }
        return FetchPlan(route: .antigravity, credential: fingerprint, kind: .antigravity(servers, userAgent: environment.userAgent))
    }

    // MARK: Browser sessions (QUOTAS 9.9, 9.10)

    /// The browser is read only when a request is due: every 5 minutes, 15 after a failure or a refusal (a refused
    /// session is asked again only once its cookie changed).
    private func browserPlan(_ route: QuotaRoute, _ round: QuotaVendorRound, _ state: inout RouteState) -> FetchPlan? {
        guard round.networkEnabled else {
            state.reading.status = .networkOff
            return nil
        }
        guard asks(route, round) else {
            if state.reading.status == nil || state.reading.status == .networkOff { state.reading.status = .notRunning }
            return nil
        }
        let wait = state.cadence.lastOutcome == .success ? QuotaCadence.intervalMs : QuotaCadence.failureWaitMs
        if let checked = state.checkedMs, round.nowMs - checked < wait { return nil }
        state.checkedMs = round.nowMs
        let refused = state.cadence.lastOutcome == .denied ? state.cadence.credential : nil
        return FetchPlan(route: route, credential: state.account ?? QuotaFingerprint(""),
                         kind: .browser(reader: environment.browser, sites: route.cookieSites, refused: refused,
                                        userAgent: environment.userAgent))
    }
}

// MARK: Running a plan

extension FetchPlan {
    func execute(_ transport: any QuotaHTTPTransport, now: @Sendable () -> Int64) async -> FetchResult {
        switch kind {
        case .single(let request, let parse):
            let answer = await Self.ask(request, transport, now: now)
            var result = FetchResult(route: route, credential: credential, outcome: answer.outcome, statusCode: answer.status)
            if answer.outcome == .success, let body = answer.body, let windows = parse(body, now()) {
                result.windows = windows
                result.observedMs = answer.dateMs ?? answer.arrivedMs
            } else if answer.outcome == .success {
                result.outcome = .failure
            }
            return result
        case .gemini(let token, let known, let userAgent):
            var project: String?? = known
            var learned: String??
            if project == nil {
                let load = await Self.ask(QuotaRequests.geminiLoad(token: token, userAgent: userAgent), transport, now: now)
                guard load.outcome == .success, let body = load.body else {
                    return FetchResult(route: route, credential: credential, outcome: load.outcome == .denied ? .denied : .failure,
                                       statusCode: load.status)
                }
                project = .some(GeminiQuota.project(body))
                learned = project
            }
            let answer = await Self.ask(QuotaRequests.geminiQuota(token: token, project: project ?? nil, userAgent: userAgent),
                                        transport, now: now)
            var result = FetchResult(route: route, credential: credential, outcome: answer.outcome, statusCode: answer.status,
                                     geminiProject: learned)
            if answer.outcome == .success, let body = answer.body {
                result.windows = GeminiQuota.windows(body)
                result.observedMs = answer.dateMs ?? answer.arrivedMs
            } else if answer.outcome == .success {
                result.outcome = .failure
            }
            return result
        case .antigravity(let servers, let userAgent):
            return await antigravity(servers, userAgent: userAgent, transport, now: now)
        case .browser(let reader, let sites, let refused, let userAgent):
            return await browser(reader, sites: sites, refused: refused, userAgent: userAgent, transport, now: now)
        }
    }

    private func browser(_ reader: any BrowserSessionReading, sites: [String], refused: QuotaFingerprint?, userAgent: String,
                         _ transport: any QuotaHTTPTransport, now: @Sendable () -> Int64) async -> FetchResult {
        let nowMs = now()
        let session = await withCheckedContinuation { continuation in
            DispatchQueue.global(qos: .utility).async {
                continuation.resume(returning: reader.session(sites: sites, nowMs: nowMs))
            }
        }
        guard let session, QuotaVendorService.isHeaderSafe(session.cookie), QuotaVendorService.isHeaderSafe(session.userAgent)
        else {
            return FetchResult(route: route, credential: credential, outcome: .failure, skipped: .noSession)
        }
        let fingerprint = session.fingerprint
        if fingerprint == refused {
            return FetchResult(route: route, credential: fingerprint, outcome: .denied, skipped: .refused, site: session.site)
        }
        let request: QuotaHTTPRequest?
        let parse: @Sendable (JSONValue, Int64) -> [QuotaWindow]?
        if route == .qoder {
            request = QuotaRequests.qoderCredits(cookie: session.cookie, site: session.site, userAgent: userAgent)
            parse = { body, _ in body.object == nil ? nil : BrowserSessionCredits.qoderWindows(body) }
        } else {
            request = QuotaRequests.workBuddySummary(cookie: session.cookie, browserUserAgent: session.userAgent)
            parse = { body, _ in BrowserSessionCredits.workBuddyWindows(body) }
        }
        guard let request else {
            return FetchResult(route: route, credential: fingerprint, outcome: .failure, skipped: .noSession)
        }
        let plan = FetchPlan(route: route, credential: fingerprint, kind: .single(request, parse: parse))
        var result = await plan.execute(transport, now: now)
        result.site = session.site
        return result
    }

    private func antigravity(_ servers: [AntigravityServer], userAgent: String, _ transport: any QuotaHTTPTransport,
                             now: @Sendable () -> Int64) async -> FetchResult {
        var result = FetchResult(route: route, credential: credential, outcome: .failure)
        var denied = false
        for server in servers {
            for endpoint in server.endpoints {
                guard let summary = QuotaRequests.antigravity(.summary, scheme: endpoint.scheme, port: endpoint.port,
                                                               token: endpoint.token, userAgent: userAgent) else { continue }
                var answer = await Self.ask(summary, transport, now: now)
                var call = QuotaRequests.AntigravityCall.summary
                if answer.status == 404, let status = QuotaRequests.antigravity(.status, scheme: endpoint.scheme, port: endpoint.port,
                                                                               token: endpoint.token, userAgent: userAgent) {
                    answer = await Self.ask(status, transport, now: now)
                    call = .status
                }
                result.statusCode = answer.status
                if answer.outcome == .denied {
                    denied = true
                    if endpoint.token != nil { result.forgetTokens.insert(server.pid) }
                    continue
                }
                guard answer.outcome == .success, let body = answer.body else { continue }
                result.outcome = .success
                result.windows = call == .summary ? AntigravitySummary.summaryWindows(body) : AntigravitySummary.statusWindows(body)
                result.observedMs = answer.dateMs ?? answer.arrivedMs
                return result
            }
        }
        if denied { result.outcome = .denied }
        return result
    }

    struct Answer {
        var outcome: QuotaCadence.Outcome
        var status: Int?
        var body: JSONValue?
        var dateMs: Int64?
        var arrivedMs: Int64
    }

    /// 2xx with a JSON body is a success, 401/403 a refusal, anything else (a redirect, a cap, a timeout) a failure.
    static func ask(_ request: QuotaHTTPRequest, _ transport: any QuotaHTTPTransport, now: @Sendable () -> Int64) async -> Answer {
        do {
            let response = try await transport.send(request)
            let arrived = now()
            switch response.status {
            case 200..<300:
                guard let body = try? JSONParser.parse(response.body), body.object != nil else {
                    return Answer(outcome: .failure, status: response.status, arrivedMs: arrived)
                }
                return Answer(outcome: .success, status: response.status, body: body, dateMs: response.dateMs, arrivedMs: arrived)
            case 401, 403:
                return Answer(outcome: .denied, status: response.status, arrivedMs: arrived)
            default:
                return Answer(outcome: .failure, status: response.status, arrivedMs: arrived)
            }
        } catch {
            return Answer(outcome: .failure, arrivedMs: now())
        }
    }
}
