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
            for folder in ["cache", "claude", "data", "app"] {
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

    /// Yields the main actor, so the controller's results arrive.
    static func waitUntil(_ condition: () -> Bool, seconds: Double = 5) async {
        let deadline = Date().addingTimeInterval(seconds)
        while !condition(), Date() < deadline { try? await Task.sleep(for: .milliseconds(50)) }
    }

    static func controller(_ folders: Folders, settings: AppSettings) -> QuotaController {
        let controller = QuotaController(settings: settings, store: SpeedStore(), traffic: TrafficStore(),
                                         data: folders.url("data"), claudeSettings: folders.url("claude/settings.json"),
                                         desktopFolder: folders.url("cache"))
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
}
