import AppKit
import Testing
@testable import AgentSpeed

/// DESIGN 2b, Context menu: a menu opened without a parent window keeps its top-left corner at the pointer, moved left
/// and up only as far as it takes to stay whole on the screen's visible area.
@Suite("Detached menu placement")
struct DetachedMenuTests {
    static let visible = NSRect(x: 2560, y: -256, width: 1440, height: 2530)
    static let size = NSSize(width: 222, height: 545)
    static let margin = NSMenu.detachedMargin

    @Test func staysAtThePointerWhenItFits() {
        let point = NSPoint(x: 3000, y: 1500)
        #expect(NSMenu.detachedOrigin(size: Self.size, at: point, within: Self.visible) == point)
    }

    @Test func movesUpAtTheBottomEdge() {
        let origin = NSMenu.detachedOrigin(size: Self.size, at: NSPoint(x: 2911, y: 96), within: Self.visible)
        #expect(origin.x == 2911)
        #expect(origin.y == Self.visible.minY + Self.margin + Self.size.height)
    }

    @Test func movesLeftAtTheRightEdge() {
        let origin = NSMenu.detachedOrigin(size: Self.size, at: NSPoint(x: 3950, y: 1500), within: Self.visible)
        #expect(origin.x == Self.visible.maxX - Self.margin - Self.size.width)
        #expect(origin.y == 1500)
    }

    @Test func staysBelowTheTopEdge() {
        let origin = NSMenu.detachedOrigin(size: Self.size, at: NSPoint(x: 3000, y: 2400), within: Self.visible)
        #expect(origin.y == Self.visible.maxY - Self.margin)
    }

    @Test func pinsTheTopWhenTallerThanTheScreen() {
        let tall = NSSize(width: 222, height: 3000)
        let origin = NSMenu.detachedOrigin(size: tall, at: NSPoint(x: 3000, y: 100), within: Self.visible)
        #expect(origin.y == Self.visible.maxY - Self.margin)
    }

    @Test func movesUpAndLeftInTheBottomRightCorner() {
        let origin = NSMenu.detachedOrigin(size: Self.size, at: NSPoint(x: 3990, y: -250), within: Self.visible)
        #expect(origin == NSPoint(x: Self.visible.maxX - Self.margin - Self.size.width,
                                  y: Self.visible.minY + Self.margin + Self.size.height))
    }
}
