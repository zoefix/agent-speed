import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

/// DESIGN 2b Data model `hideWhenIdle`: off for a new orb and for one saved by an earlier version, tolerant of a value
/// that is not a boolean, persisted with the orb, copied by Add, Duplicate and Split, and Merge keeps the target's.
@Suite("Orb hide when idle")
struct OrbHideWhenIdleTests {
    static let detected = OrbAvailability(detected: [.codex, .claudeCode, .gemini])

    static func decode(_ json: String) throws -> OrbConfig {
        try JSONDecoder().decode(OrbConfig.self, from: Data(json.utf8))
    }

    @Test func anOrbFromAnEarlierVersionDoesNotHide() throws {
        let stored = #"{"version":1,"orbs":[{"id":"a","style":"needle","size":148,"opacity":0.8,"alwaysOnTop":true,"#
            + #""clickThrough":false,"fadeWhenIdle":true,"allSpaces":true,"visible":true}]}"#
        let document = try #require(OrbsDocument(data: Data(stored.utf8)))
        let orb = try #require(document.orb("a"))
        #expect(!orb.hideWhenIdle)
        #expect(orb.fadeWhenIdle && orb.opacity == 0.8 && orb.visible)
        #expect(!OrbConfig(id: "x").hideWhenIdle && !OrbConfig.makeDefault(id: "y").hideWhenIdle)
        #expect(OrbsDocument.fresh(id: "z").orbs.allSatisfy { !$0.hideWhenIdle })
    }

    @Test func aValueThatIsNotABooleanReadsAsOff() throws {
        for value in [#""yes""#, #""true""#, "1", "null", "[]", "{}"] {
            #expect(try !Self.decode(#"{"id":"a","hideWhenIdle":\#(value)}"#).hideWhenIdle)
        }
        #expect(try Self.decode(#"{"id":"a","hideWhenIdle":true}"#).hideWhenIdle)
        #expect(try !Self.decode(#"{"id":"a","hideWhenIdle":false}"#).hideWhenIdle)
    }

    @Test func theSwitchPersistsWithTheOrbAndLeavesVisibleAlone() throws {
        var document = OrbsDocument(orbs: [OrbConfig(id: "a"), OrbConfig(id: "b", fadeWhenIdle: true, visible: false)])
        document = OrbEditing.setToggle(\.hideWhenIdle, true, of: "b", in: document)
        document = OrbEditing.setToggle(\.hideWhenIdle, true, of: "missing", in: document)
        let json = try #require(String(data: document.data(), encoding: .utf8))
        #expect(json.contains(#""hideWhenIdle":true"#) && json.contains(#""hideWhenIdle":false"#))
        let decoded = try #require(OrbsDocument(data: document.data()))
        #expect(decoded == document)
        let b = try #require(decoded.orb("b"))
        #expect(b.hideWhenIdle && b.fadeWhenIdle && !b.visible)
        #expect(decoded.orb("a")?.hideWhenIdle == false && decoded.orb("a")?.visible == true)
        document = OrbEditing.setToggle(\.hideWhenIdle, false, of: "b", in: document)
        #expect(document.orb("b")?.hideWhenIdle == false && document.orb("b")?.visible == false)
    }

    @Test func addDuplicateAndSplitCopyIt() throws {
        let template = OrbConfig(id: "a", sources: .explicit([.codex, .claudeCode]), hideWhenIdle: true)
        let doc = OrbsDocument(orbs: [template, OrbConfig(id: "b")])
        let added = try #require(OrbEditing.add(to: doc, copying: "a", availability: Self.detected, newId: "n"))
        #expect(added.orb("n")?.hideWhenIdle == true)
        let plain = try #require(OrbEditing.add(to: doc, copying: "b", availability: Self.detected, newId: "n"))
        #expect(plain.orb("n")?.hideWhenIdle == false)
        let duplicated = try #require(OrbEditing.duplicate("a", in: doc, newId: "c"))
        #expect(duplicated.orb("c")?.hideWhenIdle == true)
        let split = try #require(OrbEditing.split("a", in: doc, availability: Self.detected, newIds: ["s1"]))
        #expect(split.created == ["s1"])
        #expect(split.document.orb("a")?.hideWhenIdle == true && split.document.orb("s1")?.hideWhenIdle == true)
    }

    @Test func mergeKeepsTheTargets() throws {
        let doc = OrbsDocument(orbs: [OrbConfig(id: "a", hideWhenIdle: true), OrbConfig(id: "b")])
        #expect(try #require(OrbEditing.merge(doc, keeping: "b")).orb("b")?.hideWhenIdle == false)
        #expect(try #require(OrbEditing.merge(doc, keeping: "a")).orb("a")?.hideWhenIdle == true)
    }
}
