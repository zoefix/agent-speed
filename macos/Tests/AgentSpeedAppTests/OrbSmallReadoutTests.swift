import AgentSpeedOrbModel
import CoreGraphics
import Testing
@testable import AgentSpeed

/// DESIGN 2a, Shared foundation: below 96 pt no style draws a unit line, the title stays where it was and the money
/// value carries its symbol, so the 7 pt text floor never stacks the unit into the value or the title.
@Suite("Orb small readout")
@MainActor
struct OrbSmallReadoutTests {
    @Test func theUnitLineStartsAt96PointsInEveryStyleButMinimal() {
        for style in OrbStyleKind.allCases {
            let layout = OrbStyles.make(style).readoutLayout
            #expect((layout.at(diameter: 96).unitSize != nil) == (style != .minimal))
            #expect(layout.at(diameter: 92).unitSize == nil)
            #expect(layout.at(diameter: 64).unitSize == nil)
            #expect(OrbReadoutLayout.hasUnitLine(style: style, diameter: 96) == (style != .minimal))
            #expect(!OrbReadoutLayout.hasUnitLine(style: style, diameter: 92))
        }
    }

    @Test func theTitleKeepsItsPlaceWithoutTheUnit() {
        for style in OrbStyleKind.allCases {
            let layout = OrbStyles.make(style).readoutLayout
            #expect(abs(layout.at(diameter: 64).titleOffset - layout.titleOffset) < 1e-9)
            #expect(layout.at(diameter: 64).valueBaseline == layout.valueBaseline)
        }
    }
}
