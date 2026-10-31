import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Orbs document JSON")
struct DocumentCodingTests {
    static let specExample = """
    {
      "version": 1,
      "orbs": [
        {
          "id": "6F1C2A9E-0000-4000-8000-000000000001",
          "name": null,
          "sources": ["claudeCode", "claudeDesktop"],
          "style": "needle",
          "size": 148,
          "opacity": 1.0,
          "alwaysOnTop": true,
          "clickThrough": false,
          "fadeWhenIdle": false,
          "allSpaces": true,
          "visible": true,
          "redlineOutput": null,
          "redlineTotal": null
        }
      ],
      "layouts": {
        "A+B": { "6F1C2A9E-0000-4000-8000-000000000001": { "display": "A", "xAnchor": "high", "x": 12, "yAnchor": "fraction", "y": 0.31 } }
      }
    }
    """

    @Test func decodesTheSpecExample() throws {
        let document = try #require(OrbsDocument(data: Data(Self.specExample.utf8)))
        #expect(document.version == 1)
        #expect(document.orbs.count == 1)
        let orb = document.orbs[0]
        #expect(orb.id == "6F1C2A9E-0000-4000-8000-000000000001")
        #expect(orb.name == nil)
        #expect(orb.sources == .explicit(["claudeCode", "claudeDesktop"]))
        #expect(orb.style == .needle)
        #expect(orb.size == 148)
        #expect(orb.alwaysOnTop && !orb.clickThrough && !orb.fadeWhenIdle && orb.allSpaces && orb.visible)
        #expect(orb.redlineOutput == nil && orb.redlineTotal == nil)
        let entry = try #require(document.entry(orb: orb.id, arrangement: "A+B"))
        #expect(entry.display == "A")
        #expect(entry.record == OrbPositionRecord(x: .high(12), y: .fraction(0.31)))
        #expect(document.layoutOrder == ["A+B"])
    }

    @Test func encodesTheSpecFieldNames() throws {
        var document = OrbsDocument(orbs: [OrbConfig(id: "a"), OrbConfig(id: "b", sources: .explicit([.codex]))])
        document.setEntry(OrbLayoutEntry(display: "D1", record: .init(x: .low(12), y: .high(3))), orb: "a", arrangement: "D1")
        let object = try #require(try JSONSerialization.jsonObject(with: document.data()) as? [String: Any])
        #expect(object["version"] as? Int == 1)
        let orbs = try #require(object["orbs"] as? [[String: Any]])
        #expect(orbs[0]["sources"] as? String == "all")
        #expect(orbs[1]["sources"] as? [String] == ["codex"])
        #expect(orbs[0].keys.contains("name") && orbs[0]["name"] is NSNull)
        #expect(orbs[0].keys.contains("redlineOutput") && orbs[0]["redlineOutput"] is NSNull)
        for key in ["id", "style", "size", "opacity", "alwaysOnTop", "clickThrough", "fadeWhenIdle", "allSpaces", "visible", "redlineTotal"] {
            #expect(orbs[0].keys.contains(key), "missing \(key)")
        }
        let layouts = try #require(object["layouts"] as? [String: [String: [String: Any]]])
        let entry = try #require(layouts["D1"]?["a"])
        #expect(entry["display"] as? String == "D1")
        #expect(entry["xAnchor"] as? String == "low")
        #expect(entry["x"] as? Double == 12)
        #expect(entry["yAnchor"] as? String == "high")
        #expect(entry["y"] as? Double == 3)
    }

    @Test func roundTripsExactly() throws {
        var document = OrbsDocument(orbs: [
            OrbConfig(id: "a", name: "Work", sources: .explicit([.claudeCode, .codex]), style: .reactor, size: 196,
                      opacity: 0.7, alwaysOnTop: false, clickThrough: true, fadeWhenIdle: true, allSpaces: false,
                      visible: false, redlineOutput: 800, redlineTotal: 300_000),
            OrbConfig(id: "b", style: .minimal, size: 64),
        ])
        document.setEntry(OrbLayoutEntry(display: "X", record: .init(x: .fraction(0.25), y: .low(0))), orb: "a", arrangement: "X")
        document.setEntry(OrbLayoutEntry(display: "Y", record: .init(x: .high(12), y: .high(12))), orb: "b", arrangement: "X+Y")
        let decoded = try #require(OrbsDocument(data: document.data()))
        #expect(decoded == document)
        #expect(document.data() == decoded.data())
    }

    @Test func storesKnownSourcesInSourceOrder() {
        #expect(OrbSourceSelection.explicit([.gemini, .codex, .codex, .claudeCode]) == .explicit(["codex", "claudeCode", "gemini"]))
        #expect(OrbSourceSelection.explicit([SourceKind]()) == .all)
    }

    @Test func toleratesUnknownAndOutOfRangeValues() throws {
        let json = """
        {"version": 7, "orbs": [
          {"id": "a", "style": "hologram", "size": 20, "opacity": 3, "sources": ["future", "gemini", "codex", "gemini"],
           "name": "   Long name that keeps going on and on and on   ", "redlineOutput": -5, "extra": 1},
          {"id": "a", "style": "needle"},
          {"style": "needle"},
          {"id": "c", "style": "minimal", "size": 500, "sources": []}
        ], "layouts": {"K": {"a": {"display": "D", "xAnchor": "low", "x": 99, "yAnchor": "fraction", "y": 4},
                               "ghost": {"display": "D", "xAnchor": "low", "x": 1, "yAnchor": "low", "y": 1},
                               "c": {"display": "D", "xAnchor": "sideways", "x": 1, "yAnchor": "low", "y": 1}}}}
        """
        let document = try #require(OrbsDocument(data: Data(json.utf8)))
        #expect(document.version == 7)
        #expect(document.orbs.map(\.id) == ["a", "c"])
        let a = document.orbs[0]
        #expect(a.style == .led)
        #expect(a.size == 64)
        #expect(a.opacity == 1)
        #expect(a.sources == .explicit(["codex", "gemini", "future"]))
        #expect(a.sources.knownSources == [.codex, .gemini])
        #expect(a.sources.sourceSet == SourceSet([.codex, .gemini]))
        #expect(a.name?.count == 32)
        #expect(a.name?.hasPrefix("Long name") == true)
        #expect(a.redlineOutput == nil)
        let c = document.orbs[1]
        #expect(c.size == 240)
        #expect(c.sources == .all)
        #expect(document.entry(orb: "a", arrangement: "K")?.record == OrbPositionRecord(x: .low(24), y: .fraction(1)))
        #expect(document.entry(orb: "ghost", arrangement: "K") == nil)
        #expect(document.entry(orb: "c", arrangement: "K") == nil)
    }

    @Test func missingFieldsTakeDefaults() throws {
        let document = try #require(OrbsDocument(data: Data(#"{"orbs": [{"id": "x"}]}"#.utf8)))
        #expect(document.orbs == [OrbConfig.makeDefault(id: "x")])
        #expect(document.orbs[0].size == 148 && document.orbs[0].style == .led && document.orbs[0].sources == .all)
    }

    @Test func rejectsDocumentsWithoutOrbs() {
        #expect(OrbsDocument(data: Data("not json".utf8)) == nil)
        #expect(OrbsDocument(data: Data(#"{"version": 1}"#.utf8)) == nil)
        #expect(OrbsDocument(data: Data(#"{"orbs": []}"#.utf8)) == nil)
        #expect(OrbsDocument(data: Data(#"{"orbs": [{"name": "no id"}]}"#.utf8)) == nil)
        #expect(OrbsDocument(data: Data()) == nil)
    }

    @Test func keepsAtMostEightOrbs() throws {
        let orbs = (0..<11).map { #"{"id": "o\#($0)"}"# }.joined(separator: ",")
        let document = try #require(OrbsDocument(data: Data(#"{"orbs": [\#(orbs)]}"#.utf8)))
        #expect(document.orbs.map(\.id) == (0..<8).map { "o\($0)" })
    }

    @Test func sizeIsClampedToTheSharedRange() {
        let small = OrbConfig(id: "m", style: .minimal, size: 72)
        #expect(small.size == 72)
        #expect(small.withStyle(.needle).size == 72)
        #expect(small.withStyle(.needle).style == .needle)
        #expect(OrbConfig(id: "l", style: .led, size: 72).size == 72)
        #expect(OrbConfig(id: "l", style: .led, size: 20).size == 64)
        #expect(OrbConfig(id: "l", style: .led, size: 500).size == 240)
        #expect(OrbStyleKind.minimal.sizeRange == 64...240)
        #expect(OrbStyleKind.reactor.sizeRange == 64...240)
        #expect(OrbStyleKind.liquid.clampSize(.nan) == 148)
    }

    @Test func styleKeys() {
        #expect(OrbStyleKind.allCases.map(\.rawValue) == ["led", "needle", "liquid", "reactor", "minimal"])
        #expect(OrbStyleKind.needle.titleKey == "style.needle")
        #expect(OrbStyleKind.needle.descriptionKey == "style.needle.desc")
        #expect(OrbSizes.presets == [112, 148, 196])
    }

    @Test func positionRecordDecodesLikeTheLegacyStore() throws {
        let decode = { (json: String) in try? JSONDecoder().decode(OrbPositionRecord.self, from: Data(json.utf8)) }
        #expect(decode(#"{"xAnchor": "high", "x": 40, "yAnchor": "low", "y": -3}"#) == OrbPositionRecord(x: .high(24), y: .low(0)))
        #expect(decode(#"{"xAnchor": "fraction", "x": 1.5, "yAnchor": "fraction", "y": -1}"#) == OrbPositionRecord(x: .fraction(1), y: .fraction(0)))
        #expect(decode(#"{"xAnchor": "middle", "x": 1, "yAnchor": "low", "y": 1}"#) == nil)
        #expect(decode(#"{"xAnchor": "low", "yAnchor": "low", "y": 1}"#) == nil)
    }
}
