import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Orb editing")
struct EditingTests {
    static let detected = OrbAvailability(detected: [.gemini, .codex, .claudeCode])

    static func document(_ count: Int) -> OrbsDocument {
        OrbsDocument(orbs: (0..<count).map { OrbConfig(id: "o\($0)") })
    }

    @Test func availabilityIsInSourceOrder() {
        #expect(Self.detected.detected == [.codex, .claudeCode, .gemini])
        #expect(Self.detected.available(.all) == [.codex, .claudeCode, .gemini])
        #expect(Self.detected.available(.explicit([.gemini, .omp])) == [.gemini])
    }

    @Test func addCopiesTheTemplateAndPicksAnUntrackedSource() throws {
        var doc = OrbsDocument(orbs: [
            OrbConfig(id: "a", style: .needle, size: 196, opacity: 0.6, alwaysOnTop: false, clickThrough: true,
                      fadeWhenIdle: true, allSpaces: false, redlineOutput: 900),
            OrbConfig(id: "b", sources: .explicit([.codex])),
        ])
        doc = try #require(OrbEditing.add(to: doc, copying: "a", availability: Self.detected, newId: "n"))
        #expect(doc.orbs.map(\.id) == ["a", "n", "b"])
        let added = try #require(doc.orb("n"))
        #expect(added.sources == .explicit([.claudeCode]))
        #expect(added.style == .needle && added.size == 196 && added.opacity == 0.6)
        #expect(!added.alwaysOnTop && added.clickThrough && added.fadeWhenIdle && !added.allSpaces)
        #expect(added.name == nil && added.visible && added.redlineOutput == nil)
    }

    @Test func addFallsBackToAllWhenEverySourceIsTracked() throws {
        let doc = OrbsDocument(orbs: [OrbConfig(id: "a", sources: .explicit([.codex, .claudeCode, .gemini]))])
        let next = try #require(OrbEditing.add(to: doc, copying: nil, availability: Self.detected, newId: "n"))
        #expect(next.orb("n")?.sources == .all)
    }

    @Test func addAndDuplicateStopAtEight() {
        let full = Self.document(8)
        #expect(OrbEditing.add(to: full, copying: nil, availability: Self.detected, newId: "x") == nil)
        #expect(OrbEditing.duplicate("o0", in: full, newId: "x") == nil)
        #expect(OrbEditing.add(to: Self.document(7), copying: nil, availability: Self.detected, newId: "x")?.orbs.count == 8)
    }

    @Test func duplicateIsAnExactCopy() throws {
        let original = OrbConfig(id: "a", name: "Work", sources: .explicit([.codex]), style: .reactor, size: 112,
                                 opacity: 0.5, visible: false, redlineOutput: 700, redlineTotal: 90_000)
        let doc = try #require(OrbEditing.duplicate("a", in: OrbsDocument(orbs: [original, OrbConfig(id: "b")]), newId: "c"))
        #expect(doc.orbs.map(\.id) == ["a", "c", "b"])
        var expected = original
        expected.id = "c"
        expected.name = "Work 2"
        #expect(doc.orb("c") == expected)
    }

    @Test func theLastOrbCannotBeRemoved() throws {
        #expect(OrbEditing.remove("o0", from: Self.document(1)) == nil)
        #expect(OrbEditing.remove("missing", from: Self.document(2)) == nil)
        let doc = try #require(OrbEditing.remove("o0", from: Self.document(2)))
        #expect(doc.orbs.map(\.id) == ["o1"])
    }

    @Test func splitMakesOneOrbPerAvailableSource() throws {
        let orb = OrbConfig(id: "a", name: "Mine", sources: .all, style: .liquid, size: 196, redlineOutput: 600)
        let doc = OrbsDocument(orbs: [orb, OrbConfig(id: "z")])
        let result = try #require(OrbEditing.split("a", in: doc, availability: Self.detected, newIds: ["n1", "n2", "n3"]))
        #expect(result.document.orbs.map(\.id) == ["a", "n1", "n2", "z"])
        #expect(result.created == ["n1", "n2"])
        #expect(!result.limited)
        #expect(result.document.orbs.prefix(3).map(\.sources) == [.explicit([.codex]), .explicit([.claudeCode]), .explicit([.gemini])])
        for child in result.document.orbs.prefix(3) {
            #expect(child.style == .liquid && child.size == 196 && child.name == nil && child.redlineOutput == 600)
        }
    }

    @Test func splitOfAnExplicitSetUsesOnlyItsAvailableSources() throws {
        let doc = OrbsDocument(orbs: [OrbConfig(id: "a", sources: .explicit([.codex, .omp, .gemini]))])
        let result = try #require(OrbEditing.split("a", in: doc, availability: Self.detected, newIds: ["n1", "n2"]))
        #expect(result.document.orbs.map(\.sources) == [.explicit([.codex]), .explicit([.gemini])])
        #expect(OrbEditing.split("a", in: OrbsDocument(orbs: [OrbConfig(id: "a", sources: .explicit([.codex, .omp]))]),
                                 availability: Self.detected, newIds: ["n"]) == nil)
    }

    @Test func splitIsLimitedToEightOrbs() throws {
        var doc = Self.document(7)
        doc.orbs[0].sources = .all
        let result = try #require(OrbEditing.split("o0", in: doc, availability: Self.detected, newIds: ["n1", "n2"]))
        #expect(result.document.orbs.count == 8)
        #expect(result.created == ["n1"])
        #expect(result.limited)
        #expect(OrbEditing.split("o0", in: Self.document(8), availability: Self.detected, newIds: ["n1"]) == nil)
    }

    @Test func splitBySourceReplacesEveryOrb() throws {
        var doc = OrbsDocument(orbs: [OrbConfig(id: "first"), OrbConfig(id: "sel", style: .minimal, size: 80)])
        doc.setEntry(OrbLayoutEntry(display: "D", record: .init(x: .low(12), y: .low(12))), orb: "first", arrangement: "D")
        doc.setEntry(OrbLayoutEntry(display: "D", record: .init(x: .high(12), y: .low(12))), orb: "sel", arrangement: "D")
        let result = try #require(OrbEditing.splitBySource(doc, selectedId: "sel", availability: Self.detected, ids: ["x", "y"]))
        #expect(result.document.orbs.map(\.id) == ["first", "x", "y"])
        #expect(result.document.orbs.allSatisfy { $0.style == .minimal && $0.size == 80 })
        #expect(result.document.orbs.map(\.sources) == [.explicit([.codex]), .explicit([.claudeCode]), .explicit([.gemini])])
        #expect(result.document.entry(orb: "first", arrangement: "D") != nil)
        #expect(result.document.entry(orb: "sel", arrangement: "D") == nil)
        #expect(OrbEditing.splitBySource(doc, selectedId: nil, availability: OrbAvailability(detected: [.codex]), ids: ["x"]) == nil)
    }

    @Test func mergeKeepsTheChosenOrb() throws {
        var doc = OrbsDocument(orbs: [
            OrbConfig(id: "a", sources: .explicit([.codex])),
            OrbConfig(id: "b", name: "Mine", sources: .explicit([.gemini]), style: .reactor, size: 112, opacity: 0.8, clickThrough: true),
        ])
        doc.setEntry(OrbLayoutEntry(display: "D", record: .init(x: .low(1), y: .low(1))), orb: "a", arrangement: "D")
        doc.setEntry(OrbLayoutEntry(display: "D", record: .init(x: .high(1), y: .low(1))), orb: "b", arrangement: "D")
        let merged = try #require(OrbEditing.merge(doc, keeping: "b"))
        #expect(merged.orbs == [OrbConfig(id: "b", sources: .all, style: .reactor, size: 112, opacity: 0.8, clickThrough: true)])
        #expect(merged.entry(orb: "b", arrangement: "D") != nil)
        #expect(merged.entry(orb: "a", arrangement: "D") == nil)
        #expect(OrbEditing.merge(Self.document(1), keeping: "o0") == nil)
    }

    @Test func setStyleKeepsTheSize() {
        let doc = OrbsDocument(orbs: [OrbConfig(id: "a", style: .minimal, size: 64)])
        for style in OrbStyleKind.allCases {
            let orb = OrbEditing.setStyle(style, of: "a", in: doc).orbs[0]
            #expect(orb.style == style)
            #expect(orb.size == 64)
        }
        #expect(OrbEditing.setSize(30, of: "a", in: OrbEditing.setStyle(.needle, of: "a", in: doc)).orbs[0].size == 64)
        #expect(OrbEditing.setSize(300, of: "a", in: doc).orbs[0].size == 240)
    }

    @Test func smallMutators() {
        let doc = Self.document(2)
        #expect(OrbEditing.setOpacity(0.1, of: "o0", in: doc).orbs[0].opacity == 0.4)
        #expect(OrbEditing.setToggle(\.clickThrough, true, of: "o1", in: doc).orbs[1].clickThrough)
        #expect(!OrbEditing.setVisible(false, of: "o1", in: doc).orbs[1].visible)
        #expect(OrbEditing.setName("  Desk  ", of: "o0", in: doc).orbs[0].name == "Desk")
        #expect(OrbEditing.setName("", of: "o0", in: doc).orbs[0].name == nil)
        #expect(OrbEditing.setRedline(800, metric: .output, of: "o0", in: doc).orbs[0].redlineOutput == 800)
        #expect(OrbEditing.setRedline(.infinity, metric: .total, of: "o0", in: doc).orbs[0].redlineTotal == nil)
        #expect(OrbEditing.setSources(.explicit([.omp]), of: "o0", in: doc).orbs[0].sources == .explicit([.omp]))
        #expect(OrbEditing.setVisible(false, of: "missing", in: doc) == doc)
    }

    @Test func moveReordersLikeSwiftUI() {
        let doc = Self.document(4)
        #expect(OrbEditing.move(fromOffsets: [0], toOffset: 3, in: doc).orbs.map(\.id) == ["o1", "o2", "o0", "o3"])
        #expect(OrbEditing.move(fromOffsets: [3], toOffset: 0, in: doc).orbs.map(\.id) == ["o3", "o0", "o1", "o2"])
        #expect(OrbEditing.move(fromOffsets: [1, 2], toOffset: 4, in: doc).orbs.map(\.id) == ["o0", "o3", "o1", "o2"])
    }

    @Test func masterSwitchShowsAllWhenEveryOrbIsHidden() {
        var doc = Self.document(2)
        doc.orbs[0].visible = false
        #expect(OrbEditing.showAll(doc) == doc)
        doc.orbs[1].visible = false
        #expect(OrbEditing.showAll(doc).orbs.allSatisfy { $0.visible })
    }
}
