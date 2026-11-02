import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Boards document")
struct BoardsDocumentTests {
    static let specExample = """
    { "version": 1,
      "boards": [ { "id": "B1", "style": "neon", "size": "M", "sources": "all", "opacity": 1.0, "alwaysOnTop": true,
                    "clickThrough": false, "fadeWhenIdle": false, "allSpaces": true, "visible": true } ],
      "layouts": { "D1+D2": { "B1": { "display": "D1", "xAnchor": "high", "x": 12, "yAnchor": "fraction", "y": 0.2 } } } }
    """

    @Test func decodesTheSpecExample() throws {
        let document = try #require(BoardsDocument(data: Data(Self.specExample.utf8)))
        #expect(document.version == 1)
        #expect(document.boards == [BoardConfig(id: "B1", style: .neon, size: .medium, sources: .all)])
        let entry = try #require(document.entry(board: "B1", arrangement: "D1+D2"))
        #expect(entry.display == "D1")
        #expect(entry.record == OrbPositionRecord(x: .high(12), y: .fraction(0.2)))
        #expect(document.layoutOrder == ["D1+D2"])
    }

    @Test func aFreshInstallHasNoBoard() {
        #expect(BoardsDocument.empty.boards.isEmpty)
        #expect(BoardsDocument(data: Data("{}".utf8)) == .empty)
        #expect(BoardsDocument(data: Data("[1, 2]".utf8)) == nil)
        #expect(BoardsDocument(data: Data("not json".utf8)) == nil)
    }

    @Test func unknownValuesFallBack() throws {
        let json = """
        { "boards": [ { "id": "a", "style": "hologram", "size": "XL", "sources": ["codex", "future"], "opacity": 0.1 },
                      { "id": "b", "style": "timing", "size": "L", "opacity": 7 },
                      { "id": "" }, { "style": "neon" } ], "lastStyle": "chrome" }
        """
        let document = try #require(BoardsDocument(data: Data(json.utf8)))
        #expect(document.boards.map(\.id) == ["a", "b"])
        let a = document.boards[0]
        #expect(a.style == .glass && a.size == .medium)
        #expect(a.sources == .explicit(["codex", "future"]))
        #expect(a.opacity == 0.4)
        #expect(a.alwaysOnTop && !a.clickThrough && !a.fadeWhenIdle && a.allSpaces && a.visible)
        #expect(document.boards[1].style == .timing && document.boards[1].size == .large && document.boards[1].opacity == 1)
        #expect(document.lastStyle == .glass)
    }

    @Test func atMostFourBoardsWithUniqueIDs() throws {
        let boards = ["a", "b", "a", "c", "d", "e"].map { #"{ "id": "\#($0)" }"# }.joined(separator: ",")
        let document = try #require(BoardsDocument(data: Data(#"{ "boards": [\#(boards)] }"#.utf8)))
        #expect(document.boards.map(\.id) == ["a", "b", "c", "d"])
        #expect(!document.canAdd)
    }

    @Test func addUsesTheLastStyleElseNeon() {
        var document = BoardsDocument.empty
        let first = document.add(id: "a")
        #expect(first == BoardConfig(id: "a", style: .neon, size: .medium, sources: .all))
        var restyled = document.boards[0]
        restyled.style = .timing
        restyled.size = .large
        document.update(restyled)
        #expect(document.lastStyle == .timing)
        #expect(document.add(id: "b")?.style == .timing)
        #expect(document.add(id: "b") == nil, "ids are never reused")
        #expect(document.add(id: "c") != nil && document.add(id: "d") != nil)
        #expect(document.add(id: "e") == nil, "four at most")
        let reloaded = BoardsDocument(data: document.data())
        #expect(reloaded == document)
    }

    @Test func layoutsArePerArrangementAndRemovedWithTheirBoard() {
        var document = BoardsDocument.empty
        document.add(id: "a")
        document.add(id: "b")
        let record = OrbPositionRecord(x: .low(12), y: .high(12))
        document.setEntry(OrbLayoutEntry(display: "D1", record: record), board: "a", arrangement: "D1")
        document.setEntry(OrbLayoutEntry(display: "D2", record: record), board: "a", arrangement: "D1+D2")
        document.setEntry(OrbLayoutEntry(display: "D1", record: record), board: "b", arrangement: "D1")
        document.setEntry(OrbLayoutEntry(display: "D1", record: record), board: "ghost", arrangement: "D1")
        #expect(document.layoutOrder == ["D1+D2", "D1"])
        #expect(document.displayRecords["a"]?.keys.sorted() == ["D1", "D2"])
        #expect(document.layouts["D1"]?["ghost"] == nil)
        document.remove("a")
        #expect(document.layouts["D1+D2"] == nil)
        #expect(document.layoutOrder == ["D1"])
        #expect(document.displayRecords["a"] == nil)
        #expect(document.entry(board: "b", arrangement: "D1") != nil)
    }

    @Test func atMostSixteenArrangements() {
        var document = BoardsDocument.empty
        document.add(id: "a")
        let record = OrbPositionRecord(x: .fraction(0.5), y: .fraction(0.5))
        for index in 0..<20 {
            document.setEntry(OrbLayoutEntry(display: "D\(index)", record: record), board: "a", arrangement: "A\(index)")
        }
        #expect(document.layouts.count == 16)
        #expect(document.layoutOrder.first == "A4" && document.layoutOrder.last == "A19")
    }

    @Test func arrangementChangesResolveLikeOrbs() {
        var document = BoardsDocument.empty
        document.add(id: "a")
        let record = OrbPositionRecord(x: .high(12), y: .high(12))
        document.setEntry(OrbLayoutEntry(display: "D2", record: record), board: "a", arrangement: "D1+D2")
        // D2 unplugged: the record moves to the main display, keeping its anchors.
        let entries = OrbLayoutResolver.entries(document: document, arrangement: "D1", previousArrangement: "D1+D2",
                                                connected: ["D1"], main: "D1")
        #expect(entries["a"] == OrbLayoutEntry(display: "D1", record: record))
    }
}
