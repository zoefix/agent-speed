import Foundation
import Testing
@testable import AgentSpeedOrbModel

/// DESIGN 2c Data model `fadeWhenIdle`: off for a new board and for one saved by an earlier version, tolerant of a
/// value that is not a boolean, persisted with the board.
@Suite("Board fade when idle")
struct BoardFadeWhenIdleTests {
    static func decode(_ json: String) throws -> BoardConfig {
        try JSONDecoder().decode(BoardConfig.self, from: Data(json.utf8))
    }

    @Test func aBoardFromAnEarlierVersionDoesNotFade() throws {
        let stored = #"{"version":1,"boards":[{"id":"b","style":"neon","size":"M","sources":"all","opacity":0.8,"#
            + #""alwaysOnTop":true,"clickThrough":false,"allSpaces":true,"visible":true}]}"#
        let document = try #require(BoardsDocument(data: Data(stored.utf8)))
        let board = try #require(document.board("b"))
        #expect(!board.fadeWhenIdle)
        #expect(board.opacity == 0.8 && board.alwaysOnTop && board.visible)
    }

    @Test func aValueThatIsNotABooleanReadsAsOff() throws {
        for value in [#""yes""#, "1", "null", "[]", "{}"] {
            #expect(try !Self.decode(#"{"id":"b","fadeWhenIdle":\#(value)}"#).fadeWhenIdle)
        }
        #expect(try Self.decode(#"{"id":"b","fadeWhenIdle":true}"#).fadeWhenIdle)
    }

    @Test func aNewBoardDoesNotFade() throws {
        var document = BoardsDocument.empty
        let made = document.add(id: "b")
        let added = try #require(made)
        #expect(!added.fadeWhenIdle)
        #expect(!BoardConfig(id: "x").fadeWhenIdle)
    }

    @Test func theSwitchPersistsWithTheBoard() throws {
        var document = BoardsDocument.empty
        document.add(id: "a")
        document.add(id: "b")
        document.setFadeWhenIdle(true, of: "b")
        document.setFadeWhenIdle(true, of: "missing")
        let json = try #require(String(data: document.data(), encoding: .utf8))
        #expect(json.contains(#""fadeWhenIdle":true"#) && json.contains(#""fadeWhenIdle":false"#))
        let decoded = try #require(BoardsDocument(data: document.data()))
        #expect(decoded == document)
        #expect(decoded.board("a")?.fadeWhenIdle == false && decoded.board("b")?.fadeWhenIdle == true)
        document.setFadeWhenIdle(false, of: "b")
        #expect(document.board("b")?.fadeWhenIdle == false)
    }
}
