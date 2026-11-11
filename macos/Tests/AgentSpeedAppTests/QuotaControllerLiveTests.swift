import AgentSpeedCore
import AgentSpeedStatusLineKit
import Foundation
import Testing
@testable import AgentSpeed

/// The quota controller running for real against temporary folders: a Claude Desktop cache, a Claude settings file
/// and a data folder (QUOTAS 4.4, 5). The user's own files are never touched.
@Suite("Quota controller, live")
@MainActor
struct QuotaControllerLiveTests {
    final class Folders {
        let root: URL
        init() throws {
            root = FileManager.default.temporaryDirectory.appendingPathComponent("quota-live-\(UUID().uuidString)")
            for folder in ["cache", "claude", "data", "app", "home"] {
                try FileManager.default.createDirectory(at: root.appendingPathComponent(folder), withIntermediateDirectories: true)
            }
        }
        deinit { try? FileManager.default.removeItem(at: root) }
        func url(_ path: String) -> URL { root.appendingPathComponent(path) }
    }

    static func fixture(_ name: String) throws -> Data {
        let root = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
            .deletingLastPathComponent().deletingLastPathComponent()
        return try Data(contentsOf: root.appendingPathComponent("spec/fixtures/quota/\(name)"))
    }

    /// Yields the main actor, so the controller's results arrive; generous, since a busy CI runner starves it.
    static func waitUntil(_ condition: () -> Bool, seconds: Double = 20) async {
        let deadline = Date().addingTimeInterval(seconds)
        while !condition(), Date() < deadline { try? await Task.sleep(for: .milliseconds(50)) }
    }

    /// Answers every vendor request from a script; nothing reaches the network.
    final class Vendors: QuotaHTTPTransport, @unchecked Sendable {
        private let lock = NSLock()
        private var answers: [String: (Int, String)] = [:]
        private(set) var asked: [QuotaHTTPRequest] = []

        func reply(_ url: String, _ status: Int, _ body: String) { lock.withLock { answers[url] = (status, body) } }
        var requests: [QuotaHTTPRequest] { lock.withLock { asked } }

        func send(_ request: QuotaHTTPRequest) async throws(QuotaHTTPError) -> QuotaHTTPResponse {
            let answer = lock.withLock { () -> (Int, String)? in
                asked.append(request)
                return answers[request.url.absoluteString]
            }
            guard let answer else { throw .transport("offline") }
            return QuotaHTTPResponse(status: answer.0, body: Data(answer.1.utf8))
        }
    }

    struct NoBrowser: BrowserSessionReading {
        func session(sites: [String], nowMs: Int64) -> BrowserSession? { nil }
        func resetRefusals() {}
    }

    struct NoServers: AntigravityLocating {
        func servers() -> [AntigravityServer] { [] }
    }

    /// A temporary home, no keychain, no browser, no process listing and a scripted transport.
    /// Counts keychain reads of the ZCode key.
    final class KeyReads: @unchecked Sendable {
        private let lock = NSLock()
        private var reads = 0
        var count: Int { lock.withLock { reads } }
        func read() -> String? {
            lock.withLock { reads += 1 }
            return "zk.1"
        }
    }

    final class CountingBrowser: BrowserSessionReading, @unchecked Sendable {
        private let lock = NSLock()
        private var reads = 0
        var count: Int { lock.withLock { reads } }
        func session(sites: [String], nowMs: Int64) -> BrowserSession? {
            lock.withLock { reads += 1 }
            return BrowserSession(site: "qoder.com", cookie: "sid=1", userAgent: "UA", browser: .chrome)
        }
        func resetRefusals() {}
    }

    static func controller(_ folders: Folders, settings: AppSettings, vendors: Vendors = Vendors(), keys: KeyReads = KeyReads(),
                           browser: CountingBrowser = CountingBrowser()) -> QuotaController {
        let environment = QuotaVendorEnvironment(home: folders.url("home"), variables: [:], transport: vendors, userAgent: "AgentSpeed/test",
                                                 zcodeKey: { keys.read() }, browser: browser, antigravity: NoServers())
        let controller = QuotaController(settings: settings, store: SpeedStore(), traffic: TrafficStore(),
                                         data: folders.url("data"), claudeSettings: folders.url("claude/settings.json"),
                                         desktopFolder: folders.url("cache"), zcodeKeys: MemoryZcodeKeyStore(),
                                         vendorEnvironment: environment,
                                         signInFiles: (folders.url("home/.codex/auth.json"), folders.url("home/.claude.json")))
        controller.helperSource = folders.url("app/agent-speed-statusline")
        return controller
    }

    static func settings() -> AppSettings {
        let suite = "QuotaControllerLiveTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        return AppSettings(defaults: defaults)
    }

    @Test func readsTheDesktopCacheAndRunsTheBridge() async throws {
        let folders = try Folders()
        try Self.fixture("zstd-ultra.bin").write(to: folders.url("cache/383706cf5aea4bcc_0"))
        try Data("#!/bin/sh\nexit 0\n".utf8).write(to: folders.url("app/agent-speed-statusline"))
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: folders.url("app/agent-speed-statusline").path)
        let original = "{\n  \"model\": \"opus\"\n}\n"
        try Data(original.utf8).write(to: folders.url("claude/settings.json"))
        let settings = Self.settings()
        let quota = Self.controller(folders, settings: settings)
        quota.activate()
        defer { quota.shutdown() }

        await Self.waitUntil { quota.desktop != nil }
        #expect(quota.desktop?.windows.map { $0.label() } == ["7d", "Fable 7d"])

        var connected: Bool?
        quota.turnOnCli { connected = $0 }
        await Self.waitUntil { connected != nil }
        #expect(connected == true)
        #expect(settings.quotaClaudeCli)
        let on = try String(contentsOf: folders.url("claude/settings.json"), encoding: .utf8)
        #expect(on.contains("agent-speed-statusline"))
        #expect(FileManager.default.isExecutableFile(atPath: folders.url("data/bin/agent-speed-statusline").path))
        quota.refreshBridgeStatus()
        await Self.waitUntil { quota.bridgeStatus == .waiting }
        #expect(quota.bridgeStatus == .waiting)

        var stored = StatusLineQuota()
        stored.merge(rateLimits: ["seven_day": ["used_percentage": 41, "resets_at": Date().timeIntervalSince1970 + 3600]],
                     nowMs: QuotaController.nowMs())
        StatusLineHelper.writePrivate(stored.data(), to: folders.url("data/quota/claude-cli.json"))
        await Self.waitUntil({ quota.cli != nil }, seconds: 8)
        #expect(quota.cli?.windows.map(\.usedPercent) == [41])

        quota.turnOffCli()
        await Self.waitUntil { (try? String(contentsOf: folders.url("claude/settings.json"), encoding: .utf8)) == original }
        #expect(try String(contentsOf: folders.url("claude/settings.json"), encoding: .utf8) == original)
        #expect(!settings.quotaClaudeCli && quota.cli == nil)
    }

    @Test func anUnreadableSettingsFileKeepsTheSwitchOff() async throws {
        let folders = try Folders()
        try Data("#!/bin/sh\n".utf8).write(to: folders.url("app/agent-speed-statusline"))
        try Data("[1, 2]\n".utf8).write(to: folders.url("claude/settings.json"))
        let settings = Self.settings()
        let quota = Self.controller(folders, settings: settings)
        var connected: Bool?
        quota.turnOnCli { connected = $0 }
        await Self.waitUntil { connected != nil }
        #expect(connected == false)
        #expect(!settings.quotaClaudeCli)
        #expect(quota.bridgeFailure == .unreadable)
        #expect(try String(contentsOf: folders.url("claude/settings.json"), encoding: .utf8) == "[1, 2]\n")
    }

    @Test func asksAVendorWhileTheDashboardIsOpen() async throws {
        let folders = try Folders()
        let credentials = folders.url("home/.kimi-code/credentials")
        try FileManager.default.createDirectory(at: credentials, withIntermediateDirectories: true)
        try Data(#"{"access_token":"t-1","expires_at":1900000000}"#.utf8).write(to: credentials.appendingPathComponent("kimi-code.json"))
        let vendors = Vendors()
        vendors.reply("https://api.kimi.com/coding/v1/usages", 200, #"{"usages":{"limit_7d":{"used_ratio":0.5}}}"#)
        let settings = Self.settings()
        let quota = Self.controller(folders, settings: settings, vendors: vendors)
        quota.activate()
        defer { quota.shutdown() }
        await Self.waitUntil { quota.vendor.routes[.kimi]?.status == .notRunning }
        #expect(vendors.requests.isEmpty)
        quota.dashboardOpen = true
        await Self.waitUntil { quota.lines(.kimi) != nil }
        #expect(quota.lines(.kimi)?.windows.map { $0.label() } == ["7d"])
        #expect(vendors.requests.count == 1)
        #expect(vendors.requests.first?.header("Authorization") == "Bearer t-1")
        #expect(quota.zcodeKeySaved == false)
    }

    /// QUOTAS 9.1: a disabled source reads no quota on any route and shows none; no credential is read for it.
    @Test func aDisabledSourceReadsNothingAndShowsNothing() async throws {
        let folders = try Folders()
        let credentials = folders.url("home/.kimi-code/credentials")
        try FileManager.default.createDirectory(at: credentials, withIntermediateDirectories: true)
        try Data(#"{"access_token":"t-1","expires_at":1900000000}"#.utf8).write(to: credentials.appendingPathComponent("kimi-code.json"))
        try FileManager.default.createDirectory(at: folders.url("home/.codex"), withIntermediateDirectories: true)
        try Data(#"{"auth_mode":"chatgpt"}"#.utf8).write(to: folders.url("home/.codex/auth.json"))
        try Self.fixture("zstd-ultra.bin").write(to: folders.url("cache/383706cf5aea4bcc_0"))
        let vendors = Vendors()
        vendors.reply("https://api.kimi.com/coding/v1/usages", 200, #"{"usages":{"limit_7d":{"used_ratio":0.5}}}"#)
        vendors.reply("https://api.z.ai/api/monitor/usage/quota/limit", 200, #"{"success":true,"code":200,"data":{"limits":[]}}"#)
        vendors.reply("https://qoder.com/api/v2/me/usages/big_model_credits", 200, #"{"totalQuota":{"quotaSummary":{"usagePercentage":5}}}"#)
        let settings = Self.settings()
        settings.quotaZcode = true
        settings.quotaQoder = true
        for source in [SourceKind.kimi, .zcode, .qoder, .qoderCli, .codex, .claudeDesktop] { settings.setEnabled(source, false) }
        let keys = KeyReads()
        let browser = CountingBrowser()
        let quota = Self.controller(folders, settings: settings, vendors: vendors, keys: keys, browser: browser)
        quota.activate()
        defer { quota.shutdown() }
        quota.dashboardOpen = true
        try? await Task.sleep(for: .milliseconds(600))
        #expect(vendors.requests.isEmpty)
        #expect(keys.count == 0)
        #expect(browser.count == 0)
        #expect(quota.desktop == nil && quota.lines(.claudeDesktop) == nil)
        #expect(!quota.signedIn.contains(.codex))
        #expect(quota.lines(.kimi) == nil && quota.vendor.routes[.kimi] == nil)
        #expect(quota.status(.kimi) == nil && quota.status(.zcode) == nil && quota.status(.qoder) == nil)

        // Turned on again, each route reads and asks.
        settings.setEnabled(.kimi, true)
        settings.setEnabled(.zcode, true)
        settings.setEnabled(.qoderCli, true)
        settings.setEnabled(.codex, true)
        settings.setEnabled(.claudeDesktop, true)
        await Self.waitUntil { quota.lines(.kimi) != nil && quota.lines(.qoderCli) != nil && quota.desktop != nil }
        #expect(vendors.requests.count == 3)
        #expect(keys.count == 1 && browser.count == 1)
        #expect(quota.lines(.qoder) == nil)
        await Self.waitUntil { quota.signedIn.contains(.codex) }
        #expect(quota.signedIn.contains(.codex))
    }
}
