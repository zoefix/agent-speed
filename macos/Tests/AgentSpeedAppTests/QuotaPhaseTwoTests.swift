import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeed

/// The phase 2 switches, snapshots, status lines and the remote hosts' quotas as the app shows them (QUOTAS 9;
/// DESIGN 5 *額度, phase 2*, 13 *Host quotas*).
@Suite("Quota phase 2")
@MainActor
struct QuotaPhaseTwoTests {
    static let now: Int64 = 1_794_338_773_000
    static let hostId = "8C0D6B0A-1111-4222-8333-944455556666"

    private static func model(_ language: LanguagePreference = .zhHant) -> AppModel {
        let suite = "QuotaPhaseTwoTests.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        let model = AppModel(settings: AppSettings(defaults: defaults))
        model.settings.language = language
        model.l10n.apply(language)
        return model
    }

    static func kimi(observedAgo: Int64 = 60_000) -> QuotaSnapshot {
        QuotaSnapshot(source: .kimi, observedMs: now - observedAgo, windows: [
            QuotaWindow(kind: .month, usedPercent: 12.5, resetsAtMs: now + 20 * 86_400_000),
            QuotaWindow(kind: .fiveHour, usedPercent: 25, resetsAtMs: now + 3_600_000),
            QuotaWindow(kind: .sevenDay, usedPercent: 50, resetsAtMs: now + 2 * 86_400_000),
        ])
    }

    static let qoder = QuotaSnapshot(source: .qoder, observedMs: now - 60_000, windows: [
        QuotaWindow(kind: .credits, usedPercent: 25, resetsAtMs: nil),
    ])

    @Test func switchesStartAsSpecifiedAndPersist() {
        let suite = "QuotaPhaseTwoTests.defaults.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suite)!
        defaults.removePersistentDomain(forName: suite)
        let settings = AppSettings(defaults: defaults)
        #expect(settings.quotaRoutes == [.grokBuild, .devin, .antigravity, .kimi, .gemini, .droid])
        #expect(!settings.quotaGrokBuildLive && !settings.quotaZcode && !settings.quotaQoder && !settings.quotaWorkBuddy)
        #expect(settings.quotaZcodeRegion == .global)
        settings.quotaGrokBuildLive = true
        settings.quotaZcodeRegion = .bigmodelCn
        settings.quotaQoder = true
        #expect(defaults.string(forKey: "quotaZcodeRegion") == "bigmodel-cn")
        let reloaded = AppSettings(defaults: defaults)
        #expect(reloaded.quotaRoutes.isSuperset(of: [.grokBuildLive, .qoder]))
        #expect(reloaded.showsQuota(.qoderCli) && reloaded.showsQuota(.qoder))
        reloaded.quotaGrokBuild = false
        #expect(!reloaded.quotaRoutes.contains(.grokBuildLive))
        #expect(reloaded.remoteQuotaSwitches.sources == [.codex, .antigravity, .kimi, .gemini, .droid])
        #expect(!reloaded.remoteQuotaSwitches.grokLive)
        #expect(defaults.dictionaryRepresentation().keys.allSatisfy { !$0.lowercased().contains("zcodekey") })
    }

    @Test func vendorSnapshotsFollowTheirSwitchAndQoderServesBothRows() {
        let model = Self.model()
        model.settings.quotaQoder = true
        let readings = QuotaVendorReadings(routes: [
            .kimi: QuotaRouteReading(snapshot: Self.kimi(), status: .ok(Self.now - 60_000), signedIn: true),
            .qoder: QuotaRouteReading(snapshot: Self.qoder, status: .ok(Self.now - 60_000), signedIn: true),
        ])
        let apps = QuotaApps(agents: [.kimi, .qoder, .qoderCli])
        model.quota.preview(desktop: nil, cli: nil, apps: apps, vendor: readings, nowMs: Self.now)
        #expect(model.quota.lines(.kimi)?.windows.map { $0.label(QuotaWindow.Words(month: "月", credits: "點數")) }
                == ["5h", "7d", "月"])
        #expect(model.quota.listedSources == [.kimi, .qoder, .qoderCli])
        #expect(model.quota.lines(.qoderCli)?.source == .qoderCli)
        #expect(model.quota.cardSnapshots(within: nil).map(\.source) == [.kimi, .qoder])
        #expect(model.quota.cardSnapshots(within: SourceSet(.qoderCli)).map(\.source) == [.qoderCli])
        model.settings.quotaKimi = false
        #expect(model.quota.lines(.kimi) == nil)
        #expect(model.quotaLines(.qoder).map(\.label) == ["點數"])
        #expect(model.quotaLines(.qoder).first?.pace == nil)
    }

    @Test func statusLinesSayWhatEachStateMeans() {
        let model = Self.model(.en)
        model.quota.preview(desktop: nil, cli: nil, nowMs: Self.now)
        func text(_ status: QuotaRouteStatus, _ route: QuotaRoute) -> String {
            QuotaStatusText.text(status, route: route, model: model)
        }
        #expect(text(.noCredential, .kimi) == "No sign-in found")
        #expect(text(.expired, .kimi) == "The sign-in has expired; waiting for Kimi Code CLI to renew it")
        #expect(text(.denied(host: "api.kimi.com"), .kimi) == "api.kimi.com refused the request; trying again once the sign-in changes")
        #expect(text(.noBrowser, .qoder) == "No Qoder sign-in found in your browsers")
        #expect(text(.notRunning, .qoder) == "Qoder is not running")
        #expect(text(.notRunning, .antigravity) == "Antigravity is not running")
        #expect(text(.networkOff, .gemini) == "Network is off (About → Network)")
        #expect(text(.failed, .droid) == "The last request failed; trying again later")
        #expect(text(.ok(Self.now - 60_000), .devin).hasPrefix("Updated "))
        #expect(QuotaRouteStatus.failed.isProblem && !QuotaRouteStatus.notRunning.isProblem)
        model.settings.quotaKimi = true
        model.settings.networkEnabled = false
        #expect(model.quota.status(.kimi) == .networkOff)
        #expect(model.quota.status(.zcode) == nil)
    }

    @Test func aPastedZcodeKeyIsCleanedAndNeverStoredInSettings() {
        #expect(ZcodeKey.cleaned("  abc.123\n") == "abc.123")
        #expect(ZcodeKey.cleaned("   ") == nil)
        #expect(ZcodeKey.cleaned("a b") == nil)
        let store = MemoryZcodeKeyStore()
        #expect(store.save("k") && store.hasKey() && store.read() == "k")
        store.remove()
        #expect(!store.hasKey())
        let model = Self.model()
        #expect(model.quota.saveZcodeKey(" key-1 "))
        #expect(model.quota.zcodeKeySaved == true)
        #expect(!model.quota.saveZcodeKey("bad key"))
        model.quota.removeZcodeKey()
        #expect(model.quota.zcodeKeySaved == false)
    }

    // MARK: Hosts (REMOTE 11, DESIGN 13)

    private static func hostModel() -> AppModel {
        let model = Self.model()
        var host = RemoteHost(id: hostId, destination: "devbox")
        host.quotaClaudeCli = true
        model.settings.remoteHosts = RemoteHostsDocument(hosts: [host])
        model.quota.preview(desktop: nil, cli: nil, nowMs: now)
        return model
    }

    static func report(_ snapshot: QuotaSnapshot, running: Bool = true, signedIn: Bool = true) -> RelayMessage {
        .quota(RemoteQuotaReport(hostId: hostId, snapshot: snapshot, running: running, signedIn: signedIn, status: .ok(snapshot.observedMs)))
    }

    @Test func hostLinesFollowTheRelayAndTheSwitches() {
        let model = Self.hostModel()
        let codex = QuotaSnapshot(source: .codex, observedMs: Self.now - 60_000, windows: [
            QuotaWindow(kind: .sevenDay, usedPercent: 40, resetsAtMs: Self.now + 86_400_000 * 3),
        ])
        let claude = QuotaSnapshot(source: .claudeCode, observedMs: Self.now - 30_000, windows: [
            QuotaWindow(kind: .fiveHour, usedPercent: 6, resetsAtMs: Self.now + 3_600_000),
        ])
        model.quota.applyRemote(Self.report(Self.kimi()), hostId: Self.hostId)
        model.quota.applyRemote(Self.report(codex, running: false), hostId: Self.hostId)
        model.quota.applyRemote(Self.report(claude), hostId: Self.hostId)
        model.quota.applyRemote(.quotaBridge(RemoteQuotaBridge(hostId: Self.hostId, state: .ok, settingsPath: "~/.claude/settings.json")),
                                hostId: Self.hostId)
        model.quota.applyRemote(Self.report(Self.kimi()), hostId: "unknown")
        #expect(model.quota.hostSnapshots(Self.hostId).map(\.source) == [.codex, .claudeCode, .kimi])
        #expect(model.hostQuotaLines(Self.hostId).first?.lines.map(\.label) == ["7d"])
        #expect(model.quota.hostQuotas["unknown"] == nil)
        #expect(model.quota.hostQuotas[Self.hostId]?.bridge?.state == .ok)

        let items = model.quota.cardItems(within: nil, hosts: nil, connected: [Self.hostId])
        #expect(items.map(\.source) == [.claudeCode, .kimi])
        #expect(items.allSatisfy { $0.hostName == "devbox" })
        #expect(Set(items.map(\.key)).count == items.count)
        let drawn = CharacterQuotaText.sources(items, nowMs: Self.now, l10n: model.l10n).first
        #expect(drawn?.name == "Claude Code" && drawn?.host == "devbox")
        #expect(CharacterQuotaText.sentence(items, nowMs: Self.now, l10n: model.l10n)?.contains("Claude Code · devbox") == true)
        #expect(model.quota.cardItems(within: nil, hosts: nil, connected: []).isEmpty)
        #expect(model.quota.cardItems(within: nil, hosts: [RemoteHost.localId], connected: [Self.hostId]).isEmpty)

        model.quota.applyRemote(.quotaRemoved(hostId: Self.hostId, source: .kimi), hostId: Self.hostId)
        #expect(model.quota.hostSnapshots(Self.hostId).map(\.source) == [.codex, .claudeCode])
        model.settings.quotaCodex = false
        model.quota.pruneHostQuotas(model.settings.remoteHosts, switches: model.settings.remoteQuotaSwitches)
        #expect(model.quota.hostSnapshots(Self.hostId).map(\.source) == [.claudeCode])
        model.settings.remoteHosts = .empty
        model.quota.pruneHostQuotas(model.settings.remoteHosts, switches: model.settings.remoteQuotaSwitches)
        #expect(model.quota.hostQuotas.isEmpty)
    }

    @Test func theEditorKeepsTheBridgeSwitch() {
        var host = RemoteHost(id: Self.hostId, destination: "devbox")
        host.quotaClaudeCli = true
        var draft = RemoteHostDraft(editing: host)
        #expect(draft.quotaClaudeCli)
        draft.name = "box"
        #expect(draft.host().quotaClaudeCli)
        draft.quotaClaudeCli = false
        #expect(!draft.host().quotaClaudeCli)
        #expect(!RemoteHostDraft().host().quotaClaudeCli)
    }
}
