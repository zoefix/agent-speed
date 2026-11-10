import Foundation
import Testing
@testable import AgentSpeedCore

/// The relay quota protocol of REMOTE 11 on the app side, with scripted relay lines.
@Suite("Remote quotas")
struct RemoteQuotaTests {
    static let hostId = RemoteConnectionHarness.hostId
    let decoder = RelayLineDecoder(hostId: Self.hostId)

    static func quotaLine(source: String = "kimi", observed: Int64 = RelayLines.baseMs, status: String = "ok",
                          windows: [[String: Any]] = [window()], running: Bool = true, signedIn: Bool = true) -> String {
        RelayLines.json(["t": "quota", "q": ["source": source, "observedMs": observed, "windows": windows, "running": running,
                                             "signedIn": signedIn, "status": status] as [String: Any]])
    }

    static func window(kind: String = "fiveHour", name: Any = NSNull(), minutes: Any = 300, used: Any = 25.5,
                       resets: Any = RelayLines.baseMs + 3_600_000) -> [String: Any] {
        ["kind": kind, "name": name, "minutes": minutes, "usedPercent": used, "resetsAtMs": resets]
    }

    func decode(_ line: String) -> RelayLineDecoder.Outcome { decoder.decode(Data(line.utf8)) }

    /// The invalid field's last path component.
    func invalidField(_ line: String) -> String? {
        if case .invalid(let error) = decode(line) {
            return error.field.hasPrefix("q.windows.") ? String(error.field.split(separator: ".").last ?? "") : error.field
        }
        return nil
    }

    // MARK: Decoding and validation (REMOTE 11.1, 5.6)

    @Test func aQuotaLine() throws {
        let line = Self.quotaLine(windows: [Self.window(), Self.window(kind: "sevenDay", name: "Gemini", minutes: NSNull(), used: 103,
                                                                        resets: NSNull()),
                                            Self.window(kind: "other", minutes: 1440, used: 0)])
        guard case .message(.quota(let report)) = decode(line) else {
            Issue.record("not a quota line: \(decode(line))")
            return
        }
        #expect(report.hostId == Self.hostId)
        #expect(report.source == .kimi)
        #expect(report.running && report.signedIn)
        #expect(report.status == .ok(RelayLines.baseMs))
        #expect(report.snapshot.windows.map { $0.label() } == ["5h", "1d", "Gemini 7d"])
        #expect(report.snapshot.windows.last?.minutes == 10080)
        #expect(report.snapshot.windows.last?.resetsAtMs == nil)
        guard case .message(.quota(let denied)) = decode(Self.quotaLine(source: "droid", status: "denied", windows: [])) else {
            Issue.record("denied")
            return
        }
        #expect(denied.status == .denied(host: "api.factory.ai"))
        #expect(!denied.snapshot.hasWindows)
    }

    @Test func everyRuleMakesTheLineInvalid() {
        let tooMany = (0..<17).map { _ in Self.window() }
        #expect(invalidField(Self.quotaLine(windows: tooMany)) == "q.windows")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(kind: "hourly")])) == "kind")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(name: String(repeating: "n", count: 65))])) == "name")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(name: "Gem\u{202E}ini")])) != nil)
        #expect(invalidField(Self.quotaLine(windows: [Self.window(name: "a\nb")])) != nil)
        #expect(invalidField(Self.quotaLine(windows: [Self.window(minutes: 0)])) == "minutes")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(minutes: 527_041)])) == "minutes")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(kind: "other", minutes: NSNull())])) == "minutes")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(used: 1000.5)])) == "usedPercent")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(used: -1)])) == "usedPercent")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(used: "12")])) == "usedPercent")
        #expect(invalidField(Self.quotaLine(windows: [Self.window(resets: 1_000)])) == "resetsAtMs")
        #expect(invalidField(Self.quotaLine(observed: 5_000_000_000_000)) == "q.observedMs")
        #expect(invalidField(Self.quotaLine(status: "great")) == "q.status")
        #expect(invalidField(#"{"t":"quota","q":{"source":"kimi","observedMs":1780000000000,"windows":[],"status":"ok"}}"#) == "q.running")
        #expect(invalidField(#"{"t":"quota"}"#) == "q")
        // Exactly the limits are fine.
        let edge = Self.quotaLine(windows: (0..<16).map { _ in Self.window(name: String(repeating: "名", count: 64), minutes: 527_040, used: 1000) })
        if case .message = decode(edge) {} else { Issue.record("edge: \(decode(edge))") }
    }

    @Test func sourcesAHostCannotReadAreIgnored() {
        #expect(decode(Self.quotaLine(source: "claudeDesktop")) == .ignored)
        #expect(decode(Self.quotaLine(source: "zcode")) == .ignored)
        #expect(decode(Self.quotaLine(source: "someday")) == .ignored)
        #expect(decode(#"{"t":"quotaRemoved","source":"qoder"}"#) == .ignored)
    }

    @Test func removedAndBridgeLines() {
        #expect(decode(#"{"t":"quotaRemoved","source":"gemini"}"#) == .message(.quotaRemoved(hostId: Self.hostId, source: .gemini)))
        #expect(invalidField(#"{"t":"quotaRemoved"}"#) == "source")
        #expect(decode(#"{"t":"quotaBridge","state":"waiting","settingsPath":"~/.claude/settings.json"}"#)
                == .message(.quotaBridge(RemoteQuotaBridge(hostId: Self.hostId, state: .waiting, settingsPath: "~/.claude/settings.json"))))
        #expect(invalidField(#"{"t":"quotaBridge","state":"broken","settingsPath":"~"}"#) == "state")
        #expect(invalidField(#"{"t":"quotaBridge","state":"ok","settingsPath":"~/‮"}"#) == "settingsPath")
        #expect(invalidField(#"{"t":"quotaBridge","state":"ok"}"#) == "settingsPath")
    }

    // MARK: Encoding (REMOTE 11.1)

    @Test func startAndConfigLines() throws {
        let config = RemoteQuotaConfig(sources: [.kimi, .codex, .zcode, .claudeDesktop], network: true, grokLive: false, claudeBridge: true)
        #expect(config.sources == [.codex, .kimi])
        let line = try #require(RelayLineEncoder.quota(config))
        #expect(String(decoding: line, as: UTF8.self)
                == #"{"t":"quota","config":{"sources":["codex","kimi"],"network":true,"grokLive":false,"claudeBridge":true}}"# + "\n")
        let zone = RemoteZone.current(TimeZone(identifier: "UTC")!)
        let prices = RemotePriceTableJSON(Data(#"{"schema":3}"#.utf8))!
        let start = try JSONParser.parse(try #require(RelayLineEncoder.start(todayStartMs: 1, sources: [.codex], zone: zone,
                                                                              prices: prices, quota: config)).dropLast())
        #expect(start["quota"]?["claudeBridge"]?.bool == true)
        #expect(start["quota"]?["sources"]?.array?.compactMap(\.string) == ["codex", "kimi"])
        let plain = try JSONParser.parse(try #require(RelayLineEncoder.start(todayStartMs: 1, sources: [.codex], zone: zone,
                                                                              prices: prices)).dropLast())
        #expect(plain["quota"] == nil)
    }

    @Test func eachHostAddsItsOwnBridge() {
        let switches = RemoteQuotaSwitches(sources: [.codex, .claudeCode, .grokBuild, .kimi, .zcode, .devin], grokLive: true)
        let plain = RemoteHost(id: Self.hostId, destination: "devbox")
        var bridged = plain
        bridged.quotaClaudeCli = true
        let off = RemoteQuotaConfig.forHost(plain, switches: switches, networkEnabled: true)
        #expect(off.sources == [.codex, .grokBuild, .kimi])
        #expect(off.grokLive && !off.claudeBridge)
        let on = RemoteQuotaConfig.forHost(bridged, switches: switches, networkEnabled: false)
        #expect(on.sources == [.codex, .claudeCode, .grokBuild, .kimi])
        #expect(on.claudeBridge && !on.network)
        let noGrok = RemoteQuotaConfig.forHost(plain, switches: RemoteQuotaSwitches(sources: [.codex], grokLive: true), networkEnabled: true)
        #expect(!noGrok.grokLive)
        // A disabled source reads nothing on the host; the bridge stays the host's switch (QUOTAS 9.1).
        let disabled = RemoteQuotaConfig.forHost(bridged, switches: switches, networkEnabled: true, enabled: [.codex, .claudeCode])
        #expect(disabled.sources == [.codex, .claudeCode])
        var noClaude = switches
        noClaude.claudeCode = false
        let claudeOff = RemoteQuotaConfig.forHost(bridged, switches: noClaude, networkEnabled: true)
        #expect(!claudeOff.sources.contains(.claudeCode) && claudeOff.claudeBridge)
    }

    @Test func theHubSendsOnlyEnabledSources() throws {
        let harness = try RemoteHubTests.Harness()
        let host = RemoteHost(id: RemoteHubTests.a, destination: "alpha")
        harness.hub.update(hosts: [host], networkEnabled: true, enabledSources: [.codex], priceTable: RemoteHubTests.prices,
                           quota: RemoteQuotaSwitches(sources: [.codex, .kimi, .gemini]))
        harness.settle()
        let run = try #require(harness.runs("alpha").first)
        #expect(run.writtenLines.first?["quota"]?["sources"]?.array?.compactMap(\.string) == ["codex"])
    }

    @Test func theHostFieldRoundTripsAndIsLeftOutWhileOff() throws {
        let data = Data(#"{"version":1,"hosts":[{"id":"\#(Self.hostId)","destination":"devbox","quotaClaudeCli":true,"future":1}]}"#.utf8)
        let document = try #require(RemoteHostsDocument(data: data))
        #expect(document.hosts.first?.quotaClaudeCli == true)
        let written = try #require(try JSONParser.parse(document.data())["hosts"]?.array?.first)
        #expect(written["quotaClaudeCli"]?.bool == true)
        #expect(written["future"]?.int64 == 1)
        var host = try #require(document.hosts.first)
        host.quotaClaudeCli = false
        let off = RemoteHostsDocument(hosts: [host])
        #expect(try JSONParser.parse(off.data())["hosts"]?.array?.first?["quotaClaudeCli"] == nil)
        #expect(RemoteHostsDocument(data: Data(#"{"hosts":[{"id":"\#(Self.hostId)","destination":"d"}]}"#.utf8))?.hosts.first?.quotaClaudeCli == false)
    }

    // MARK: A connection (REMOTE 11.1, 11.3, 6)

    @Test func theConfigGoesWithStartThenOnlyWhenItChanges() throws {
        let config = RemoteQuotaConfig(sources: [.codex], network: true, grokLive: false, claudeBridge: false)
        let harness = try RemoteConnectionHarness(quota: config)
        let run = try harness.connect()
        #expect(run.writtenLines.first?["quota"]?["sources"]?.array?.compactMap(\.string) == ["codex"])
        harness.connection.updateQuota(config)
        harness.settle()
        #expect(!run.writtenTypes.contains("quota"))
        var next = config
        next.sources.insert(.kimi)
        harness.connection.updateQuota(next)
        harness.settle()
        let line = try #require(run.writtenLines.last)
        #expect(line["t"]?.string == "quota")
        #expect(line["config"]?["sources"]?.array?.compactMap(\.string) == ["codex", "kimi"])
        #expect(harness.transport.runs.count == 1)
    }

    @Test func quotaTimesBecomeLocalLikeEveryOtherTime() throws {
        let harness = try RemoteConnectionHarness()
        harness.remoteOffsetMs = 2_000
        let run = try harness.connect()
        let observed = harness.nowMs + 2_000
        run.send(Self.quotaLine(observed: observed, windows: [Self.window(resets: observed + 60_000)]))
        run.send(#"{"t":"quotaBridge","state":"ok","settingsPath":"~/.claude/settings.json"}"#)
        run.send(#"{"t":"quotaRemoved","source":"kimi"}"#)
        harness.settle()
        let quota = harness.events.messages.compactMap { message, _ -> RemoteQuotaReport? in
            if case .quota(let report) = message { return report }
            return nil
        }
        #expect(quota.first?.snapshot.observedMs == observed - 2_000)
        #expect(quota.first?.snapshot.windows.first?.resetsAtMs == observed + 58_000)
        #expect(quota.first?.status == .ok(observed - 2_000))
        let kinds = harness.events.messages.map { "\($0.0)".prefix(10) }
        #expect(kinds.contains { $0.hasPrefix("quotaBridg") })
        #expect(kinds.contains { $0.hasPrefix("quotaRemov") })
        // An invalid quota line counts like any invalid line.
        run.send(Self.quotaLine(windows: [Self.window(kind: "nope")]))
        harness.settle()
        #expect(harness.connection.sync { harness.connection.session.invalidLines } == 1)
    }

    @Test func retiringRestoresTheBridgeBeforeStopping() throws {
        let config = RemoteQuotaConfig(sources: [.codex, .claudeCode], network: true, grokLive: false, claudeBridge: true)
        let harness = try RemoteConnectionHarness(quota: config)
        let run = try harness.connect()
        harness.connection.retire()
        harness.settle()
        let line = try #require(run.writtenLines.last)
        #expect(line["t"]?.string == "quota")
        #expect(line["config"]?["claudeBridge"]?.bool == false)
        #expect(line["config"]?["sources"]?.array?.compactMap(\.string) == ["codex"])
        #expect(!run.isInputClosed)
        run.send(#"{"t":"quotaBridge","state":"off","settingsPath":"~/.claude/settings.json"}"#)
        harness.settle()
        #expect(run.isInputClosed && run.wasTerminated)

        let silent = try RemoteConnectionHarness(quota: config)
        let quiet = try silent.connect()
        silent.connection.retire()
        silent.advance(9)
        #expect(!quiet.isInputClosed)
        silent.advance(2)
        #expect(quiet.isInputClosed)
    }

    @Test func aHubSendsEachHostItsConfigAndRetiresARemovedBridge() throws {
        let harness = try RemoteHubTests.Harness()
        var host = RemoteHost(id: RemoteHubTests.a, destination: "alpha")
        host.quotaClaudeCli = true
        let switches = RemoteQuotaSwitches(sources: [.codex, .kimi], grokLive: false)
        harness.hub.update(hosts: [host], networkEnabled: true, enabledSources: [.codex, .claudeCode, .kimi],
                           priceTable: RemoteHubTests.prices, quota: switches)
        harness.settle()
        let run = try #require(harness.runs("alpha").first)
        #expect(run.writtenLines.first?["quota"]?["sources"]?.array?.compactMap(\.string) == ["codex", "claudeCode", "kimi"])
        run.send(RelayLines.hello())
        run.send(RelayLines.ready)
        harness.settle()
        harness.hub.update(hosts: [host], networkEnabled: true, enabledSources: [.codex, .claudeCode, .kimi],
                           priceTable: RemoteHubTests.prices, quota: RemoteQuotaSwitches(sources: [.codex], grokLive: false))
        harness.settle()
        #expect(harness.runs("alpha").count == 1)
        #expect(run.writtenLines.last?["config"]?["sources"]?.array?.compactMap(\.string) == ["codex", "claudeCode"])
        harness.hub.update(hosts: [], networkEnabled: true, enabledSources: [.codex, .claudeCode, .kimi],
                           priceTable: RemoteHubTests.prices)
        #expect(harness.hub.retiringHostIds == [RemoteHubTests.a])
        #expect(harness.hub.connectedHostIds.isEmpty)
        harness.hub.retiringConnection(RemoteHubTests.a)?.sync {}
        #expect(run.writtenLines.last?["config"]?["claudeBridge"]?.bool == false)
        harness.hub.stopAll()
        #expect(harness.hub.retiringHostIds.isEmpty)
    }
}
