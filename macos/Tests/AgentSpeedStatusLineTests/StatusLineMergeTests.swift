import Foundation
import Testing
@testable import AgentSpeedStatusLineKit

/// The helper's merge and line against `spec/fixtures/quota-cases.json` (QUOTAS 5.5).
struct StatusLineMergeTests {
    nonisolated(unsafe) static let cases: [String: Any] = {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("spec/fixtures/quota-cases.json")
        let data = (try? Data(contentsOf: url)) ?? Data()
        return (try? JSONSerialization.jsonObject(with: data) as? [String: Any]) ?? [:]
    }()

    static func stored(_ value: Any?) -> StatusLineQuota? {
        guard let value, !(value is NSNull) else { return nil }
        return (try? JSONSerialization.data(withJSONObject: value)).flatMap(StatusLineQuota.init(data:))
    }

    @Test func everyMergeStepMatchesTheReference() throws {
        let steps = try #require(Self.cases["helper"] as? [[String: Any]])
        #expect(steps.count == 6)
        var stored = StatusLineQuota()
        for (index, step) in steps.enumerated() {
            let input = try #require(step["input"] as? [String: Any])
            let now = try #require((step["nowMs"] as? NSNumber)?.int64Value)
            stored.merge(rateLimits: input["rate_limits"], nowMs: now)
            let expected = try #require(Self.stored(step["expected"]), "step \(index)")
            #expect(stored == expected, "step \(index)")
            #expect(stored.line(nowMs: now) == step["line"] as? String, "step \(index) line")
        }
    }

    @Test func expiredWindowsLeaveTheLine() throws {
        let expired = try #require(Self.cases["helperExpired"] as? [String: Any])
        let stored = try #require(Self.stored(expired["stored"]))
        let now = try #require((expired["nowMs"] as? NSNumber)?.int64Value)
        #expect(stored.line(nowMs: now) == expired["line"] as? String)
    }

    @Test func theFileRoundTrips() throws {
        var stored = StatusLineQuota()
        stored.merge(rateLimits: ["five_hour": ["used_percentage": 6, "resets_at": 1_794_354_000],
                                  "seven_day": ["used_percentage": 86.5, "resets_at": 1_794_448_800]], nowMs: 5)
        let read = try #require(StatusLineQuota(data: stored.data()))
        #expect(read == stored)
        let object = try #require(try JSONSerialization.jsonObject(with: stored.data()) as? [String: Any])
        #expect((object["version"] as? NSNumber)?.intValue == 1)
        let five = try #require((object["windows"] as? [String: Any])?["five_hour"] as? [String: Any])
        #expect("\(five["used_percentage"]!)" == "6")
    }

    @Test func booleansAndStringsAreNotNumbers() {
        var stored = StatusLineQuota()
        let changed = stored.merge(rateLimits: ["five_hour": ["used_percentage": true, "resets_at": 1],
                                                "seven_day": ["used_percentage": "5", "resets_at": 1]], nowMs: 1)
        #expect(!changed)
        #expect(stored.windows.isEmpty)
    }
}
