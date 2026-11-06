import Foundation
import Testing
@testable import AgentSpeedCore

@Suite("Remote protocol encoding")
struct RemoteEncoderTests {
    static let prices = RemotePriceTableJSON(Data(#"{ "schema": 3, "models": [ {"id": "a b", "price": 0.20000000000000001} ] }"#.utf8))!

    func object(_ line: Data?) throws -> [String: JSONValue] {
        let line = try #require(line)
        #expect(line.last == UInt8(ascii: "\n"))
        #expect(line.dropLast().contains(UInt8(ascii: "\n")) == false)
        return try #require(try JSONParser.parse(line.dropLast()).object)
    }

    @Test func priceTablesAreCompactWithLiteralsKept() {
        #expect(Self.prices.text == #"{"schema":3,"models":[{"id":"a b","price":0.20000000000000001}]}"#)
        #expect(RemotePriceTableJSON(Data("[1]".utf8)) == nil)
        #expect(RemotePriceTableJSON(Data("{".utf8)) == nil)
        let escaped = RemotePriceTableJSON(Data(#"{"k":"q\" \\ x"}"#.utf8))
        #expect(escaped?.text == #"{"k":"q\" \\ x"}"#)
        let bundled = RemotePriceTableJSON(Data(BundledPriceAssets.prices.utf8))
        #expect(bundled != nil)
        #expect(bundled?.text.contains("\n") == false)
        #expect((try? JSONParser.parse(bundled?.text ?? "")) == (try? JSONParser.parse(BundledPriceAssets.prices)))
    }

    @Test func startCarriesSourcesZoneAndPrices() throws {
        let zone = RemoteZone.current(TimeZone(identifier: "Asia/Taipei")!)
        let start = try object(RelayLineEncoder.start(todayStartMs: 1_780_000_000_000, sources: [.claudeCode, .codex],
                                                      zone: zone, prices: Self.prices))
        #expect(start["t"]?.string == "start")
        #expect(start["protocol"]?.int64 == 1)
        #expect(start["todayStartMs"]?.int64 == 1_780_000_000_000)
        #expect(start["sources"]?.array?.compactMap(\.string) == ["codex", "claudeCode"])
        #expect(start["zone"]?["id"]?.string == "Asia/Taipei")
        let tzif = try #require(Data(base64Encoded: start["zone"]?["tzif"]?.string ?? ""))
        #expect(tzif.prefix(4) == Data("TZif".utf8))
        #expect(tzif == (TimeZone(identifier: "Asia/Taipei")! as NSTimeZone).data)
        #expect(start["prices"] == (try JSONParser.parse(Self.prices.text)))
    }

    @Test func smallLines() throws {
        #expect(try object(RelayLineEncoder.ping(id: 42)) == ["t": .string("ping"), "id": .number(JSONLiteral("42"))])
        #expect(try object(RelayLineEncoder.today(todayStartMs: 5))["todayStartMs"]?.int64 == 5)
        #expect(try object(RelayLineEncoder.watch(true))["on"]?.bool == true)
        #expect(try object(RelayLineEncoder.watch(false))["on"]?.bool == false)
        #expect(try object(RelayLineEncoder.prices(Self.prices))["table"] == (try JSONParser.parse(Self.prices.text)))
        let zone = try object(RelayLineEncoder.zone(RemoteZone.current(TimeZone(identifier: "Europe/Berlin")!)))
        #expect(zone["id"]?.string == "Europe/Berlin")
        let history = try #require(RelayLineEncoder.history(digests: ["2026-11-03/codex": "0123456789abcdef",
                                                                      "2026-11-02/claudeCode": "fedcba9876543210"]))
        #expect(String(decoding: history, as: UTF8.self)
            == #"{"t":"history","digests":{"2026-11-02/claudeCode":"fedcba9876543210","2026-11-03/codex":"0123456789abcdef"}}"# + "\n")
    }

    @Test func zonesAreTZif() {
        let current = RemoteZone.current()
        #expect(RemoteZone.isTZif(current.tzif))
        let fixed = RemoteZone.current(TimeZone(secondsFromGMT: 3600)!)
        #expect(fixed.id == "Etc/GMT-1")
        #expect(RemoteZone.isTZif(fixed.tzif))
        let odd = RemoteZone.current(TimeZone(secondsFromGMT: 5 * 3600 + 1800)!)
        #expect(odd.id == "UTC")
        #expect(RemoteZone(id: "Bad Zone", tzif: current.tzif) == nil)
        #expect(RemoteZone(id: "UTC", tzif: Data("nope".utf8)) == nil)
    }

    @Test func linesBeyond4MiBAreRefused() {
        let big = RemotePriceTableJSON(Data(("{\"k\":\"" + String(repeating: "x", count: 4 << 20) + "\"}").utf8))
        #expect(big == nil)
    }
}

@Suite("Remote clock")
struct RemoteClockTests {
    @Test func helloGivesTheOffsetUntilTheFirstSample() {
        var clock = RemoteClockEstimator()
        #expect(clock.offsetMs == nil)
        clock.noteHello(remoteNowMs: 10_000, localMs: 3_000)
        #expect(clock.offsetMs == 7_000)
        #expect(clock.isSkewed)
        clock.addSample(sendMs: 1_000, receiveMs: 1_100, remoteNowMs: 1_060)
        #expect(clock.offsetMs == 10)
        #expect(!clock.isSkewed)
    }

    @Test func smallestRoundTripOfTheLastEightWins() {
        var clock = RemoteClockEstimator()
        clock.addSample(sendMs: 0, receiveMs: 10, remoteNowMs: 105)
        clock.addSample(sendMs: 0, receiveMs: 400, remoteNowMs: 900)
        #expect(clock.offsetMs == 100)
        for index in Int64(0)..<8 {
            clock.addSample(sendMs: index * 1000, receiveMs: index * 1000 + 50, remoteNowMs: index * 1000 + 25 - 2000)
        }
        #expect(clock.samples.count == 8)
        #expect(clock.offsetMs == -2000)
        clock.addSample(sendMs: 100_000, receiveMs: 100_003, remoteNowMs: 106_001)
        #expect(clock.offsetMs == 6000)
        #expect(clock.isSkewed)
    }

    @Test func halfMillisecondsRound() {
        var clock = RemoteClockEstimator()
        clock.addSample(sendMs: 0, receiveMs: 1, remoteNowMs: 100)
        #expect(clock.offsetMs == 100)
        var negative = RemoteClockEstimator()
        negative.addSample(sendMs: 0, receiveMs: 1, remoteNowMs: -100)
        #expect(negative.offsetMs == -101)
    }

    @Test func skewThresholdIsFiveSeconds() {
        #expect(!RemoteClockEstimator.isSkewed(4_999))
        #expect(RemoteClockEstimator.isSkewed(5_000))
        #expect(RemoteClockEstimator.isSkewed(-5_000))
        #expect(RemoteHostStatus(status: .connected, clockOffsetMs: -12_000).isClockSkewed)
        #expect(!RemoteHostStatus(status: .connected).isClockSkewed)
    }

    @Test func conversionShiftsEveryTimeAndKeepsDurations() throws {
        let decoder = RelayLineDecoder(hostId: "8C0D6B0A-1111-4222-8333-944455556666")
        guard case .message(let response) = decoder.decode(Data(RelayLines.response().utf8)),
              case .message(let agent) = decoder.decode(Data(RelayLines.agent().utf8)) else {
            Issue.record("decoding failed")
            return
        }
        guard case .response(let shifted) = response.converted(offsetMs: 3_000) else { return }
        #expect(shifted.record.startMs == RelayLines.baseMs - 3_000)
        #expect(shifted.record.endMs == RelayLines.baseMs + 2000 - 3_000)
        #expect(shifted.record.firstMs == RelayLines.baseMs + 300 - 3_000)
        guard case .agent(let moved) = agent.converted(offsetMs: -1_000) else { return }
        #expect(moved.inFlightStartMs == RelayLines.baseMs + 1_000)
        #expect(moved.lastLineMs == RelayLines.baseMs + 1_500)
        #expect(moved.actions[0].callMs == RelayLines.baseMs + 1_000)
        #expect(moved.actions[0].resultMs == RelayLines.baseMs + 1_400)
        #expect(RelayMessage.ready.converted(offsetMs: 99) == .ready)
    }

    @Test func conversionThatLeavesTheRangeIsInvalid() {
        let decoder = RelayLineDecoder(hostId: "8C0D6B0A-1111-4222-8333-944455556666")
        var object = RelayLines.responseObject()
        object["endMs"] = RemoteFields.earliestMs + 10
        object["startMs"] = RemoteFields.earliestMs
        object["firstMs"] = NSNull()
        guard case .message(let response) = decoder.decode(Data(RelayLines.response(object).utf8)) else {
            Issue.record("decoding failed")
            return
        }
        #expect(response.converted(offsetMs: -5) != nil)
        #expect(response.converted(offsetMs: 0) != nil)
        #expect(response.converted(offsetMs: 1) == nil)
        #expect(response.converted(offsetMs: RemoteFields.latestMs) == nil)
        #expect(response.converted(offsetMs: .min) == nil)
    }
}

@Suite("Remote relay bundle")
struct RemoteRelayBundleTests {
    @Test func findsBothArchitecturesWithTheirDigests() throws {
        let relays = try FakeRelayBundle(version: "1.7.0")
        let x86 = try #require(relays.bundle.executable(for: .x86_64))
        let arm = try #require(relays.bundle.executable(for: .aarch64))
        #expect(x86.sha256 == (try FileDigest.sha256(of: relays.directory.file("agent-speed-relay-linux-x86_64"))))
        #expect(x86.sha256 != arm.sha256)
        #expect(x86.installedName == "agent-speed-relay-1.7.0-x86_64-" + x86.sha256.prefix(12))
        #expect(arm.installedName.hasPrefix("agent-speed-relay-1.7.0-aarch64-"))
        #expect(x86.size == 600_000)
    }

    @Test func overrideFolderComesFromTheEnvironment() throws {
        let relays = try FakeRelayBundle()
        let bundle = RemoteRelayBundle.current(environment: [RemoteRelayBundle.overrideVariable: relays.directory.path])
        #expect(bundle.directory?.path == relays.directory.url.path)
        #expect(bundle.executable(for: .x86_64)?.sha256 == relays.x86.sha256)
        let unbundled = RemoteRelayBundle.current(environment: [:])
        #expect(unbundled.directory?.lastPathComponent == "relay")
        #expect(RemoteRelayBundle(directory: relays.directory.file("missing"), version: "1").executable(for: .x86_64) == nil)
    }

    @Test func digestFollowsAChangedFile() throws {
        let relays = try FakeRelayBundle()
        let url = relays.directory.file("agent-speed-relay-linux-x86_64")
        try Data("replaced".utf8).write(to: url)
        #expect(relays.bundle.executable(for: .x86_64)?.sha256 == FileDigest.sha256(of: Data("replaced".utf8)))
    }
}
