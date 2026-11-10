import Foundation
import Testing
@testable import AgentSpeedCore

/// Rounds of the phase 2 readers against a temporary home and a scripted transport (QUOTAS 9.1–9.9, NETWORK 9).
struct QuotaServiceTests {
    static let now: Int64 = 1_794_338_773_000
    static let minute: Int64 = 60_000

    static func round(_ routes: Set<QuotaRoute>, at now: Int64 = QuotaServiceTests.now, network: Bool = true,
                      running: Set<SourceKind> = [], dashboard: Bool = false, region: QuotaRequests.ZaiRegion = .global)
        -> QuotaVendorRound {
        QuotaVendorRound(nowMs: now, networkEnabled: network, routes: routes, running: running, dashboardOpen: dashboard,
                         zaiRegion: region)
    }

    static let kimiBody = #"{"usages":{"limit_5h":{"used_ratio":0.25,"reset_time":"2026-11-10T22:00:00Z"},"limit_7d":{"used_ratio":0.5}}}"#
    static let kimiURL = "https://api.kimi.com/coding/v1/usages"

    static func kimiHome(token: String = "kimi-token", expiresS: Int64 = 1_900_000_000) throws -> TempDirectory {
        let home = try TempDirectory()
        try home.write(".kimi-code/credentials/kimi-code.json",
                       #"{"access_token":"\#(token)","refresh_token":"never-read","expires_at":\#(expiresS)}"#)
        return home
    }

    @Test func aRequestWhileTheAgentRunsThenEveryFiveMinutes() async throws {
        let home = try Self.kimiHome()
        let transport = ScriptedQuotaTransport()
        transport.script(Self.kimiURL, .status(200, Self.kimiBody, dateMs: Self.now - 1000))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        let first = await service.refresh(Self.round([.kimi], running: [.kimi]))
        let reading = try #require(first.routes[.kimi])
        #expect(reading.status == .ok(Self.now - 1000))
        #expect(reading.signedIn)
        #expect(reading.snapshot?.windows.map { $0.label() } == ["5h", "7d"])
        #expect(first.snapshot(.kimi)?.observedMs == Self.now - 1000)
        let request = try #require(transport.requests.first)
        #expect(request.header("Authorization") == "Bearer kimi-token")
        #expect(request.header("User-Agent") == "AgentSpeed/9.9.9 (macOS)")
        _ = await service.refresh(Self.round([.kimi], at: Self.now + 4 * Self.minute, running: [.kimi]))
        #expect(transport.requests.count == 1)
        _ = await service.refresh(Self.round([.kimi], at: Self.now + 5 * Self.minute, running: [.kimi]))
        #expect(transport.requests.count == 2)
    }

    @Test func nothingAsksWithTheNetworkOffOrWhileNothingRuns() async throws {
        let home = try Self.kimiHome()
        let transport = ScriptedQuotaTransport()
        transport.script(Self.kimiURL, .status(200, Self.kimiBody))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        var readings = await service.refresh(Self.round([.kimi], network: false, running: [.kimi]))
        #expect(readings.routes[.kimi]?.status == .networkOff)
        readings = await service.refresh(Self.round([.kimi]))
        #expect(readings.routes[.kimi]?.status == .notRunning)
        #expect(readings.routes[.kimi]?.signedIn == true)
        #expect(transport.requests.isEmpty)
        readings = await service.refresh(Self.round([.kimi], dashboard: true))
        #expect(transport.requests.count == 1)
        #expect(readings.routes[.kimi]?.status == .ok(Self.now))
        // Off forgets everything.
        readings = await service.refresh(Self.round([], dashboard: true))
        #expect(readings.routes[.kimi] == nil)
    }

    @Test func aRefusalStopsUntilTheSignInChangesAndAnotherAccountDropsTheNumbers() async throws {
        let home = try Self.kimiHome()
        let transport = ScriptedQuotaTransport()
        transport.script(Self.kimiURL, .status(200, Self.kimiBody), .status(401, ""), .status(200, Self.kimiBody))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        _ = await service.refresh(Self.round([.kimi], running: [.kimi]))
        var readings = await service.refresh(Self.round([.kimi], at: Self.now + 5 * Self.minute, running: [.kimi]))
        #expect(readings.routes[.kimi]?.status == .denied(host: "api.kimi.com"))
        #expect(readings.routes[.kimi]?.snapshot != nil)
        readings = await service.refresh(Self.round([.kimi], at: Self.now + 300 * Self.minute, running: [.kimi]))
        #expect(transport.requests.count == 2)
        #expect(readings.routes[.kimi]?.status == .denied(host: "api.kimi.com"))
        try home.write(".kimi-code/credentials/kimi-code.json", #"{"access_token":"other-account","expires_at":1900000000}"#)
        readings = await service.refresh(Self.round([.kimi], at: Self.now + 301 * Self.minute, network: false, running: [.kimi]))
        #expect(readings.routes[.kimi]?.snapshot == nil)
        readings = await service.refresh(Self.round([.kimi], at: Self.now + 301 * Self.minute, running: [.kimi]))
        #expect(transport.requests.count == 3)
        #expect(transport.requests.last?.header("Authorization") == "Bearer other-account")
        #expect(readings.routes[.kimi]?.status == .ok(Self.now))
        #expect(readings.routes[.kimi]?.snapshot != nil)
    }

    @Test func anExpiredTokenAsksNothingAndKeepsTheLastSnapshot() async throws {
        let home = try Self.kimiHome(expiresS: (Self.now + 10 * Self.minute) / 1000)
        let transport = ScriptedQuotaTransport()
        transport.script(Self.kimiURL, .status(200, Self.kimiBody))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        _ = await service.refresh(Self.round([.kimi], running: [.kimi]))
        let readings = await service.refresh(Self.round([.kimi], at: Self.now + 20 * Self.minute, running: [.kimi]))
        #expect(transport.requests.count == 1)
        #expect(readings.routes[.kimi]?.status == .expired)
        #expect(readings.routes[.kimi]?.signedIn == false)
        #expect(readings.routes[.kimi]?.snapshot?.observedMs == Self.now)
    }

    @Test func aFailureOrARedirectWaitsFifteenMinutes() async throws {
        let home = try Self.kimiHome()
        let transport = ScriptedQuotaTransport()
        transport.script(Self.kimiURL, .failure(.redirected), .status(500, ""), .status(200, "not json"))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        var readings = await service.refresh(Self.round([.kimi], running: [.kimi]))
        #expect(readings.routes[.kimi]?.status == .failed)
        _ = await service.refresh(Self.round([.kimi], at: Self.now + 14 * Self.minute, running: [.kimi]))
        #expect(transport.requests.count == 1)
        readings = await service.refresh(Self.round([.kimi], at: Self.now + 15 * Self.minute, running: [.kimi]))
        #expect(transport.requests.count == 2)
        #expect(readings.routes[.kimi]?.status == .failed)
        readings = await service.refresh(Self.round([.kimi], at: Self.now + 30 * Self.minute, running: [.kimi]))
        #expect(readings.routes[.kimi]?.status == .failed)
        #expect(readings.routes[.kimi]?.snapshot == nil)
    }

    @Test func geminiLearnsItsProjectOncePerToken() async throws {
        let home = try TempDirectory()
        try home.write(".gemini/oauth_creds.json", #"{"access_token":"ya29","refresh_token":"x","expiry_date":1900000000000}"#)
        let transport = ScriptedQuotaTransport()
        transport.script("https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
                         .status(200, #"{"cloudaicompanionProject":"proj-9"}"#))
        transport.script("https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota",
                         .status(200, #"{"buckets":[{"modelId":"gemini-3-pro","remainingFraction":0.75}]}"#))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        let readings = await service.refresh(Self.round([.gemini], running: [.gemini]))
        #expect(readings.routes[.gemini]?.snapshot?.windows.map { $0.label() } == ["Pro 1d"])
        #expect(transport.urls.map { URL(string: $0)!.lastPathComponent }
                == ["v1internal:loadCodeAssist", "v1internal:retrieveUserQuota"])
        #expect(transport.requests.last.flatMap { $0.body.map { String(decoding: $0, as: UTF8.self) } } == #"{"project":"proj-9"}"#)
        _ = await service.refresh(Self.round([.gemini], at: Self.now + 5 * Self.minute, running: [.gemini]))
        #expect(transport.urls.count == 3)
        #expect(transport.urls.last?.hasSuffix("retrieveUserQuota") == true)
    }

    @Test func droidReadsTheEnvironmentThenItsEnvFile() async throws {
        let home = try TempDirectory()
        try home.write(".factory/.env", "OTHER=secret\nexport FACTORY_API_KEY='fk-file'\n")
        try home.write(".factory/settings.json", #"{"apiKey":"must-not-be-read"}"#)
        let transport = ScriptedQuotaTransport()
        transport.script("https://api.factory.ai/api/billing/limits",
                         .status(200, #"{"limits":{"standard":{"weekly":{"usedPercent":55,"secondsRemaining":60}}}}"#))
        let fromFile = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        let readings = await fromFile.refresh(Self.round([.droid], running: [.droid]))
        #expect(transport.requests.last?.header("Authorization") == "Bearer fk-file")
        #expect(readings.routes[.droid]?.snapshot?.windows.first?.resetsAtMs == Self.now + 60_000)
        let fromEnvironment = QuotaVendorService(environment: .test(home: home.url, transport: transport,
                                                                    variables: ["FACTORY_API_KEY": "fk-env"]))
        _ = await fromEnvironment.refresh(Self.round([.droid], running: [.droid]))
        #expect(transport.requests.last?.header("Authorization") == "Bearer fk-env")
    }

    @Test func groksLogAndLiveShareOneSnapshotTheNewerWins() async throws {
        let home = try TempDirectory()
        let line = #"{"ts":"2026-11-10T10:00:00Z","msg":"billing: fetched credits config","ctx":{"config":{"creditUsagePercent":10}}}"#
        try home.write(".grok/logs/unified.jsonl", "noise\n\(line)\n")
        try home.write(".grok/auth.json", #"{"https://grok.com/scope":{"key":"xai-key","expires_at":"2030-01-01T00:00:00Z"},"b":{"key":"second"}}"#)
        let transport = ScriptedQuotaTransport()
        transport.script("https://cli-chat-proxy.grok.com/v1/billing?format=credits",
                         .status(200, #"{"config":{"creditUsagePercent":64}}"#, dateMs: Self.now))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        var readings = await service.refresh(Self.round([.grokBuild], running: [.grokBuild]))
        #expect(transport.requests.isEmpty)
        #expect(readings.snapshot(.grokBuild)?.windows.first?.usedPercent == 10)
        #expect(readings.routes[.grokBuild]?.status == .ok(1_794_304_800_000))
        readings = await service.refresh(Self.round([.grokBuild, .grokBuildLive], running: [.grokBuild]))
        #expect(transport.requests.first?.header("Authorization") == "Bearer xai-key")
        #expect(transport.requests.first?.header("x-xai-token-auth") == "xai-grok-cli")
        #expect(readings.snapshot(.grokBuild)?.windows.first?.usedPercent == 64)
        #expect(readings.snapshot(.grokBuild)?.source == .grokBuild)
    }

    @Test func zcodeAsksItsRegionWithTheSavedKeyOnly() async throws {
        let home = try TempDirectory()
        let transport = ScriptedQuotaTransport()
        transport.script("https://open.bigmodel.cn/api/monitor/usage/quota/limit",
                         .status(200, #"{"success":true,"code":200,"data":{"limits":[]}}"#))
        let none = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        var readings = await none.refresh(Self.round([.zcode], running: [.zcode]))
        #expect(readings.routes[.zcode]?.status == nil)
        #expect(transport.requests.isEmpty)
        let keyed = QuotaVendorService(environment: .test(home: home.url, transport: transport, zcodeKey: "zk.123"))
        readings = await keyed.refresh(Self.round([.zcode], running: [.zcode], region: .bigmodelCn))
        #expect(transport.requests.first?.header("Authorization") == "Bearer zk.123")
        #expect(readings.routes[.zcode]?.status == .ok(Self.now))
        #expect(readings.routes[.zcode]?.snapshot?.hasWindows == false)
        let refused = QuotaVendorService(environment: .test(home: home.url, transport: transport, zcodeKey: "bad\nkey"))
        readings = await refused.refresh(Self.round([.zcode], running: [.zcode]))
        #expect(transport.requests.count == 1)
    }

    @Test func antigravityFallsBackToTheStatusCallAndForgetsARefusedToken() async throws {
        let home = try TempDirectory()
        let transport = ScriptedQuotaTransport()
        let base = "/exa.language_server_pb.LanguageServerService/"
        transport.script("http://127.0.0.1:5000\(base)RetrieveUserQuotaSummary", .failure(.transport("refused")))
        transport.script("http://127.0.0.1:5001\(base)RetrieveUserQuotaSummary", .status(404, ""))
        transport.script("http://127.0.0.1:5001\(base)GetUserStatus", .status(200, """
            {"userStatus":{"cascadeModelConfigData":{"clientModelConfigs":[{"label":"Gemini 3 Pro","quotaInfo":{"remainingFraction":0.6}}]}}}
            """), .status(403, ""))
        let server = AntigravityServer(pid: 77, endpoints: [.init(scheme: "http", port: 5000, token: "t"),
                                                            .init(scheme: "http", port: 5001, token: "t")])
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport, antigravity: [server]))
        var readings = await service.refresh(Self.round([.antigravity]))
        #expect(readings.routes[.antigravity]?.status == .notRunning)
        #expect(transport.requests.isEmpty)
        readings = await service.refresh(Self.round([.antigravity], running: [.antigravity]))
        #expect(readings.antigravityRuns)
        #expect(readings.routes[.antigravity]?.snapshot?.windows.map { $0.label() } == ["Gemini 3 Pro 5h"])
        #expect(readings.routes[.antigravity]?.signedIn == true)
        #expect(transport.requests.allSatisfy { $0.header("X-Codeium-Csrf-Token") == "t" })
        #expect(transport.requests.allSatisfy { $0.timeout == 2 })
        transport.reset()
        readings = await service.refresh(Self.round([.antigravity], at: Self.now + 5 * Self.minute, running: [.antigravity]))
        #expect(readings.routes[.antigravity]?.status == .denied(host: "127.0.0.1"))
        transport.reset()
        readings = await service.refresh(Self.round([.antigravity], at: Self.now + 25 * Self.minute, running: [.antigravity]))
        #expect(!transport.requests.isEmpty)
        #expect(transport.requests.allSatisfy { $0.header("X-Codeium-Csrf-Token") == nil })
        let idle = QuotaVendorService(environment: .test(home: home.url, transport: transport))
        readings = await idle.refresh(Self.round([.antigravity], dashboard: true))
        #expect(readings.routes[.antigravity]?.status == .notRunning)
        #expect(!readings.antigravityRuns)
    }

    @Test func browserSessionsAreReadOnlyWhenARequestIsDue() async throws {
        let home = try TempDirectory()
        let transport = ScriptedQuotaTransport()
        transport.script("https://qoder.com/api/v2/me/usages/big_model_credits",
                         .status(200, #"{"totalQuota":{"quotaSummary":{"usedValue":30,"limitValue":100}}}"#))
        let browser = FixedBrowser(BrowserSession(site: "qoder.com", cookie: "sid=1", userAgent: "Browser/1", browser: .chrome))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport, browser: browser))
        var readings = await service.refresh(Self.round([.qoder]))
        #expect(readings.routes[.qoder]?.status == .notRunning)
        #expect(browser.reads == 0)
        readings = await service.refresh(Self.round([.qoder], running: [.qoderCli]))
        #expect(browser.reads == 1)
        #expect(transport.requests.first?.header("Cookie") == "sid=1")
        #expect(transport.requests.first?.header("User-Agent") == "AgentSpeed/9.9.9 (macOS)")
        #expect(readings.snapshot(.qoderCli)?.source == .qoderCli)
        #expect(readings.snapshot(.qoder)?.windows.first?.kind == .credits)
        #expect(readings.signedIn(.qoderCli))
        _ = await service.refresh(Self.round([.qoder], at: Self.now + Self.minute, running: [.qoder]))
        #expect(browser.reads == 1)
        browser.session = nil
        readings = await service.refresh(Self.round([.qoder], at: Self.now + 5 * Self.minute, running: [.qoder]))
        #expect(readings.routes[.qoder]?.status == .noBrowser)
    }

    @Test func workBuddySendsTheBrowsersUserAgent() async throws {
        let home = try TempDirectory()
        let transport = ScriptedQuotaTransport()
        transport.script("https://www.workbuddy.cn/billing/meter/get-user-resource-summary", .status(200, #"{"code":401}"#))
        let browser = FixedBrowser(BrowserSession(site: "workbuddy.cn", cookie: "wb=1", userAgent: "Mozilla/5.0 Chrome/142", browser: .chrome))
        let service = QuotaVendorService(environment: .test(home: home.url, transport: transport, browser: browser))
        let readings = await service.refresh(Self.round([.workbuddy], running: [.workbuddy]))
        #expect(transport.requests.first?.header("User-Agent") == "Mozilla/5.0 Chrome/142")
        #expect(transport.requests.first?.method == .post)
        #expect(readings.routes[.workbuddy]?.status == .failed)
    }

    @Test func devinsPlanFromItsDatabaseTimedByTheLaunchFolder() async throws {
        let home = try TempDirectory()
        let folder = home.file("Library/Application Support/Devin")
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("logs/20261111T032000"), withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: folder.appendingPathComponent("logs/20261110T090000"), withIntermediateDirectories: true)
        let database = DevinPlanReader.database(folder)
        try FileManager.default.createDirectory(at: database.deletingLastPathComponent(), withIntermediateDirectories: true)
        let db = try SQLiteConnection(path: database.path, mode: .readWriteCreate)
        try db.execute("CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB)")
        try db.execute(#"INSERT INTO ItemTable VALUES('windsurf.reactSettings.cachedPlanInfoData:x', '{"weeklyRemainingPercent":40,"weeklyResetAtUnix":1794800000,"accountIdentityText":"me@example.com"}')"#)
        db.close()
        let service = QuotaVendorService(environment: .test(home: home.url, transport: ScriptedQuotaTransport()))
        let readings = await service.refresh(Self.round([.devin]))
        let snapshot = try #require(readings.snapshot(.devin))
        #expect(snapshot.observedMs == 1_794_338_400_000)
        #expect(snapshot.windows.map(\.usedPercent) == [60])
        #expect(readings.routes[.devin]?.signedIn == true)
        #expect(readings.routes[.devin]?.status == .ok(1_794_338_400_000))
    }
}
