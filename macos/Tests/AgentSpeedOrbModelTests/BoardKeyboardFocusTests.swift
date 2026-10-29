import Foundation
import Testing
@testable import AgentSpeedOrbModel

/// DESIGN 12 *Keyboard* on a board: the score rows take focus in visual order while keyboard navigation is on; Tab and
/// Shift-Tab wrap like the window's key view loop, the arrow keys stop at the ends like a list; Space shows the focused
/// row's card, Esc closes an open one; the focus keeps its row across ticks.
@Suite("Board keyboard focus")
struct BoardKeyboardFocusTests {
    static let rows = [
        BoardPlannedRow(kind: .group(0), key: "a", y: 0, height: 38),
        BoardPlannedRow(kind: .sub(group: 0, sub: 0), key: "a1", y: 38, height: 26),
        BoardPlannedRow(kind: .moreSubs(group: 0, count: 2, live: 5), key: "a#more", y: 64, height: 22),
        BoardPlannedRow(kind: .group(1), key: "b", y: 86, height: 38),
        BoardPlannedRow(kind: .group(2), key: "c", y: 124, height: 38),
        BoardPlannedRow(kind: .moreGroups(count: 2, live: 9), key: "#rest", y: 162, height: 22),
    ]

    static func focus(_ keys: [String] = ["a", "a1", "b", "c"]) -> BoardKeyboardFocus {
        var focus = BoardKeyboardFocus()
        focus.update(keys)
        return focus
    }

    @Test func scoreRowsTakeFocusInVisualOrderOnlyWithKeyboardNavigation() {
        #expect(BoardKeyboardFocus.keys(Self.rows, intelligence: true, keyboardNavigation: true) == ["a", "a1", "b", "c"],
                "group and sub rows top to bottom; the more lines take no focus")
        #expect(BoardKeyboardFocus.keys(Self.rows, intelligence: true, keyboardNavigation: false).isEmpty)
        #expect(BoardKeyboardFocus.keys(Self.rows, intelligence: false, keyboardNavigation: true).isEmpty,
                "with 性能 off no row shows a score")
        var off = BoardKeyboardFocus()
        off.update([])
        let space = off.handle(.space, cardShown: false), tab = off.handle(.tab(backward: false), cardShown: false)
        #expect(!off.isEnabled && space == nil && tab == nil)
        let focused = off.focus("a")
        #expect(!focused && off.key == nil)
    }

    @Test func tabWrapsLikeTheKeyViewLoopArrowsStopLikeAList() {
        var focus = Self.focus()
        let r1 = focus.enter()
        #expect(r1 == "a", "entering the board focuses its first row")
        #expect(focus.handle(.tab(backward: false), cardShown: false) == .moved("a1"))
        let r2 = focus.handle(.down, cardShown: false)
        #expect(r2 == .moved("b"))
        let r3 = focus.handle(.down, cardShown: false)
        #expect(r3 == .moved("c"))
        let r4 = focus.handle(.down, cardShown: false)
        #expect(r4 == BoardKeyboardFocus.Effect.none, "the arrows stop at the last row")
        #expect(focus.handle(.tab(backward: false), cardShown: false) == .moved("a"), "Tab wraps to the first row")
        let r5 = focus.handle(.up, cardShown: false)
        #expect(r5 == BoardKeyboardFocus.Effect.none, "the arrows stop at the first row")
        #expect(focus.handle(.tab(backward: true), cardShown: false) == .moved("c"), "Shift-Tab wraps to the last row")
        let r6 = focus.handle(.up, cardShown: false)
        #expect(r6 == .moved("b"))
        // Without a focused row, Tab starts at the first and Shift-Tab at the last.
        var fresh = Self.focus()
        #expect(fresh.handle(.tab(backward: true), cardShown: false) == .moved("c"))
        var down = Self.focus()
        let r7 = down.handle(.down, cardShown: false)
        #expect(r7 == .moved("a"))
    }

    @Test func spaceShowsTheFocusedCardEscClosesIt() {
        var focus = Self.focus()
        let r8 = focus.handle(.space, cardShown: false)
        #expect(r8 == .showCard("a"), "Space on a board just entered shows the first row's card")
        _ = focus.focus("b")
        let r9 = focus.handle(.space, cardShown: false)
        #expect(r9 == .showCard("b"))
        let r10 = focus.handle(.escape, cardShown: true)
        #expect(r10 == .closeCard)
        let r11 = focus.handle(.escape, cardShown: false)
        #expect(r11 == BoardKeyboardFocus.Effect.none, "Esc without a card does nothing")
        // A move with a card open reports the new row: the card follows it.
        let r12 = focus.handle(.down, cardShown: true)
        #expect(r12 == .moved("c"))
    }

    @Test func theFocusKeepsItsRowAcrossTicks() {
        var focus = Self.focus()
        _ = focus.focus("b")
        focus.update(["a", "a1", "c"])
        #expect(focus.key == "c", "a row that leaves passes the focus to the row now in its place")
        focus.update(["c", "a", "a1"])
        #expect(focus.key == "c", "a new ranking keeps the focus on its row")
        focus.update(["a"])
        #expect(focus.key == "a", "a shorter list falls back to its last row")
        focus.update([])
        #expect(focus.key == nil && !focus.isEnabled)
        var other = Self.focus()
        let stranger = other.focus("zz")
        #expect(!stranger, "only a row that takes focus can have it")
    }

    @Test func keysMapFromKeyCodes() {
        #expect(BoardKey(keyCode: 48, shift: false) == .tab(backward: false))
        #expect(BoardKey(keyCode: 48, shift: true) == .tab(backward: true))
        #expect(BoardKey(keyCode: 126, shift: false) == .up && BoardKey(keyCode: 125, shift: false) == .down)
        #expect(BoardKey(keyCode: 49, shift: false) == .space && BoardKey(keyCode: 53, shift: false) == .escape)
        #expect(BoardKey(keyCode: 36, shift: false) == nil, "Return is not the board's")
    }
}
