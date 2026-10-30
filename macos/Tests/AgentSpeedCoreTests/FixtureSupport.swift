import Foundation
import Testing
@testable import AgentSpeedCore

enum Fixtures {
    static let root: URL = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .deletingLastPathComponent()
        .appendingPathComponent("spec/fixtures", isDirectory: true)

    static func json(_ name: String) throws -> [String: Any] {
        let data = try Data(contentsOf: root.appendingPathComponent(name))
        return try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
    }
}

/// Splits a buffer into lines the way the tailer does: newline-terminated lines only, an incomplete last line waits for
/// its newline (METRICS 5), as `reference.py` reads them.
func forEachLine(in data: Data, _ body: (UnsafeRawBufferPointer) -> Void) {
    data.withUnsafeBytes { buffer in
        var start = 0
        for index in 0..<buffer.count where buffer[index] == 0x0A {
            body(UnsafeRawBufferPointer(rebasing: buffer[start..<index]))
            start = index + 1
        }
    }
}

extension Dictionary where Key == String, Value == Any {
    func int64(_ key: String) -> Int64? { (self[key] as? NSNumber)?.int64Value }
    func int(_ key: String) -> Int { (self[key] as? NSNumber)?.intValue ?? 0 }
    func double(_ key: String) -> Double? { (self[key] as? NSNumber)?.doubleValue }
    func string(_ key: String) -> String? { self[key] as? String }
}

/// `effort` and `speed` (the agent's current attributions) and `responseModes` (per response key), checked only when
/// the expectation carries them (EXECUTION-MODES, Reasoning effort and speed).
func expectModeFields(
    _ view: EffortAndSpeed,
    _ responses: [ResponseRecord],
    _ wanted: [String: Any],
    _ label: String,
    sourceLocation: SourceLocation = #_sourceLocation
) {
    func object(_ value: ExecutionModeAttribution?) -> [String: String]? {
        guard let value else { return nil }
        var result = ["mode": value.mode, "evidence": value.evidence]
        if let provider = value.provider { result["provider"] = provider }
        return result
    }
    if wanted.keys.contains("effort") {
        #expect(object(view.effort) == wanted["effort"] as? [String: String], "\(label) effort", sourceLocation: sourceLocation)
    }
    if wanted.keys.contains("speed") {
        #expect(object(view.speed) == wanted["speed"] as? [String: String], "\(label) speed", sourceLocation: sourceLocation)
    }
    guard let byKey = wanted["responseModes"] as? [String: [String: Any]] else { return }
    #expect(Set(byKey.keys) == Set(responses.map(\.key)), "\(label) responseModes keys", sourceLocation: sourceLocation)
    for record in responses {
        guard let modes = byKey[record.key] else { continue }
        let view = record.modes.effortAndSpeed
        #expect(object(view.effort) == modes["effort"] as? [String: String], "\(label) \(record.key) effort",
                sourceLocation: sourceLocation)
        #expect(object(view.speed) == modes["speed"] as? [String: String], "\(label) \(record.key) speed",
                sourceLocation: sourceLocation)
    }
}

/// What the fixture folders play for an agent's current values: `codex/config.toml`, `gemini/settings.json`,
/// `antigravity/config/config.json`, and the settings and database of the `workbuddy` data folders.
func fixtureCurrent(_ state: AgentState, agentId: String) -> EffortAndSpeed {
    let root = Fixtures.root
    var session = state.sessionModes, configured = ExecutionModes.empty
    switch state.source {
    case .codex:
        let home = agentId.components(separatedBy: "/sessions/")[0]
        configured = ConfiguredModes.codex(path: home + "/config.toml")
    case .gemini:
        configured = ConfiguredModes.gemini(path: root.appendingPathComponent("gemini/settings.json").path).modes(model: state.model)
    case .antigravity:
        configured = ConfiguredModes.antigravity(path: root.appendingPathComponent("antigravity/config/config.json").path)
    case .workbuddy, .workbuddyAi:
        let data = root.appendingPathComponent(state.source == .workbuddy ? "workbuddy/.workbuddy" : "workbuddy/.workbuddy-ai")
        configured = ConfiguredModes.workBuddy(path: data.appendingPathComponent("settings.json").path)
        let id = ((agentId as NSString).lastPathComponent as NSString).deletingPathExtension
        session = SessionModes.workBuddy(databasePath: data.appendingPathComponent("workbuddy.db").path, sessions: [id])[id] ?? .empty
    default:
        break
    }
    return EffortAndSpeed(facts: state.modes, session: session, configured: configured)
}

/// The fields added with the cost feature (METRICS 3.2): `cacheWrite1hInput`, `provider` and `fast`.
func expectPricingFields(
    _ record: ResponseRecord,
    _ wanted: [String: Any],
    _ label: String,
    sourceLocation: SourceLocation = #_sourceLocation
) {
    #expect(record.usage.cacheWrite1hInput == wanted.int("cacheWrite1hInput"), "\(label) cacheWrite1hInput",
            sourceLocation: sourceLocation)
    #expect(record.provider == wanted.string("provider"), "\(label) provider", sourceLocation: sourceLocation)
    #expect(record.fast == (wanted["fast"] as? Bool), "\(label) fast", sourceLocation: sourceLocation)
}
