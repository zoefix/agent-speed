import AppKit

extension NSMenu {
    /// Opens the menu at a point on screen with no parent window, kept whole on the screen under the point.
    ///
    /// A menu shown for a view joins that view's window group. After a floating panel has been dragged to another
    /// display, macOS 27 can leave that group on no Space: every menu opened for the panel is then never drawn, and on
    /// some displays the panel leaves the screen with it until the app restarts. A menu without a parent is placed on
    /// the Space under the pointer on its own.
    func popUpDetached(at screenPoint: NSPoint, appearance: NSAppearance?) {
        self.appearance = appearance
        let screen = NSScreen.screens.first { NSMouseInRect(screenPoint, $0.frame, false) } ?? NSScreen.main
        let origin = screen.map { Self.detachedOrigin(size: size, at: screenPoint, within: $0.visibleFrame) } ?? screenPoint
        popUp(positioning: nil, at: origin, in: nil)
    }

    /// Where the menu's top-left corner goes: at the point, moved left and up as a context menu is when it would
    /// cross the right or bottom edge, instead of scrolling (DESIGN 2b, Context menu).
    nonisolated static func detachedOrigin(size: NSSize, at point: NSPoint, within visible: NSRect) -> NSPoint {
        let area = visible.insetBy(dx: detachedMargin, dy: detachedMargin)
        let x = max(area.minX, min(point.x, area.maxX - size.width))
        let y = min(area.maxY, max(point.y, area.minY + size.height))
        return NSPoint(x: x, y: y)
    }

    nonisolated static let detachedMargin: CGFloat = 6
}
