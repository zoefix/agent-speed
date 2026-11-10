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

    @Test func readoutsDefaultOnExceptTheModel() throws {
        let orb = OrbConfig(id: "a", style: .character)
        #expect(orb.characterReadouts == .default)
        #expect(CharacterReadout.allCases.allSatisfy { orb.characterReadouts[$0] == ($0 != .model) })
        let old = try JSONDecoder().decode(OrbConfig.self, from: Data(#"{"id": "a", "style": "character"}"#.utf8))
        #expect(old.characterReadouts == .default)
        let encoded = String(decoding: try JSONEncoder().encode(orb), as: UTF8.self)
        #expect(!encoded.contains("characterReadouts"))
    }

    @Test func readoutsDecodeTolerantlyAndRoundTrip() throws {
        let json = #"{"id": "a", "characterReadouts": {"model": true, "network": false, "cost": "yes"}}"#
        let orb = try JSONDecoder().decode(OrbConfig.self, from: Data(json.utf8))
        #expect(orb.characterReadouts == CharacterReadouts(model: true, network: false))
        let again = try JSONDecoder().decode(OrbConfig.self, from: JSONEncoder().encode(orb))
        #expect(again.characterReadouts == orb.characterReadouts)
        let broken = try JSONDecoder().decode(OrbConfig.self, from: Data(#"{"id": "a", "characterReadouts": 3}"#.utf8))
        #expect(broken.characterReadouts == .default)
    }

    /// The 首字 row and the models card (user's choice, 2026-11-10): on by default, in the menus' order, tolerant and
    /// written only when they differ.
    @Test func latencyAndModelsAreOnByDefaultAndRoundTrip() throws {
        #expect(CharacterReadout.allCases == [.intelligence, .thinking, .latency, .network, .cost, .agents, .models, .quota,
                                              .model])
        #expect(CharacterReadout.latency.titleKey == "character.readouts.latency")
        #expect(CharacterReadout.models.titleKey == "character.readouts.models")
        #expect(CharacterReadouts.default.latency && CharacterReadouts.default.models)
        let older = try JSONDecoder().decode(CharacterReadouts.self, from: Data(#"{"model": true, "cost": false}"#.utf8))
        #expect(older == CharacterReadouts(model: true, cost: false))
        let typed = try JSONDecoder().decode(CharacterReadouts.self,
                                             from: Data(#"{"latency": "no", "models": 0, "thinking": false}"#.utf8))
        #expect(typed == CharacterReadouts(thinking: false))
        let off = CharacterReadouts(latency: false, models: false)
        let json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(off)) as? [String: Bool]
        #expect(json?["latency"] == false && json?["models"] == false && json?["network"] == true)
        #expect(try JSONDecoder().decode(CharacterReadouts.self, from: JSONEncoder().encode(off)) == off)
        var orb = OrbConfig(id: "a", style: .character)
        orb.characterReadouts.models = false
        let again = try JSONDecoder().decode(OrbConfig.self, from: JSONEncoder().encode(orb))
        #expect(again.characterReadouts == CharacterReadouts(models: false))
        var doc = OrbsDocument(orbs: [OrbConfig(id: "a", style: .character)])
        doc = OrbEditing.setCharacterReadout(.latency, false, of: "a", in: doc)
        #expect(doc.orb("a")?.characterReadouts == CharacterReadouts(latency: false))
        doc = OrbEditing.setCharacterReadout(.latency, true, of: "a", in: doc)
        let encoded = String(decoding: try JSONEncoder().encode(doc.orb("a")), as: UTF8.self)
        #expect(!encoded.contains("characterReadouts"))
    }

    /// 額度 (user's choice, 2026-11-10): on by default, between the models card and the model chip, tolerant and written
    /// only when it differs.
    @Test func quotaIsOnByDefaultAndRoundTrips() throws {
        #expect(CharacterReadouts.default.quota)
        #expect(CharacterReadout.quota.titleKey == "character.readouts.quota")
        let models = try #require(CharacterReadout.allCases.firstIndex(of: .models))
        #expect(CharacterReadout.allCases[models + 1] == .quota && CharacterReadout.allCases.last == .model)
        let older = try JSONDecoder().decode(CharacterReadouts.self, from: Data(#"{"models": false}"#.utf8))
        #expect(older == CharacterReadouts(models: false) && older.quota)
        let typed = try JSONDecoder().decode(CharacterReadouts.self, from: Data(#"{"quota": "off"}"#.utf8))
        #expect(typed == .default)
        let off = CharacterReadouts(quota: false)
        let json = try JSONSerialization.jsonObject(with: JSONEncoder().encode(off)) as? [String: Bool]
        #expect(json?["quota"] == false && json?["models"] == true)
        var doc = OrbsDocument(orbs: [OrbConfig(id: "a", style: .character)])
        doc = OrbEditing.setCharacterReadout(.quota, false, of: "a", in: doc)
        #expect(doc.orb("a")?.characterReadouts == off)
        #expect(String(decoding: try JSONEncoder().encode(doc.orb("a")), as: UTF8.self).contains("\"quota\":false"))
        doc = OrbEditing.setCharacterReadout(.quota, true, of: "a", in: doc)
        #expect(!String(decoding: try JSONEncoder().encode(doc.orb("a")), as: UTF8.self).contains("characterReadouts"))
    }

    @Test func editingSetsAndCopiesTheReadouts() throws {
        var doc = OrbsDocument(orbs: [OrbConfig(id: "a", style: .character)])
        doc = OrbEditing.setCharacterReadout(.model, true, of: "a", in: doc)
        doc = OrbEditing.setCharacterReadout(.cost, false, of: "a", in: doc)
        #expect(doc.orb("a")?.characterReadouts == CharacterReadouts(model: true, cost: false))
        let added = try #require(OrbEditing.add(to: doc, copying: "a", availability: OrbAvailability(detected: []), newId: "b"))
        #expect(added.orb("b")?.characterReadouts == doc.orb("a")?.characterReadouts)
        let copy = try #require(OrbEditing.duplicate("a", in: doc, newId: "c"))
        #expect(copy.orb("c")?.characterReadouts == doc.orb("a")?.characterReadouts)
    }
}
