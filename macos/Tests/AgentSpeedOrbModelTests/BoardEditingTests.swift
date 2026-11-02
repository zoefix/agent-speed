import AgentSpeedCore
import Foundation
import Testing
@testable import AgentSpeedOrbModel

@Suite("Board editing and menus")
struct BoardEditingTests {
    static func document(_ count: Int) -> BoardsDocument {
        var document = BoardsDocument.empty
        for index in 0..<count { document.add(id: "b\(index)") }
        return document
    }

    @Test func editsKeepTheDocumentValidAndPersist() throws {
        var document = Self.document(2)
        document.setStyle(.glass, of: "b0")
        document.setSize(.large, of: "b0")
        document.setOpacity(0.1, of: "b0")
        document.setAlwaysOnTop(false, of: "b1")
        document.setClickThrough(true, of: "b1")
        document.setFadeWhenIdle(true, of: "b1")
        document.setAllSpaces(false, of: "b1")
        document.setStyle(.timing, of: "missing")
        let b0 = try #require(document.board("b0"))
        #expect(b0.style == .glass && b0.size == .large && b0.opacity == 0.4 && !b0.fadeWhenIdle)
        #expect(document.lastStyle == .glass)
        let b1 = try #require(document.board("b1"))
        #expect(!b1.alwaysOnTop && b1.clickThrough && b1.fadeWhenIdle && !b1.allSpaces)
        let decoded = try #require(BoardsDocument(data: document.data()))
        #expect(decoded == document)
        let added = document.add(id: "b2")
        #expect(added?.style == .glass)
    }

    @Test func sourcesToggleLikeAnOrbs() throws {
        var document = Self.document(1)
        let single = document.toggleSource(.codex, of: "b0")
        #expect(single && document.board("b0")?.sources == .explicit([.codex]))
        let pair = document.toggleSource(.claudeCode, of: "b0")
        #expect(pair && document.board("b0")?.sources == .explicit([.claudeCode, .codex]))
        let back = document.toggleSource(.claudeCode, of: "b0")
        let lastOne = document.toggleSource(.codex, of: "b0")
        #expect(back && !lastOne)
        #expect(document.board("b0")?.sources == .explicit([.codex]))
        document.setSources(.all, of: "b0")
        #expect(document.board("b0")?.sources == .all)
    }

    @Test func showAndHideActOnEveryBoard() {
        var document = Self.document(3)
        document.setVisible(false, of: "b1")
        #expect(document.anyVisible)
        document.setAllVisible(false)
        #expect(!document.anyVisible)
        document.setAllVisible(true)
        #expect(document.boards.allSatisfy { $0.visible })
    }

    @Test func boardsReorderInTheList() {
        var document = Self.document(4)
        document.move(fromOffsets: [0], toOffset: 3)
        #expect(document.boards.map(\.id) == ["b1", "b2", "b0", "b3"])
        document.move(fromOffsets: [3], toOffset: 0)
        #expect(document.boards.map(\.id) == ["b3", "b1", "b2", "b0"])
    }

    @Test func theContextMenuFollowsTheBoard() throws {
        var document = Self.document(4)
        document.setSources(.explicit([.codex]), of: "b0")
        let board = try #require(document.board("b0"))
        let items = BoardMenuModel.context(board, document: document, status: [.gemini: "source.notFound"])
        #expect(items.first == .header(style: .neon, sources: .explicit([.codex])))
        #expect(items.contains(.styles(selected: .neon)))
        #expect(items.contains(.sizes(selected: .medium)))
        #expect(items.contains(.add(enabled: false)))
        #expect(items.contains(.toggle(.alwaysOnTop, on: true)) && items.contains(.toggle(.clickThrough, on: false)))
        guard case .sources(let all, let entries)? = items.first(where: { if case .sources = $0 { true } else { false } }) else {
            Issue.record("no sources item")
            return
        }
        #expect(!all)
        let codex = try #require(entries.first { $0.source == .codex })
        #expect(codex.checked && !codex.enabled)
        let gemini = try #require(entries.first { $0.source == .gemini })
        #expect(!gemini.checked && gemini.enabled && gemini.statusKey == "source.notFound")
        #expect(entries.map(\.source) == SourceKind.allCases)
    }

    @Test func theQuickMenuOffersShowHideAndAdd() {
        #expect(BoardMenuModel.quick(.empty) == [.add(enabled: true)])
        var document = Self.document(4)
        #expect(BoardMenuModel.quick(document) == [.visibility(anyVisible: true), .add(enabled: false)])
        document.setAllVisible(false)
        #expect(BoardMenuModel.quick(document) == [.visibility(anyVisible: false), .add(enabled: false)])
    }
}
