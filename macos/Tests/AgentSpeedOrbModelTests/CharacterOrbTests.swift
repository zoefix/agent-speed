import AgentSpeedOrbModel
import Foundation
import Testing

@Suite struct CharacterSelectionTests {
    let packs = [
        CharacterSelection.Pack(id: "claude", vendors: ["anthropic"]),
        CharacterSelection.Pack(id: "gpt", vendors: ["openai"]),
    ]

    @Test func nothingInstalledShowsNothing() {
        var selection = CharacterSelection()
        #expect(selection.update(locked: "claude", vendorRates: ["anthropic": 50], installed: [], at: 0) == nil)
    }

    @Test func lockedShowsItsCharacterRightAway() {
        var selection = CharacterSelection(shown: "claude")
        #expect(selection.update(locked: "gpt", vendorRates: ["anthropic": 90], installed: packs, at: 0) == "gpt")
    }

    @Test func aLockOnARemovedCharacterFallsBackToAuto() {
        var selection = CharacterSelection()
        #expect(selection.update(locked: "gemini", vendorRates: ["openai": 30], installed: packs, at: 0) == "gpt")
    }

    @Test func autoFollowsTheFastestVendorWithACharacter() {
        var selection = CharacterSelection()
        let rates = ["google": 200, "openai": 40, "anthropic": 30.0]
        #expect(selection.update(locked: nil, vendorRates: rates, installed: packs, at: 0) == "gpt")
    }

    @Test func aChangeIsHeldForThreeSeconds() {
        var selection = CharacterSelection()
        #expect(selection.update(locked: nil, vendorRates: ["anthropic": 60], installed: packs, at: 0) == "claude")
        #expect(selection.update(locked: nil, vendorRates: ["openai": 90, "anthropic": 60], installed: packs, at: 1) == "claude")
        #expect(selection.update(locked: nil, vendorRates: ["openai": 90, "anthropic": 60], installed: packs, at: 3.9) == "claude")
        #expect(selection.update(locked: nil, vendorRates: ["openai": 90, "anthropic": 60], installed: packs, at: 4.0) == "gpt")
    }

    @Test func aBriefLeadDoesNotSwitch() {
        var selection = CharacterSelection()
        _ = selection.update(locked: nil, vendorRates: ["anthropic": 60], installed: packs, at: 0)
        _ = selection.update(locked: nil, vendorRates: ["openai": 90], installed: packs, at: 1)
        _ = selection.update(locked: nil, vendorRates: ["anthropic": 60], installed: packs, at: 2)
        #expect(selection.update(locked: nil, vendorRates: ["openai": 90], installed: packs, at: 4.5) == "claude")
    }

    @Test func quietOrUnknownVendorsKeepTheLastCharacter() {
        var selection = CharacterSelection()
        #expect(selection.update(locked: nil, vendorRates: [:], installed: packs, at: 0) == "claude")
        _ = selection.update(locked: nil, vendorRates: ["openai": 20], installed: packs, at: 1)
        #expect(selection.update(locked: nil, vendorRates: ["openai": 20], installed: packs, at: 5) == "gpt")
        #expect(selection.update(locked: nil, vendorRates: ["xai": 300], installed: packs, at: 9) == "gpt")
    }
}

@Suite struct CharacterFootprintTests {
    @Test func theCharacterStandsInADoubleDisc() {
        #expect(OrbStyleKind.character.footprint == 2)
        #expect(OrbStyleKind.allCases.filter { $0 != .character }.allSatisfy { $0.footprint == 1 })
        #expect(OrbConfig(id: "a", style: .character, size: 148).footprintDiameter == 296)
        #expect(OrbConfig(id: "a", style: .led, size: 148).footprintDiameter == 148)
    }

    @Test func clicksLandOnTheBodyOnly() {
        let shape = OrbStyleKind.character.hitShape
        let d: CGFloat = 300
        #expect(shape.contains(dx: 0, dy: 0, diameter: d))
        #expect(shape.contains(dx: 0, dy: 120, diameter: d))
        #expect(shape.contains(dx: 0, dy: -140, diameter: d))
        #expect(!shape.contains(dx: 100, dy: 0, diameter: d))
        #expect(!shape.contains(dx: 0, dy: 140, diameter: d))
        #expect(OrbHitShape.disc.contains(dx: 100, dy: 0, diameter: d))
        let box = shape.bounds(diameter: d)
        #expect(abs(box.width - 126) < 0.001 && abs(box.height - 270) < 0.001)
    }

    @Test func characterIDsAreCleanedOnDecode() throws {
        let json = #"{"id": "a", "style": "character", "character": "claude"}"#
        let orb = try JSONDecoder().decode(OrbConfig.self, from: Data(json.utf8))
        #expect(orb.style == .character && orb.character == "claude")
        let bad = try JSONDecoder().decode(OrbConfig.self, from: Data(#"{"id": "a", "character": "../x"}"#.utf8))
        #expect(bad.character == nil)
    }

    @Test func autoIsOmittedWhenEncoding() throws {
        let auto = String(decoding: try JSONEncoder().encode(OrbConfig(id: "a")), as: UTF8.self)
        #expect(!auto.contains("character\""))
        let locked = try JSONEncoder().encode(OrbConfig(id: "a", style: .character, character: "gpt"))
        #expect(try JSONDecoder().decode(OrbConfig.self, from: locked).character == "gpt")
    }

    @Test func editingSetsAndCopiesTheCharacter() throws {
        var doc = OrbsDocument(orbs: [OrbConfig(id: "a", style: .character)])
        doc = OrbEditing.setCharacter("claude", of: "a", in: doc)
        #expect(doc.orb("a")?.character == "claude")
        let added = try #require(OrbEditing.add(to: doc, copying: "a", availability: OrbAvailability(detected: []), newId: "b"))
        #expect(added.orb("b")?.character == "claude")
        #expect(OrbEditing.setCharacter(nil, of: "a", in: doc).orb("a")?.character == nil)
        #expect(OrbEditing.setCharacter("Not Valid", of: "a", in: doc).orb("a")?.character == nil)
    }
}
