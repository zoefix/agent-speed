import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Orb idle behavior persistence")
struct OrbIdleBehaviorTests {
    private func decode(_ json: String) throws -> OrbConfig {
        try JSONDecoder().decode(OrbConfig.self, from: Data(json.utf8))
    }

    @Test func oldConfigurationsKeepTheirBehavior() throws {
        #expect(try decode(#"{"id":"a"}"#).idleBehavior == .keepVisible)
        #expect(try decode(#"{"id":"a","fadeWhenIdle":false}"#).idleBehavior == .keepVisible)
        #expect(try decode(#"{"id":"a","fadeWhenIdle":true}"#).idleBehavior == .fade)
        #expect(try decode(#"{"id":"a","fadeWhenIdle":true,"idleBehavior":"hide"}"#).idleBehavior == .hide)
        #expect(try decode(#"{"id":"a","fadeWhenIdle":true,"idleBehavior":"future"}"#).idleBehavior == .fade)
    }

    @Test(arguments: OrbIdleBehavior.allCases)
    func persistsAndCopiesEachBehavior(_ behavior: OrbIdleBehavior) throws {
        let orb = OrbConfig(id: "a", visible: false, idleBehavior: behavior)
        let document = OrbsDocument(orbs: [orb])
        let restored = try #require(OrbsDocument(data: document.data()))
        #expect(restored == document)
        #expect(restored.orbs[0].visible == false)
        let duplicate = try #require(OrbEditing.duplicate("a", in: restored, newId: "b"))
        #expect(duplicate.orb("b")?.idleBehavior == behavior)
        let added = try #require(OrbEditing.add(to: restored, copying: "a", availability: .init(detected: []), newId: "c"))
        #expect(added.orb("c")?.idleBehavior == behavior)
        let edited = OrbEditing.setIdleBehavior(.hide, of: "a", in: restored)
        #expect(edited.orb("a")?.idleBehavior == .hide)
        #expect(edited.orb("a")?.visible == false)
    }
}
