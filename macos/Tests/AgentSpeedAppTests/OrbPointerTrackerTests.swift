import AppKit
import Testing
@testable import AgentSpeed

/// A press on the orb starts a drag that only a mouse-up, or a plain move proving the mouse-up was lost, ends. A move
/// the window server sent before the press can arrive after it while the button is still down; it must not count.
@Suite("Orb pointer tracker")
struct OrbPointerTrackerTests {
    @Test func aMoveWithNoButtonHeldIsPlain() {
        #expect(OrbWindowPointerTracker.isPlainMove(.mouseMoved, pressedButtons: 0))
    }

    @Test func aMoveArrivingWhileTheButtonIsDownIsNotPlain() {
        #expect(!OrbWindowPointerTracker.isPlainMove(.mouseMoved, pressedButtons: 1))
        #expect(!OrbWindowPointerTracker.isPlainMove(.mouseMoved, pressedButtons: 2))
    }

    @Test func dragsAndPressesAreNeverPlain() {
        for type in [NSEvent.EventType.leftMouseDragged, .leftMouseDown, .leftMouseUp, .rightMouseDown] {
            #expect(!OrbWindowPointerTracker.isPlainMove(type, pressedButtons: 0))
        }
    }
}
