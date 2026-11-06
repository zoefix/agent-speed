import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Name plate visibility")
struct NameplateVisibilityTests {
    static let allSources = OrbConfig(id: "all")
    static let subset = OrbConfig(id: "subset", sources: .explicit([.codex]))

    static func orb(_ base: OrbConfig = allSources, showName: Bool?) -> OrbConfig {
        var orb = base
        orb.showName = showName
        return orb
    }

    @Test(arguments: [
        (OrbNamesMode.auto, 1, true, false),
        (.auto, 1, false, true),
        (.auto, 2, true, true),
        (.auto, 2, false, true),
        (.auto, 8, true, true),
        (.always, 1, true, true),
        (.always, 1, false, true),
        (.always, 3, true, true),
        (.never, 1, false, false),
        (.never, 2, true, false),
        (.never, 8, false, false),
    ])
    func globalSettingTable(_ mode: OrbNamesMode, _ shownOrbs: Int, _ tracksAll: Bool, _ expected: Bool) {
        #expect(NameplateVisibility.automatic(mode: mode, shownOrbs: shownOrbs, tracksAll: tracksAll) == expected)
    }

    @Test func aSingleAllSourcesOrbLooksAsBefore() {
        #expect(!NameplateVisibility.isOn(Self.allSources, mode: .auto, shownOrbs: 1))
        #expect(!NameplateVisibility.isDrawn(Self.allSources, mode: .auto, shownOrbs: 1, isShown: true))
        let migrated = OrbConfig.makeDefault(id: "migrated")
        #expect(!NameplateVisibility.isDrawn(migrated, mode: .auto, shownOrbs: 1, isShown: true))
    }

    @Test func autoShowsASubsetOrbEvenAlone() {
        #expect(NameplateVisibility.isOn(Self.subset, mode: .auto, shownOrbs: 1))
        let pair = OrbConfig(id: "pair", sources: .explicit([.claudeCode, .claudeDesktop]))
        #expect(NameplateVisibility.isDrawn(pair, mode: .auto, shownOrbs: 1, isShown: true))
    }

    @Test func autoShowsEveryPlateWithTwoOrMoreShownOrbs() {
        for count in 2...8 {
            #expect(NameplateVisibility.isOn(Self.allSources, mode: .auto, shownOrbs: count))
            #expect(NameplateVisibility.isOn(Self.subset, mode: .auto, shownOrbs: count))
        }
    }

    @Test func showNameOverridesTheGlobalSetting() {
        for mode in OrbNamesMode.allCases {
            for count in [1, 2, 5] {
                for base in [Self.allSources, Self.subset] {
                    #expect(NameplateVisibility.isOn(Self.orb(base, showName: true), mode: mode, shownOrbs: count))
                    #expect(!NameplateVisibility.isOn(Self.orb(base, showName: false), mode: mode, shownOrbs: count))
                }
            }
        }
        #expect(NameplateVisibility.isDrawn(Self.orb(showName: true), mode: .never, shownOrbs: 1, isShown: true))
        #expect(!NameplateVisibility.isDrawn(Self.orb(Self.subset, showName: false), mode: .always, shownOrbs: 4, isShown: true))
    }

    @Test func aListOfOnlyUnknownKeysCountsAsAllSources() {
        let future = OrbConfig(id: "future", sources: .explicit(["future"]))
        #expect(NameplateVisibility.tracksAll(future))
        #expect(!NameplateVisibility.isOn(future, mode: .auto, shownOrbs: 1))
        let mixed = OrbConfig(id: "mixed", sources: .explicit(["codex", "future"]))
        #expect(!NameplateVisibility.tracksAll(mixed))
        #expect(NameplateVisibility.isOn(mixed, mode: .auto, shownOrbs: 1))
        #expect(NameplateVisibility.tracksAll(Self.allSources))
        #expect(!NameplateVisibility.tracksAll(Self.subset))
    }

    @Test(arguments: [
        (OrbStyleKind.minimal, 64.0, false),
        (.minimal, 72, false),
        (.minimal, 79.5, false),
        (.minimal, 80, true),
        (.minimal, 96, true),
        (.led, 64, false),
        (.reactor, 76, false),
        (.liquid, 80, true),
        (.led, 96, true),
        (.needle, 240, true),
    ])
    func neverDrawnBelow80Points(_ style: OrbStyleKind, _ size: Double, _ drawn: Bool) {
        let orb = OrbConfig(id: "o", sources: .explicit([.codex]), style: style, size: size, showName: true)
        #expect(orb.size == size)
        #expect(NameplateVisibility.isOn(orb, mode: .always, shownOrbs: 3), "the menus still show the setting as on")
        #expect(NameplateVisibility.isDrawn(orb, mode: .always, shownOrbs: 3, isShown: true) == drawn)
    }

    @Test func neverDrawnWhileTheOrbIsHidden() {
        for orb in [Self.orb(showName: true), Self.subset, Self.allSources] {
            #expect(!NameplateVisibility.isDrawn(orb, mode: .always, shownOrbs: 2, isShown: false))
        }
        #expect(NameplateVisibility.isDrawn(Self.allSources, mode: .always, shownOrbs: 2, isShown: true))
    }

    @Test func showNameDecodesAbsentOrUnknownAsAutomatic() throws {
        func decode(_ field: String) throws -> Bool? {
            let json = #"{"id": "a"\#(field)}"#
            return try JSONDecoder().decode(OrbConfig.self, from: Data(json.utf8)).showName
        }
        #expect(try decode("") == nil)
        #expect(try decode(#", "showName": null"#) == nil)
        #expect(try decode(#", "showName": true"#) == true)
        #expect(try decode(#", "showName": false"#) == false)
        #expect(try decode(#", "showName": "yes""#) == nil)
        #expect(try decode(#", "showName": 1"#) == nil)
        let encoded = try JSONEncoder().encode(Self.orb(showName: false))
        #expect(try JSONDecoder().decode(OrbConfig.self, from: encoded).showName == false)
    }
}
