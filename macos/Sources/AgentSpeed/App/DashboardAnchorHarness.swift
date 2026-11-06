#if DEBUG
import AppKit

/// `AGENT_SPEED_DASHBOARD_ANCHOR=SECONDS` (development runs): opens the dashboard from a small anchor at the top right
/// of the main screen, for a menu bar that does not show this build's status item. The popover, its glass and its live
/// data are the real ones.
@MainActor
enum DashboardAnchorHarness {
    private static var anchor: NSWindow?
    private static var dashboard: DashboardController?

    static func show(model: AppModel) {
        guard let screen = NSScreen.main else { return }
        let frame = NSRect(x: screen.visibleFrame.maxX - 220, y: screen.visibleFrame.maxY - 2, width: 2, height: 2)
        let window = NSWindow(contentRect: frame, styleMask: .borderless, backing: .buffered, defer: false)
        window.isOpaque = false
        window.backgroundColor = .clear
        window.level = .statusBar
        window.orderFrontRegardless()
        anchor = window
        let controller = DashboardController(model: model)
        dashboard = controller
        if let view = window.contentView { controller.show(relativeTo: view) }
    }
}
#endif
